# Vibe Engineering — Diagnóstico de falso positivo DONE-sin-imprimir

> **Fecha**: 2026-04-21
> **Caso gatillo**: Job `ad99c742a37f` en PRINTER_2_EPSON (Epson TM-T20IIIL M352A, 192.168.10.125:9100), BARRA SUSHI.
> Secuencia observada en el Historial: `FAILED (19:37:26) → RETRIED (19:37:35) → DONE (19:37:40)`.
> Modal del job muestra `Estado DONE`, `Reintentos 1/3`, `DLE EOT: P:16 O:12 E:12 S:12`, `Error: Sin errores`.
> **Realidad física**: el papel nunca salió.
>
> **Propósito del plan**: cerrar la brecha entre "PrinterServices dice DONE" y "la impresora físicamente imprimió". Sin esto, el proyecto pierde su razón de ser (no generar falsos positivos).

---

## 1. Por qué hoy es un falso positivo

Gap por gap, medido contra el código actual (referencias a archivos y líneas verificadas):

| # | Gap de diagnóstico | Evidencia en código | Consecuencia en este caso |
|---|---|---|---|
| 1 | **No hay DLE EOT post-send.** Solo se consulta el estado ANTES de enviar. | `PrintWorker.cs:318-323` (pre-check) — no existe post-check | Si el papel se acaba, el cabezal se traba o se abre la tapa DURANTE la impresión, el worker no se entera. TCP `SendAsync` devuelve OK y el job se marca DONE. |
| 2 | **`printer_response` se sobreescribe por intento.** No hay tabla de intentos. | `PrintWorker.cs:326-327` `UPDATE print_jobs SET printer_response = ? WHERE job_id = ?` | En el modal solo vemos el DLE del ÚLTIMO intento (el "exitoso"). El DLE del intento FAILED se perdió — justo el que explicaría por qué falló. |
| 3 | **"Duración" en el Historial siempre sale `-`.** | `DashboardController.cs:196` `duracionMs = 0, // TODO: Calcular de timestamps` | Es imposible distinguir "impresión de 3 s" (normal) de "send instantáneo sin impresión" (sospechoso). El campo ya está en el JSON pero nunca se calculó. |
| 4 | **Port lock contention invisible.** | `PrinterPortLock.cs:44-71` `sem.WaitAsync(ct)` sin timing ni log | Si un StatusMonitor + un retry + un ArpScan pelean por el port, el job espera en silencio. No sabemos si la latencia anómala vino del lock. |
| 5 | **Post-print wait es heurístico, no medido.** | `PrintWorker.cs:987-998` solo loggea el estimado a DEBUG; no persiste duración real | Si el estimador dio `100 ms` pero la impresora necesitaba `2000 ms`, el siguiente job pisa al anterior — y no queda huella. |
| 6 | **No se persiste la legenda del DLE EOT.** | `PrinterStatusChecker.Explain()` existe (líneas 362-391) y se usa para `printer_status_log`, pero no para `print_jobs` | El operador ve `P:16 O:12 E:12 S:12` y no sabe interpretarlo. |
| 7 | **No se persiste timing por intento.** | `LatencyTiming.cs` captura `TcpConnectMs`, `DataSendMs`, `TotalPrintMs`, `DataSizeBytes`, pero NO se guarda en `print_jobs` ni en `print_log` | No podemos ver "intento #1 fue timeout a los 5s, intento #2 conectó en 80ms y envió 3KB en 120ms". |
| 8 | **`print_log` guarda transiciones, no diagnósticos.** | Ver `print_log` — solo tiene `estado`, `fecha`, `error_mensaje` | FAILED → RETRIED → DONE aparecen como 3 rows, pero sin los DLE/timings/bytes de cada intento. |
| 9 | **No hay ASB (Automatic Status Back).** | No se envía `GS a n` en `BuildPayload` | La TM-T20IIIL soporta ASB — con una sola línea de código al inicio del payload, la impresora nos manda 4 bytes cada vez que cambia el estado (se acaba papel, se traba, etc.) mientras imprime. Hoy no lo usamos. |
| 10 | **El modal no muestra `fecha_impresion`, ni bytes enviados, ni duración, ni intentos anteriores.** | `DashboardController.cs:88-153` + `dashboard.html:618-695` | El operador abre el modal buscando pistas y encuentra un JSON estéril. |

### Decodificación del DLE EOT del caso (para referencia)

`P:16 O:12 E:12 S:12` en hex:

| Byte | Valor | Bits | Interpretación Epson TM estándar |
|---|---|---|---|
| P | `0x16` = `0001 0110` | bit 1, 2, 4 | Online, no en feed, drawer pin low. **Sin flags de error.** |
| O | `0x12` = `0001 0010` | bit 1, 4 | Tapa cerrada, papel presente, sin error. **OK.** |
| E | `0x12` = `0001 0010` | bit 1, 4 | Sin error recuperable/no-recuperable/cutter. **OK.** |
| S | `0x12` = `0001 0010` | bit 1, 4 | Papel presente, no near-end. **OK.** |

**Conclusión**: la impresora reportó "todo OK" en el pre-check, por eso el worker imprimió. El problema pasó DESPUÉS del pre-check (entre el envío TCP y el fin físico), y no lo capturamos.

---

## 2. Hipótesis del caso concreto (ad99c742a37f)

Sin más datos no es posible confirmar cuál de estas fue, pero con los fixes del plan podremos distinguirlas en el próximo caso:

| # | Hipótesis | Cómo se vería hoy | Cómo la detectaríamos post-plan |
|---|---|---|---|
| A | **Papel se acabó justo al imprimir.** Pre-check OK, durante el raster se terminó el rollo. | DONE sin papel | DLE EOT post-send mostraría `S:60` (bit 5,6 = near-end/end) + legenda "Sin papel" |
| B | **Tapa se abrió durante el print.** (poco probable pero posible si alguien la tocó) | DONE sin papel | DLE EOT post-send mostraría `O:24` (bit 2 = tapa abierta) |
| C | **Cabezal trabado / error mecánico recuperable** (atasco, térmico crítico). | DONE sin papel | DLE EOT post-send mostraría `E:48` o `E:40` (bit 6 = recuperable) |
| D | **Cross-print** por post-print wait insuficiente — la impresora se reseteó con `ESC @` del siguiente job. Documentado en `vibe_engeneering_postprint_wait.md`. | DONE sin papel; el SIGUIENTE job sí imprime, pero el contenido es el del siguiente. | Duración medida anómala + ASB reportaría buffer cleared mid-print |
| E | **Race del port lock con StatusMonitor**. StatusMonitor abrió conexión mientras PrintWorker imprimía → bytes intercalados → la impresora descartó el job. Documentado en §2.3 del vibe previo. | DONE sin papel | Log del port lock mostraría contention; ASB reportaría `Recoverable error, feed button pressed` o buffer abort |
| F | **La impresora recibió el ticket pero se imprimió en blanco** (cabezal térmico frío / defecto del rollo). | DONE sin papel | GS ( H o lectura de contador mostraría print ok; éste es el caso que NO vamos a poder detectar y hay que asumir |

El único escenario realmente indetectable desde software es F (papel defectuoso). Los otros 5 son cubribles.

---

## 3. Plan — 4 fases, incrementales, cada una valiosa sola

Cada fase cierra una brecha específica. Se pueden mergear por separado.

---

### **Fase 1 — DLE EOT post-send + ASB (cierra el falso positivo en sí)**

**Objetivo**: que el job NO se marque DONE si la impresora, después del envío, reporta un estado distinto de "imprimiendo normalmente".

**Dos mecanismos complementarios**:

#### 1.1 DLE EOT post-send síncrono

Después de `transport.SendAsync(payload)` y después de `WaitForPhysicalPrintAsync()`, antes de soltar el port lock, hacer un nuevo check DLE EOT:

```
pre:   DLE EOT 1-4 → guardar `printer_response_pre`
send:  transport.SendAsync(payload)
wait:  WaitForPhysicalPrintAsync(estimatedMs)
post:  DLE EOT 1-4 → guardar `printer_response_post` + `printer_response_post_legend`
      → si post-check indica sin papel / tapa / error no-recuperable / offline → MarkFailed con motivo real
      → si pre y post son ambos idénticos "OK" → DONE
```

**Archivos tocados**:
- `Monitoring/PrinterStatusChecker.cs` — reutilizar `CheckAsync` tal cual, ya soporta el timeout chico.
- `Workers/PrintWorker.cs` — después de `WaitForPhysicalPrintAsync`, antes de `return true` en `SendWithRetryInstrumented`.
- `Data/Models/PrintJobEntity.cs` — +3 columnas: `printer_response_pre` (renombrar el actual a este), `printer_response_post`, `printer_response_post_legend`.
- `Data/PrinterServiceDb.cs` — migración auto `ALTER TABLE` idempotente.

**Config nuevo** (categoría `diagnostics`):

| Key | Default | Descripción |
|---|---|---|
| `PostSendDleCheckEnabled` | `1` | Master switch del post-check |
| `PostSendDleCheckTimeoutMs` | `600` | Timeout corto — debe ser rápido |
| `PostSendDleFailOnError` | `1` | Si el post-check reporta error, marcar FAILED en vez de DONE |
| `PostSendDleFailOnMissingPaper` | `1` | Si el post-check dice sin papel, marcar FAILED |

#### 1.2 ASB (Automatic Status Back) — opcional, más preciso

TM-T20IIIL soporta ASB via `GS a n` (n=1..15 bitmask). Al inicio del payload, agregar:

```
GS a 0x0F   → activa ASB para Drawer, Online, Error, Paper
```

Cuando el estado cambia durante el print, la impresora manda espontáneamente 4 bytes al socket. Si PrintWorker mantiene el socket abierto durante el `WaitForPhysicalPrintAsync` y lee cualquier byte disponible, puede detectar el problema ANTES de que termine el wait.

**Ventajas vs DLE EOT post-send**:
- Detección en tiempo real, no polling.
- No gasta un request/response extra.

**Desventajas**:
- Más complejo (leer del mismo socket async).
- Puede ensuciar el buffer del siguiente comando si no se lee bien.
- No todos los modelos lo soportan consistente (el TM-T88VII sí, el TM-T20IIIL debería pero hay que validar).

**Recomendación de arranque**: implementar 1.1 (post-send DLE) ya, dejar 1.2 (ASB) como paso 2 si 1.1 no alcanza.

---

### **Fase 2 — Historial de intentos (deja de sobrescribir datos diagnósticos)**

**Objetivo**: que cada intento de impresión guarde sus propios datos, para que FAILED → RETRIED → DONE sean 3 registros con diagnóstico independiente, no 1 sobrescrito 3 veces.

**Nueva tabla**:

```sql
CREATE TABLE print_job_attempts (
    id                       INTEGER PRIMARY KEY AUTOINCREMENT,
    job_id                   TEXT NOT NULL,
    attempt_number           INTEGER NOT NULL,       -- 1, 2, 3...
    started_at               TEXT NOT NULL,
    ended_at                 TEXT,
    outcome                  TEXT NOT NULL,           -- DONE, FAILED, WAITING, TIMEOUT
    fail_reason              TEXT,                    -- "Sin papel", "Tapa abierta", "Timeout TCP", etc.

    -- Diagnóstico pre-send
    printer_response_pre     TEXT,                    -- raw DLE EOT
    printer_response_pre_legend TEXT,

    -- Diagnóstico post-send (Fase 1)
    printer_response_post    TEXT,
    printer_response_post_legend TEXT,

    -- Timings (ya capturados en LatencyTiming, solo persistir)
    port_lock_wait_ms        INTEGER,                 -- Fase 3
    tcp_connect_ms           INTEGER,
    data_send_ms             INTEGER,
    post_print_wait_estimated_ms INTEGER,
    post_print_wait_actual_ms    INTEGER,             -- Fase 3

    -- Payload
    payload_bytes            INTEGER,                 -- bytes enviados
    copies                   INTEGER,

    -- Errores
    exception_type           TEXT,                    -- SocketException, TimeoutException...
    exception_message        TEXT
);
CREATE INDEX idx_attempts_job ON print_job_attempts(job_id);
CREATE INDEX idx_attempts_start ON print_job_attempts(started_at);
```

**Flujo en `PrintWorker.ProcessJobAsync`**:

1. Al inicio del proceso (después del port lock): insertar row en `print_job_attempts` con `attempt_number = job.Reintentos + 1` y `outcome = "IN_PROGRESS"`.
2. A medida que avanza: `UPDATE print_job_attempts SET ...` con los campos que se vayan llenando.
3. Al final del intento (DONE/FAILED/WAITING): `UPDATE` con `ended_at`, `outcome`, `fail_reason`.

**Modal del job** pasa de mostrar un solo bloque de diagnóstico a mostrar una **tabla de intentos**:

```
Intento 1 (19:37:26 → 19:37:34, FAILED)
    Pre:  P:12 O:00 E:00 S:00   (Lista para imprimir)
    Post: OFFLINE:Timeout       (TCP dejó de responder a los 5000 ms)
    Timings: lock 2ms · connect 3000ms · send failed
    Error: SocketException - Connection timed out

Intento 2 (19:37:35 → 19:37:40, DONE)
    Pre:  P:16 O:12 E:12 S:12   (Lista para imprimir)
    Post: P:16 O:12 E:12 S:12   (Lista para imprimir, sin cambios)
    Timings: lock 1ms · connect 80ms · send 150ms · wait 3000ms
    Bytes: 2340
```

**Archivos tocados**:
- `Data/Models/PrintJobAttemptEntity.cs` *(nuevo)*
- `Data/PrinterServiceDb.cs` — `CreateTable<PrintJobAttemptEntity>()`.
- `Workers/PrintWorker.cs` — inserción y update del row por intento.
- `Api/Controllers/DashboardController.cs` — endpoint `GET /api/dashboard/job/{id}/attempts` + incluir attempts en el job detail.
- `Resources/dashboard.html` — nueva sección "Intentos" en el modal.

---

### **Fase 3 — Métricas de infraestructura que faltan (cierra la visibilidad)**

**Objetivo**: capturar y exponer las métricas que ya tenemos a medias.

#### 3.1 Calcular y persistir `duracionMs`

Hoy: `DashboardController.cs:196` → `duracionMs = 0`.

Cambio mínimo: en `PrintLogEntity` o en la query del history, calcular `duracionMs = fecha_impresion - fecha_creacion` (para DONE) o `ultimo_update - fecha_creacion` (para los demás estados). Para el detalle fino por intento, viene de Fase 2.

#### 3.2 Medir y persistir el wait del port lock

`PrinterPortLock.AcquireAsync` hoy no reporta cuánto esperó.

Cambio: devolver además del handle un `TimeSpan` con el wait time, que el PrintWorker persiste en el row del intento.

```csharp
public static async Task<PortLockAcquireResult> AcquireAsync(string ip, CancellationToken ct)
{
    var sw = Stopwatch.StartNew();
    await sem.WaitAsync(ct);
    return new PortLockAcquireResult(handle, sw.Elapsed);
}
```

Loggear `WARN` si `wait > 2000 ms`.

#### 3.3 Medir el post-print wait real (no solo el estimado)

`WaitForPhysicalPrintAsync` hoy hace `await Task.Delay(estimatedMs)` — el "real" y el "estimado" son iguales. Pero si se implementa Fase 1.1 (DLE EOT post-send), el "real" pasa a ser `tiempo hasta que el DLE EOT post-send dice idle` — si la impresora responde antes del estimado, se puede cortar el wait.

Cambio opcional: convertir `WaitForPhysicalPrintAsync` en un **wait con poll**: cada 200 ms preguntar DLE EOT, salir cuando reporte idle, máximo `PostPrintWaitMaxMs`. Registrar el tiempo real.

---

### **Fase 4 — UI del modal y del Historial (hacer accesible lo que capturamos)**

**Objetivo**: que el operador pueda diagnosticar sin leer logs.

#### 4.1 Modal de detalle del job — rediseño

Agregar:

- **Fecha Impresión** (ya está en BD, falta mostrarla).
- **Duración total** del job (desde creación hasta DONE, o tiempo total hasta FAILED).
- **DLE EOT pre** con legenda al lado (tooltip o inline).
- **DLE EOT post** con legenda (si Fase 1 implementada).
- **Tabla de intentos** con los campos de Fase 2 (cronológico, expandible).
- **Badge visual** cuando hay discrepancia pre vs post ("⚠ estado cambió durante el print").

Ejemplo de layout propuesto:

```
┌─ Detalle del Job ─────────────────────────────────────────┐
│ Job ad99c742a37f · Comanda 7FC5A6A2... · DONE             │
│ PRINTER_2_EPSON (192.168.10.125:9100) · BARRA SUSHI        │
│ Creado 19:37:26 · Impreso 19:37:40 · Duración 14.2s       │
│ Reintentos 1/3 · Copias 1 · Tipo comanda                  │
│                                                            │
│ ┌─ Intentos ─────────────────────────────────────────────┐│
│ │ #1  FAILED  19:37:26 → 19:37:34 (8.1s)                ││
│ │     Pre:  P:12 O:00 E:00 S:00  ✓ Lista                ││
│ │     Post: —                                             ││
│ │     Error: SocketException (timeout a 3s)              ││
│ │     Bytes: 0  ·  Port lock wait: 2ms                   ││
│ │                                                         ││
│ │ #2  DONE  19:37:35 → 19:37:40 (5.1s)                  ││
│ │     Pre:  P:16 O:12 E:12 S:12  ✓ Lista                ││
│ │     Post: P:16 O:12 E:12 S:12  ✓ Lista (sin cambios)  ││
│ │     Bytes: 2340  ·  Connect: 80ms  ·  Send: 150ms     ││
│ │     Wait físico: estimado 3000ms / real 2847ms        ││
│ └─────────────────────────────────────────────────────────┘│
│                                                            │
│ [Contenido ESC/POS...]                                    │
└────────────────────────────────────────────────────────────┘
```

#### 4.2 Historial de Impresiones

- Rellenar la columna `Duración`.
- Agregar columna `Reintentos` (si > 1, highlightear).
- Icono `⚠` si DLE EOT pre ≠ post (discrepancia durante el print) — disponible post-Fase-1.

#### 4.3 Filtros útiles nuevos

- Checkbox "Solo jobs con discrepancia pre/post DLE EOT" (el filtro anti-falso-positivo).
- Checkbox "Solo jobs con > 1 intento".

---

## 4. Implementación sugerida (orden y esfuerzo)

| Fase | Esfuerzo | Valor | Dependencias |
|---|---|---|---|
| 1.1 (DLE EOT post-send) | S (1 archivo principal, 1 migración) | **Alto** — corta el falso positivo directo | — |
| 2 (tabla de intentos) | M (nuevo modelo, migración, UI) | **Alto** — cambia la narrativa del modal | — |
| 3.1 (duracionMs) | XS (una query) | Medio | — |
| 3.2 (port lock timing) | XS | Medio | — |
| 3.3 (post-wait real con poll) | S | Medio | 1.1 |
| 4.1 (modal rediseñado) | M | Alto (accesibilidad) | 1, 2, 3 |
| 4.2 (Historial) | S | Medio | 3.1 |
| 4.3 (filtros) | S | Bajo | 1, 2 |
| 1.2 (ASB) | L (lectura async de ASB durante send) | Alto si 1.1 no alcanza | 1.1 |

**Recomendación**: mergear **Fase 1.1 + Fase 2 + Fase 3.1/3.2** en un solo PR. Después de 1 semana de producción, evaluar si el falso positivo sigue apareciendo. Si sí → Fase 1.2 (ASB). Si no → Fase 4 (UI).

---

## 5. Configuración consolidada nueva

Todas tocables en caliente vía SQL Console o `POST /api/config/sync`:

| Key | Default | Categoría | Descripción |
|---|---|---|---|
| `PostSendDleCheckEnabled` | `1` | diagnostics | Activa el check DLE EOT después del send |
| `PostSendDleCheckTimeoutMs` | `600` | diagnostics | Timeout del post-check (debe ser corto) |
| `PostSendDleFailOnError` | `1` | diagnostics | Falla el job si el post-check reporta error |
| `PostSendDleFailOnMissingPaper` | `1` | diagnostics | Falla el job si el post-check dice sin papel |
| `AsbEnabled` | `0` | diagnostics | (Fase 1.2) activa ASB en el payload |
| `PostPrintWaitPollEnabled` | `0` | timing | (Fase 3.3) convierte el wait heurístico en wait con poll |
| `PostPrintWaitPollIntervalMs` | `200` | timing | (Fase 3.3) cada cuánto consultar DLE EOT durante el wait |

---

## 6. Migraciones BD

### Nueva columna(s) en `print_jobs` (Fase 1)

```sql
ALTER TABLE print_jobs ADD COLUMN printer_response_post TEXT;
ALTER TABLE print_jobs ADD COLUMN printer_response_post_legend TEXT;
-- Renombrar logical: lo actual printer_response pasa a ser "pre"
-- (NO hace falta rename físico, solo que la legenda nueva se llama _pre en código)
ALTER TABLE print_jobs ADD COLUMN printer_response_pre_legend TEXT;
```

### Nueva tabla `print_job_attempts` (Fase 2)

Ver §3 — Fase 2.

Ambas migraciones idempotentes en `PrinterServiceDb.CreateTables()` siguiendo el patrón de §2.5 del vibe previo (`PRAGMA table_info` + `ALTER TABLE ADD COLUMN`).

---

## 7. Validación manual (post-deploy)

Casos a reproducir físicamente en producción con PRINTER_2_EPSON:

1. **Quitar papel a mitad de impresión** (abrir tapa con ticket en curso + sacar rollo):
   - Esperado: job pasa a FAILED con `fail_reason = "Sin papel (post-check)"`.
   - Dashboard debe mostrar `Pre: OK / Post: S:60 (sin papel)`.

2. **Apagar la impresora a mitad de envío**:
   - Esperado: FAILED con `fail_reason = "Timeout TCP"`, `printer_response_post = "OFFLINE:Timeout"`.

3. **Impresión normal larga (ticket con mucho negro)**:
   - Esperado: DONE, ambos DLE EOT iguales, `post_print_wait_actual_ms < estimated_ms` (si Fase 3.3 activa).

4. **Dos jobs consecutivos back-to-back**:
   - Esperado: no cross-print, port lock visible en logs con wait < 100 ms.

5. **Retry manual desde el dashboard**:
   - Esperado: aparece un row nuevo en `print_job_attempts` con `attempt_number = 2`, el `attempt_number = 1` original sigue intacto en BD.

---

## 8. Limitaciones reconocidas

1. **Papel con defecto físico (rollo descolorido, baja térmica)**: el cabezal imprime "nada" pero reporta OK. Indetectable desde software. Mitigación: un sensor de "papel caliente" requeriría hardware adicional, fuera de alcance.

2. **Impresora con firmware defectuoso que mienta en DLE EOT**: reportado en algunos clones no-Epson. Mitigación: ASB (Fase 1.2) es más difícil de falsear porque es asincrónico.

3. **Red intermitente entre pre-check y send**: el pre-check pasa, el send truena, el post-check truena. Fase 1.1 lo reporta correctamente, pero el usuario debería notarlo antes (ícono `⚠` en el listado).

4. **`printer_response` actual se renombra a `printer_response_pre`**: JSON del endpoint `/api/dashboard/job/{id}` va a tener ambos campos durante la transición. Cliente debe tolerar ambos hasta el deploy completo.

---

## 9. Cambios futuros evaluables (NO en este plan)

- **Telemetría agregada**: dashboard con rate de falsos positivos por impresora/día, para detectar tendencias.
- **Test de impresión programado** (página de auto-diag diario): imprime un ticket de prueba a las 6am, si el DLE EOT post es discrepante alerta.
- **Integración con GS I n** (identificación de impresora) al registrar — hoy no validamos que el modelo declarado coincida con lo que la impresora físicamente es.
- **Feedback visual desde el Front del POS** (QuipuNetX) al operador: toast inmediato cuando un job marca FAILED con la leyenda del DLE EOT, no esperar a que revise el dashboard.

---

## 10. Glosario

- **Pre-check DLE EOT**: consulta al estado de la impresora ANTES del envío del payload ESC/POS.
- **Post-check DLE EOT**: consulta al estado DESPUÉS del envío y del wait físico. *Nuevo en este plan.*
- **ASB (Automatic Status Back)**: comando `GS a n` de Epson que habilita notificaciones asincrónicas del estado. El driver recibe 4 bytes automáticos cada vez que el estado cambia, sin necesidad de polling.
- **Falso positivo**: job marcado `DONE` por PrinterServices sin que el papel haya salido físicamente. Lo que este plan ataca.
- **Intento** (`attempt`): una ejecución de `ProcessJobAsync` para un job específico. Un job FAILED con reintentos genera N intentos.
