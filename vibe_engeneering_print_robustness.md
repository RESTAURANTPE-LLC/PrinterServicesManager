# Vibe Engineering — Robustez de impresión y monitoreo (sesión 2026-04-21)

> Documenta los problemas detectados y los fixes aplicados sobre `PrinterServices`
> en una sola sesión de trabajo, con contexto suficiente para debugging futuro.
> Todos los fixes son acumulativos y compatibles entre sí.

---

## 1. Resumen ejecutivo

Cinco problemas distintos atacados, en orden cronológico de descubrimiento:

| # | Síntoma reportado | Causa raíz | Fix |
|---|---|---|---|
| 1 | Tickets con mucho negro se cruzan en el papel (uno trunca, sigue otro) | `PrintWorker` marca `DONE` apenas el TCP `SendAsync` vuelve, pero la impresora física tarda mucho más por throttling térmico → el siguiente job manda `ESC @` mientras la anterior sigue imprimiendo, la resetea y los tickets se pisan | **Post-print wait** proporcional a la densidad de negro del bitmap (heurística calibrable) |
| 2 | Transiciones `OFFLINE→ONLINE` cada ~6s para PRINTER_1; tabla de impresoras muestra OFFLINE cuando StatusMonitor justo lo puso ONLINE | `SyncPrinters._db.Update(existing)` reescribe TODA la fila con valores stale (incluido `estado_online`), pisando lo que `StatusMonitor` acababa de actualizar | **UPDATE selectivo** por SQL directo en SyncPrinters/RegisterPrinter/MacEnricher; **catch de StatusMonitor** ya no resetea `EstadoOnline=0` ante excepciones |
| 3 | Tickets cortados en puntos aleatorios (a veces casi nada se imprime, a veces falta la cabecera) | StatusMonitor + PrintWorker pre-check + PrintWorker send abren conexiones TCP concurrentes a port 9100 — impresoras ESC/POS económicas mezclan los bytes de ambos sockets en su buffer interno | **Port lock por IP** — semáforo que serializa todo acceso TCP al puerto de cada impresora |
| 4 | `UNIQUE constraint failed: printers.mac_address` en logs durante SyncPrinters | Race entre el SELECT de dedup y el INSERT/UPDATE — `PrinterMacEnricher` corre en paralelo y se queda con la MAC antes de que `SyncPrinters` haga su Insert | **Catch específico de la violación UNIQUE** + recovery: actualiza la fila ganadora con los datos del dto en lugar de propagar excepción |
| 5 | Reporte de Conectividad solo dice "OFFLINE / ONLINE" sin explicar por qué | `printer_status_log` no guardaba la respuesta cruda de la impresora ni una decodificación humana | **2 columnas nuevas** (`printer_response` raw + `printer_response_legend` decodificado) + decoder DLE EOT + UI actualizada |

Bonus diagnósticos descartados durante el camino:
- ❌ **LockBits 32→1bpp corrupta filas finales** — descartado: cortes son aleatorios, no consistentes por altura del bitmap.
- ❌ **`PostPrintWaitMaxMs=4000` insuficiente** — descartado: mismo síntoma con o sin wait, prueba que la causa estaba en otro lado.

---

## 2. Problemas y fixes en detalle

### 2.1 Post-print wait (cruce de tickets con mucho negro)

**Síntoma**: imprimir un cuadrado negro de ~3 cm en una térmica ESC/POS (con `BitmapEmulacion=escasterisc`, especialmente). El primer ticket queda truncado, el segundo sale completo debajo en el mismo papel.

**Causa raíz**:
- `TcpTransport.SendAsync` devuelve apenas los bytes salen del NIC (~50–200 ms para un bitmap chico).
- La impresora térmica tarda 500–1500 ms+ en imprimir físicamente cuando hay alta densidad de negro (throttling para no quemar el cabezal).
- `PrintWorker.SendWithRetryInstrumented` no espera al fin físico, marca el job `DONE` y libera al worker.
- El siguiente job abre una nueva conexión TCP, manda `ESC @` (Init), y muchos modelos se resetean cortando el ticket en curso por la mitad.

**Fórmula del wait**:
```
estimatedMs = height * BaseMsPerRow + Σ(darkRatio_por_fila) * ExtraMsPerBlackRow
```
Calculada vía `LockBits` rápido sobre el bitmap nativo (32bpp/24bpp). Clampada a `[PostPrintWaitMinMs, PostPrintWaitMaxMs]`.

**Configuración** (categoría `timing`, ajustable en caliente vía `/api/config/sync` o SQL Console):

| Key | Default | Descripción |
|---|---|---|
| `PostPrintWaitEnabled` | `1` | Master switch |
| `PostPrintWaitBaseMsPerRow` | `2` | ms por fila clara (referencia: ~150 mm/s a 8 dots/mm) |
| `PostPrintWaitExtraMsPerBlackRow` | `10` | ms extra por fila 100% negra (compensa throttling) |
| `PostPrintWaitMinMs` | `100` | Piso para bitmaps chicos |
| `PostPrintWaitMaxMs` | `4000` | Techo de seguridad |

**Archivos**:
- `Rendering/PrintDurationEstimator.cs` *(nuevo)* — helper estático `EstimateMs(Bitmap)`.
- `Workers/PrintWorker.cs` — `BuildPayload` retorna `BuiltPayload { Data, EstimatedWaitMs }`; nuevo helper `WaitForPhysicalPrintAsync` se llama desde `SendWithRetryInstrumented`/`SendUsbWithRetry` tras cada `SendAsync` exitoso.
- `Config/ConfigManager.cs` — 5 keys default.
- `PrinterServices.csproj` — registro del .cs nuevo.

---

### 2.2 Race condition en SyncPrinters / MacEnricher (flapping OFFLINE↔ONLINE)

**Síntoma**: el reporte de conectividad mostraba `OFFLINE→ONLINE Conectividad restaurada` cada ~6 s para PRINTER_1, sin la transición inversa correspondiente. La tabla de impresoras mostraba OFFLINE aunque el printer físicamente estaba online.

**Causa raíz**:
1. `StatusMonitor` actualiza `EstadoOnline=1` correctamente.
2. `QuipuNetX → SyncPrinters` corre periódicamente y hace `_db.Update(existing)`.
3. `existing` fue leído antes con `SELECT *` → en RAM tiene `EstadoOnline=0` (stale).
4. `_db.Update(entity)` de sqlite-net **reescribe toda la fila**, pisando `EstadoOnline` con el valor stale.
5. Próximo ciclo de StatusMonitor: `wasOnline=false` → check pasa → loggea transición OFFLINE→ONLINE → loop infinito.

Nunca aparece la transición ONLINE→OFFLINE porque el reset a `0` lo hace `SyncPrinters` silenciosamente, sin pasar por `LogStatusTransition`.

**Fix**: reemplazar todos los `_db.Update(entity)` de SyncPrinters/RegisterPrinter/MacEnricher por `_db.Execute("UPDATE ... SET <campos> WHERE impresora_id = ?")` con SOLO los campos que ese flujo tiene derecho a tocar (`ip`, `nombre`, `puerto`, `mac_address`, `modelo`, `modo_impresion`, `tipo_conexion`, `usb_*`).

**Campos PROHIBIDOS para SyncPrinters/MacEnricher** (los maneja exclusivamente StatusMonitor):
- `estado_online`
- `disponible_para_imprimir`
- `tiene_papel`
- `tapa_abierta`
- `ultimo_check`
- `snmp_enabled`
- `snmp_community`
- `ip_resuelta_por_arp`
- `fecha_registro` (solo en INSERT)

**Bug colateral arreglado**: `StatusMonitor.CheckAllPrintersAsync` catch (línea ~584) hacía `printer.EstadoOnline = 0; _db.Update(printer)` ante CUALQUIER excepción → ocultaba fallas reales del UPDATE y generaba flap "fantasma" (excepción en update → catch marca offline → siguiente ciclo loggea OFFLINE→ONLINE). Ahora **no toca `EstadoOnline`**, solo actualiza `ultimo_check` con UPDATE selectivo y loguea el stack trace completo.

**Archivos**:
- `Api/Controllers/PrinterController.cs` — 5 call-sites de SyncPrinters/RegisterPrinter/EditPrinter convertidos a UPDATE selectivo.
- `Services/Printers/PrinterMacEnricher.cs` — 3 call-sites (TryEnrichMac dedupe + asignación + TryNormalizeMac) convertidos a UPDATE selectivo.
- `Monitoring/StatusMonitor.cs` — catch ya no resetea estado, loggea con stack trace.

---

### 2.3 Port lock TCP por IP (cortes aleatorios mid-print)

**Síntoma**: tickets cortados en puntos aleatorios. A veces sale casi nada (solo "Operación: 4110056" muy tenue), a veces falta la cabecera completa pero está N° y MESA (imagen 13 sin "Operación:"), a veces sale hasta MESA + AREA y se corta.

**Causa raíz**: tres rutas distintas abren conexiones TCP a port 9100 de la misma impresora **sin coordinación**:

1. `PrintWorker.ProcessJobAsync:306` → pre-check DLE EOT.
2. `PrintWorker.SendWithRetryInstrumented` → bitmap completo (puede tardar 1–5 s).
3. `StatusMonitor.CheckAllPrintersAsync` → check periódico cada `StatusCheckIntervalSeconds` (default 2–15s).

Las impresoras ESC/POS económicas (y emulaciones no-Epson como CUSTOM/POS) suelen aceptar múltiples conexiones simultáneas y **serializar los bytes en el orden de llegada desde cualquier socket**. El byte `0x10` de un DLE EOT (`0x10 0x04 0x0N`) inyectado en medio del raster `GS v 0` puede ser tomado como real-time command por la impresora, abortando la rasterización en cualquier punto.

Imagen 13 (sin "Operación:" pero con N°) es la prueba reina: empezar a imprimir desde el medio del bitmap solo se explica si bytes iniciales fueron consumidos como real-time commands o perdidos por interleave de sockets.

**Fix**: nuevo `PrinterPortLock` (semáforo por IP). TODO acceso TCP al puerto de impresión adquiere primero:

| Ruta | Método de adquisición | Comportamiento si lock ocupado |
|---|---|---|
| PrintWorker (pre-check + send + wait) | `AcquireAsync(ip, ct)` | Espera indefinida (con cancellation) |
| StatusMonitor background | `TryAcquireAsync(ip, timeoutMs, ct)` | Skipea check de este ciclo, retry en el próximo |

El lock se mantiene durante todo el flujo del print (pre-check + BuildPayload + send loop + WaitForPhysicalPrintAsync), incluyendo el wait post-print. Así el StatusMonitor no puede inyectar bytes ni siquiera durante la ventana en que el cabezal está físicamente imprimiendo.

**Implementación** (`Core/Network/PrinterPortLock.cs`):
- `ConcurrentDictionary<string, SemaphoreSlim>` keyed por IP normalizada (lowercase trim).
- Semáforos creados lazy via `GetOrAdd`.
- Handle `IDisposable` con `Interlocked.Exchange` para release idempotente.
- `NoOp` handle estático para IPs vacías (no-op safe).

**Configuración**:

| Key | Default | Descripción |
|---|---|---|
| `StatusCheckPortLockTimeoutMs` | `1500` | Timeout que espera el StatusMonitor antes de skipear el ciclo |

**Archivos**:
- `Core/Network/PrinterPortLock.cs` *(nuevo)*
- `Workers/PrintWorker.cs` — `ProcessJobAsync` envuelve pre-check+build+send en `try { ... } finally { portLock.Dispose(); }`.
- `Monitoring/StatusMonitor.cs` — `CheckAllPrintersAsync` rama RED hace `TryAcquireAsync`, `continue` si timeout.
- `Config/ConfigManager.cs` — key default.
- `PrinterServices.csproj` — registro del .cs nuevo.

**Nota importante**: el lock es por IP, no por dispositivo físico. Dos impresoras distintas no se bloquean entre sí. Si la misma impresora física tiene dos IPs lógicas (caso multi-homed o duplicado de registro), cada IP tiene su propio lock — es responsabilidad del catálogo de printers tener una sola fila por dispositivo (ver §2.4).

---

### 2.4 UNIQUE constraint failed: printers.mac_address (race recovery)

**Síntoma**: spam en logs:
```
ERROR PrinterController - [PRINTER-SYNC] Error durante sincronización
PSQLite.SQLiteException: UNIQUE constraint failed: printers.mac_address
```

**Causa raíz**: race entre el SELECT de dedup en SyncPrinters y el INSERT/UPDATE final. `PrinterMacEnricher` corre desde StatusMonitor en otro hilo y se queda con la MAC antes de que SyncPrinters termine su iteración.

**Fix**: catch específico via `IsMacUniqueViolation(ex)` (match por mensaje, no por tipo, para tolerar diferencias de versión de PSQLite). En el catch, `TryRecoverMacRace(mac, dto)` busca la fila ganadora (la que retiene la MAC) y aplica los datos del dto vía UPDATE selectivo. Resultado consistente sin propagar excepción ni dejar el catálogo en estado raro.

**Antes**:
```
ERROR ... UNIQUE constraint failed: printers.mac_address
   ...stack trace ruidoso...
```

**Después**:
```
WARN ... [PRINTER-SYNC] Race MAC AABBCC...: ya estaba tomada por 9 (QA) entre dedup-check y operación → datos del dto 10 aplicados a 9
```

**Bonus problema relacionado — MAC en distintos formatos bypassea el UNIQUE**:
La constraint `idx_printers_mac_unique` es sobre el string literal de `mac_address`. Si una fila tiene `"50579C089E6F"` (sin separadores) y otra `"50:57:9C:08:9E:6F"` (con `:`), SQLite las ve como strings distintas → ambas conviven en BD.

`PhysicalDeviceGrouper` después las normaliza a `50579C089E6F` y las agrupa como mismo dispositivo físico → solo chequea una y propaga estado → si esa "una" da OFFLINE (porque su IP es vieja/stale), arrastra a la otra a OFFLINE aunque su IP funcione perfecto.

**Mitigación inmediata** (manual, vía SQL Console):
```sql
-- Detectar MACs duplicadas en distintos formatos
SELECT impresora_id, nombre, ip, mac_address,
       REPLACE(REPLACE(REPLACE(UPPER(mac_address), ':', ''), '-', ''), '.', '') AS mac_norm,
       estado_online
FROM printers
WHERE mac_address IS NOT NULL AND mac_address != ''
ORDER BY mac_norm;

-- Borrar la fila stale (la que tiene IP vieja)
DELETE FROM printers WHERE impresora_id = '<id_stale>';

-- Normalizar la MAC de la sobreviviente
UPDATE printers
SET mac_address = REPLACE(REPLACE(REPLACE(UPPER(mac_address), ':', ''), '-', ''), '.', '')
WHERE impresora_id = '<id_sobreviviente>';
```

**Mitigación pendiente en código** (no aplicada en esta sesión, ver §6):
- Normalizar `dto.mac_address` con `MacAddressNormalizer.Normalize()` ANTES de cualquier SELECT/INSERT/UPDATE en SyncPrinters/RegisterPrinter.
- Migración auto al arranque que detecte y consolide duplicados por MAC normalizada.

**Archivos**:
- `Api/Controllers/PrinterController.cs` — `IsMacUniqueViolation` + `TryRecoverMacRace` helpers; wrappers en INSERT main path + UPDATE main path.

---

### 2.5 Reporte de Conectividad con DLE EOT y leyenda

**Síntoma**: el reporte mostraba `OFFLINE → ONLINE Conectividad restaurada` sin explicar **qué** dijo la impresora exactamente al momento del check. Imposible diagnosticar por qué bajó/subió.

**Fix**: 2 columnas nuevas en `printer_status_log`:

| Columna | Tipo | Ejemplo |
|---|---|---|
| `printer_response` | TEXT | `"P:12 O:00 E:00 S:00"`, `"OFFLINE:Timeout"`, `"P:00 O:00 E:00 S:00 [TCP_OK_NO_DLE]"` |
| `printer_response_legend` | TEXT | `"Lista para imprimir (online, con papel, tapa cerrada, sin errores)"` |

**Decoder DLE EOT** (`PrinterStatusChecker.Explain(rawStatus)` — método estático público):

| Entrada | Leyenda |
|---|---|
| `null` o vacío | "Sin respuesta de la impresora" |
| `OFFLINE:<msg>` | "Impresora no alcanzable por TCP (port 9100): \<msg\>" |
| `...[TCP_OK_NO_DLE]` | "TCP responde pero la impresora NO contesta DLE EOT (puede ser un print server, un dispositivo en port 9100 que no es ESC/POS, o un modelo que no soporta DLE EOT)" |
| `...[TCP_OK_DLE_INVALIDO]` | "TCP responde pero DLE EOT devolvió bytes que no cumplen la máscara Epson" |
| `P:12 O:00 E:00 S:00` | "Lista para imprimir (online, con papel, tapa cerrada, sin errores)" |
| `P:12 O:04 E:00 S:00` | "Problemas: tapa abierta (O bit 2)" |
| `P:12 O:00 E:00 S:60` | "Problemas: sin papel o por terminarse (S bits 5,6)" |

Bits decodificados (Epson TM estándar):
- **P** (DLE EOT 1): bit 3 = no lista
- **O** (DLE EOT 2): bit 2 = tapa abierta, bit 5 = parado por papel/error
- **E** (DLE EOT 3): bit 6 = error recuperable, bit 5 = error NO recuperable, bit 3 = error de corte automático
- **S** (DLE EOT 4): bits 5,6 = sin papel o por terminarse

**Archivos**:
- `Data/Models/PrinterStatusLogEntity.cs` — 2 columnas nuevas.
- `Data/PrinterServiceDb.cs` — migración auto: `PRAGMA table_info` + `ALTER TABLE ADD COLUMN` solo si no existe.
- `Monitoring/PrinterStatusChecker.cs` — `Explain()` público + `TryParseDleEot()` privado.
- `Monitoring/StatusMonitor.cs` — `LogStatusTransition` recibe `printerResponseRaw`, calcula leyenda y persiste; los 4 call-sites pasan `status.RawStatus`.
- `Api/Controllers/DashboardController.cs` — JSON incluye `printerResponse` + `printerResponseLegend`.
- `Resources/dashboard.html` — tabla del Reporte de Conectividad: 2 columnas nuevas con tooltip.

---

## 3. Archivos modificados / creados (consolidado)

### Nuevos (3)
| Archivo | Propósito |
|---|---|
| `Rendering/PrintDurationEstimator.cs` | Estima ms de impresión física desde un Bitmap (densidad de negro) |
| `Core/Network/PrinterPortLock.cs` | Mutex per-IP que serializa TCP a port 9100 |
| `vibe_engeneering_postprint_wait.md` | Doc canónica del fix de post-print wait (subset de este doc) |
| `vibe_engeneering_print_robustness.md` | **Este doc** (referencia consolidada) |

### Modificados (8)
| Archivo | Qué cambió |
|---|---|
| `Workers/PrintWorker.cs` | Post-print wait + port lock + `BuildPayload` retorna `BuiltPayload` |
| `Monitoring/PrinterStatusChecker.cs` | Helper público `Explain` + `TryParseDleEot` |
| `Monitoring/StatusMonitor.cs` | Catch sin reset de estado + port lock + LogStatusTransition con DLE EOT |
| `Api/Controllers/PrinterController.cs` | UPDATE selectivo en SyncPrinters/RegisterPrinter/EditPrinter + helpers `IsMacUniqueViolation`/`TryRecoverMacRace` |
| `Api/Controllers/DashboardController.cs` | Endpoint connectivity expone `printerResponse` + `printerResponseLegend` |
| `Services/Printers/PrinterMacEnricher.cs` | UPDATE selectivo en TryEnrichMac/TryNormalizeMac |
| `Data/Models/PrinterStatusLogEntity.cs` | +2 columnas |
| `Data/PrinterServiceDb.cs` | Migración auto de las 2 columnas en printer_status_log |
| `Config/ConfigManager.cs` | +6 keys default (5 timing + 1 status) |
| `Resources/dashboard.html` | Tabla connectivity con 2 columnas nuevas |
| `PrinterServices.csproj` | +2 Compile Include |

---

## 4. Configuración nueva (consolidado)

Categoría `timing`:

| Key | Default | Mín | Máx | Descripción |
|---|---|---|---|---|
| `PostPrintWaitEnabled` | `1` | - | - | Master switch del wait post-send |
| `PostPrintWaitBaseMsPerRow` | `2` | `0` | `50` | ms por fila clara |
| `PostPrintWaitExtraMsPerBlackRow` | `10` | `0` | `100` | ms extra por fila 100% negra |
| `PostPrintWaitMinMs` | `100` | `0` | `10000` | Piso de la espera |
| `PostPrintWaitMaxMs` | `4000` | `0` | `60000` | Techo de seguridad |
| `StatusCheckPortLockTimeoutMs` | `1500` | `100` | `30000` | Timeout que espera StatusMonitor por el lock |

Todos tocables en caliente vía SQL Console o `POST /api/config/sync`.

### Calibración recomendada

| Síntoma | Ajuste |
|---|---|
| Tickets muy negros aún se cruzan | Subir `PostPrintWaitExtraMsPerBlackRow` a `12-15` |
| Latencia innecesaria entre jobs sin bitmap pesado | Bajar `PostPrintWaitBaseMsPerRow` a `1` |
| Tickets MUY largos con mucho negro siguen truncándose | Subir `PostPrintWaitMaxMs` |
| Logs con muchos `port lock ocupado, check salteado` | Bajar `StatusCheckIntervalSeconds` o subir `StatusCheckPortLockTimeoutMs` |
| Modelo específico con problemas crónicos | Cambiar `BitmapEmulacion` de `escasterisc` → `escpos` |

---

## 5. Migraciones BD

### `printer_status_log` (auto al arranque)
```sql
ALTER TABLE printer_status_log ADD COLUMN printer_response TEXT;
ALTER TABLE printer_status_log ADD COLUMN printer_response_legend TEXT;
```
Ejecutado por `PrinterServiceDb.CreateTables()` con check previo de `PRAGMA table_info`. Idempotente. Filas viejas quedan con `NULL` en las nuevas columnas; el dashboard renderiza `-`.

### Limpieza manual recomendada (NO automática)
```sql
-- Detectar MACs duplicadas en distintos formatos
SELECT impresora_id, nombre, ip, mac_address,
       REPLACE(REPLACE(REPLACE(UPPER(mac_address), ':', ''), '-', ''), '.', '') AS mac_norm
FROM printers
WHERE mac_address IS NOT NULL
ORDER BY mac_norm;

-- Si hay duplicados: borrar el stale, normalizar el sobreviviente
DELETE FROM printers WHERE impresora_id = '<stale>';
UPDATE printers
SET mac_address = REPLACE(REPLACE(REPLACE(UPPER(mac_address), ':', ''), '-', ''), '.', '')
WHERE mac_address IS NOT NULL;
```

---

## 6. Limitaciones conocidas y siguientes pasos

### Implementadas pero con espacio para mejorar

1. **Post-print wait es heurística, no confirmación real**. Para máxima precisión: polling DLE EOT post-send hasta que la impresora reporte idle. Más complejo, requiere confirmar que cada modelo de impresora soporta consultas durante print. Implementarlo solo si el wait heurístico no alcanza.

2. **Port lock es por IP, no por dispositivo físico**. Si la misma impresora física tiene varias filas (mismo MAC, distinto `impresora_id`), cada una tiene su propio lock — permite acceso TCP concurrente al hardware. Solución: garantizar una sola fila por MAC normalizada en el catálogo.

### No aplicadas en esta sesión (recomendadas para próxima)

3. **Normalizar `dto.mac_address` en SyncPrinters/RegisterPrinter ANTES de cualquier SELECT/INSERT/UPDATE**. Hoy `MacAddressNormalizer` solo se usa en MacEnricher y dedup-de-batch. Si el front manda MAC con `:` y otra sin, ambas pasan a BD. La constraint UNIQUE no las dedupea (strings distintos).

4. **Migración auto al arranque que detecte duplicados por MAC normalizada y consolide**: NULLear MAC de los más viejos (`fecha_registro` ASC), normalizar el sobreviviente, después intentar el UNIQUE INDEX.

5. **Wrappers `IsMacUniqueViolation`/`TryRecoverMacRace` también en `RegisterPrinter`** (endpoint manual desde el dashboard). Misma estructura de bug, frecuencia mucho menor — quedó pendiente.

6. **`PostPrintWaitEnabled` por impresora**, no global. Hoy aplica a todas. Útil cuando algunos modelos se benefician más que otros del wait.

### Diagnósticos que NO necesitan código

7. **Verificar TCP 9100 manualmente** cuando una impresora aparece OFFLINE pero ping funciona:
   ```powershell
   Test-NetConnection 192.168.x.x -Port 9100
   arp -a 192.168.x.x          # ver qué MAC responde realmente
   ```

8. **Confirmar que el port lock está funcionando** ante sospecha de cortes mid-print:
   ```
   netstat -an | findstr 9100   # debería haber máx 1 ESTABLISHED por IP de impresora
   ```
   En logs: buscar `[MONITOR] ... port lock ocupado, check salteado este ciclo` — su presencia ocasional es normal y benigna.

---

## 7. Glosario rápido

- **DLE EOT**: comando ESC/POS de status real-time. 4 sub-comandos (n=1,2,3,4) que devuelven 1 byte cada uno con bits significativos del estado físico de la impresora.
- **Raw status**: string concatenado `"P:XX O:XX E:XX S:XX"` que persiste el resultado del DLE EOT.
- **Throttling térmico**: la térmica baja velocidad cuando el cabezal sobrepasa cierta temperatura (alta densidad de negro). Hace que el tiempo físico de impresión sea muy mayor al tiempo de TCP send.
- **GS v 0**: comando ESC/POS de raster bitmap estándar Epson. Header + height + width + bytes 1bpp.
- **ESC \***: comando ESC/POS de bit image por bandas de 24 dots. ~3× más bytes que GS v 0, más compatible con emulaciones no-Epson.
- **Port lock**: semáforo per-IP que serializa todo acceso TCP al puerto de impresión. Implementado en `PrinterPortLock.cs`.
- **Race recovery**: detección de UNIQUE constraint y recovery automático de race entre SELECT-de-dedup y INSERT/UPDATE.
