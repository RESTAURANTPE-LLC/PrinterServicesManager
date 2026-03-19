# Manual Técnico de PrinterServices

## Guía Completa para Soporte Técnico y Customer Success

**Versión:** 1.0  
**Fecha:** Marzo 2026  
**Audiencia:** Personal de soporte técnico en línea y área de Customer Success  
**Producto:** QuipuNet POS — Módulo PrinterServices

---

# PARTE 1: CONCEPTOS FUNDAMENTALES

---

## 1. ¿Qué es PrinterServices?

**PrinterServices** es un servicio de Windows que se encarga de **gestionar todas las impresiones** (comandas de cocina, boletas, facturas, precuentas, etc.) de manera centralizada en el restaurante.

### Antes de PrinterServices (flujo antiguo)

Cada terminal del POS enviaba directamente los datos a la impresora por la red. Esto causaba problemas:

- Si la impresora estaba ocupada o sin papel, **nadie se enteraba**
- Si dos terminales enviaban a la misma impresora al mismo tiempo, **se perdían comandas**
- Si el POS se cerraba durante la impresión, **el trabajo se perdía**
- No había reintentos automáticos ni historial de impresiones

### Con PrinterServices (flujo nuevo)

PrinterServices actúa como un **intermediario inteligente**:

1. El POS envía el pedido al servidor
2. El servidor envía los datos de impresión a PrinterServices
3. PrinterServices los guarda en una base de datos local (nunca se pierden)
4. PrinterServices imprime con reintentos automáticos (hasta 3 intentos)
5. PrinterServices notifica al POS si la impresión fue exitosa o falló
6. Si la impresora no tiene papel o está apagada, PrinterServices espera y reintenta automáticamente cuando vuelva

### Beneficio clave para el restaurante

> **"Toda comanda se imprime. Si no se puede, el POS se entera inmediatamente."**

---

## 2. Componentes del Sistema

El sistema de impresión involucra **tres programas** que trabajan juntos:

| Componente | Programa | Ubicación | Rol |
|---|---|---|---|
| **Frontend (Front)** | `QuipuNet.exe` | Cada terminal POS | Lo que ve y usa el usuario (pantallas, botones) |
| **Backend (Servidor)** | `QuipuNetX.dll` | Dentro de cada terminal | Procesa la lógica de negocio (pedidos, ventas, etc.) |
| **Servicio de Impresión** | `PrinterServices.exe` | Solo en la PC servidor | Gestiona todas las impresiones del local |

### Diagrama simplificado

```
Terminal 1 (Caja) ─────┐
Terminal 2 (Salón) ─────┼──► Servidor QuipuNetX ──► PrinterServices.exe
Terminal 3 (Delivery) ──┘         (lógica)              (impresión)
                                                            │
                                                    ┌───────┼───────┐
                                                    ▼       ▼       ▼
                                                Impresora Impresora Impresora
                                                 Cocina    Bar      Caja
```

### Puntos clave para soporte

- **PrinterServices solo corre en la PC servidor** — nunca en las terminales cliente
- **PrinterServices es un servicio de Windows** — se inicia automáticamente al encender la PC
- **Una sola instancia** de PrinterServices gestiona TODAS las impresoras del local
- PrinterServices se comunica por **red local (LAN)** — no necesita internet

---

## 3. Feature Flag: USAR_PRINTER_SERVICE

### ¿Qué es un Feature Flag?

Es un **interruptor** que activa o desactiva PrinterServices. Cuando está:

- **OFF (apagado)**: El sistema funciona como antes — cada terminal imprime directamente
- **ON (encendido)**: El servidor delega toda la impresión a PrinterServices

### ¿Dónde se configura?

**Archivo:** `config_fla.cfg`  
**Ubicación:** `%LOCALAPPDATA%\QuipuNet\config_fla.cfg`  
(Normalmente: `C:\Users\[usuario]\AppData\Local\QuipuNet\config_fla.cfg`)

**Contenido relevante:**
```
USAR_PRINTER_SERVICE=true
PRINTER_SERVICE_IP=10.0.0.21
PRINTER_SERVICE_HTTP_PORT=8090
PRINTER_SERVICE_GRPC_PORT=50051
```

| Parámetro | Descripción | Valor por defecto |
|---|---|---|
| `USAR_PRINTER_SERVICE` | Activa/desactiva PrinterServices | `false` |
| `PRINTER_SERVICE_IP` | IP donde corre PrinterServices | IP del servidor |
| `PRINTER_SERVICE_HTTP_PORT` | Puerto HTTP de PrinterServices | `8090` |
| `PRINTER_SERVICE_GRPC_PORT` | Puerto de notificaciones | `50051` |

### Regla importante

> **Solo el SERVIDOR evalúa el flag.** Las terminales cliente no necesitan tener este flag configurado. Ellas simplemente envían pedidos al servidor y el servidor decide si usa PrinterServices o no.

### Los 4 escenarios posibles

| Escenario | Terminal | Flag | ¿Quién imprime? |
|---|---|---|---|
| 1 | Servidor | OFF | El propio POS (flujo antiguo) |
| 2 | Servidor | ON | PrinterServices (flujo nuevo) |
| 3 | Cliente | OFF en servidor | El cliente imprime localmente |
| 4 | Cliente | ON en servidor | PrinterServices (el servidor delegó) |

---

## 4. Puertos de Comunicación

PrinterServices usa varios puertos de red. Es importante conocerlos para diagnóstico:

| Puerto | Protocolo | Dirección | Uso |
|---|---|---|---|
| **8090** | HTTP | Servidor → PrinterServices | Enviar trabajos de impresión |
| **50051** | gRPC | PrinterServices → POS | Notificaciones de estado (éxito/fallo) |
| **9999** | UDP | POS ↔ PrinterServices | Auto-descubrimiento en la red |
| **9100** | TCP | PrinterServices → Impresoras | Envío de datos a impresoras físicas |

### Para diagnóstico de conectividad

Si hay problemas de comunicación, verificar que estos puertos no estén bloqueados por:
- Firewall de Windows
- Antivirus
- Router/Switch configurado con filtros

---

## 5. Identificadores Clave del Sistema

Estos son los IDs que aparecen en logs, pantallas y la API. Es fundamental conocerlos:

| Identificador | Qué es | Ejemplo | Dónde aparece |
|---|---|---|---|
| **job_id** | ID único del trabajo de impresión en PrinterServices | `"714"` | Logs, API, base de datos |
| **pedido_id** | ID del pedido en QuipuNet | `"1772"` | POS, base de datos |
| **impresora_id** | ID de la impresora configurada | `"IMP-001"` | Configuración, API |
| **device_id** | ID del dispositivo/terminal POS | `"POS-001"` | Notificaciones, logs |
| **mac_address** | Dirección física de la impresora (inmutable) | `"AA:BB:CC:DD:EE:FF"` | Base de datos, logs de red |
| **impresora_ip** | IP de red de la impresora | `"192.168.1.100"` | Configuración, puede cambiar |

### Relación entre job_id y pedido_id

Un **job** (trabajo de impresión) puede contener **varios pedidos**. Ejemplo:

- El mozo envía un pedido con 3 platos para la cocina
- Se genera **1 comanda** (1 trabajo de impresión) con **3 pedidos** internos
- `job_id = "714"` contiene `pedido_ids = ["1772", "1773", "1774"]`

Esta relación es crucial para la trazabilidad: si un job falla, sabemos exactamente qué pedidos se vieron afectados.

### La MAC Address: El identificador más importante

La **MAC address** es el identificador **físico** de la impresora que **nunca cambia**, a diferencia de la IP que puede cambiar si el router reasigna direcciones (DHCP). PrinterServices usa la MAC como referencia principal para:

- Identificar impresoras de manera confiable
- Detectar automáticamente si una impresora cambió de IP
- Auto-actualizar la configuración sin intervención manual

---

## 6. Estados de los Trabajos de Impresión

Cada trabajo de impresión pasa por diferentes estados:

| Estado | Significado | Acción requerida |
|---|---|---|
| **PENDING** | En cola, esperando ser impreso | Ninguna — se procesará automáticamente |
| **PRINTING** | Imprimiéndose en este momento | Ninguna — en proceso |
| **DONE** | Impreso exitosamente | Ninguna — completado |
| **FAILED** | Falló después de 3 reintentos | Revisar impresora y reintentar manualmente |
| **WAITING** | Impresora offline — esperando reconexión | Verificar impresora (encendida, conectada) |
| **CANCELLED** | Cancelado (por reimpresión u operador) | Ninguna — fue reemplazado |

### Flujo normal de estados

```
PENDING → PRINTING → DONE (éxito)
PENDING → PRINTING → FAILED (fallo tras 3 reintentos)
PENDING → WAITING (impresora offline) → PENDING (cuando vuelva) → PRINTING → DONE
```

### Reintentos automáticos

PrinterServices reintenta automáticamente hasta **3 veces** con esperas progresivas:
- Intento 1: inmediato
- Intento 2: espera 500ms
- Intento 3: espera 1 segundo
- Si todos fallan: marca como **FAILED** y notifica al POS

---

## 7. Tipos de Impresión

PrinterServices maneja diferentes tipos de documentos:

| Tipo | Descripción | Endpoint API | Estrategia Backend |
|---|---|---|---|
| **Comandas** | Pedidos a cocina/barra | `/api/print/comandas` | ServerComandasStrategy |
| **Comprobante de venta** | Boleta o factura | `/api/print/venta` | ServerVentaRapidaStrategy |
| **Precuenta** | Pre-cuenta para el cliente | `/api/print/precuenta` | ServerDeliveryStrategy |
| **Sorteo** | Cupón promocional | `/api/print/comanda` | (incluido en estrategias) |
| **Encuesta** | Encuesta de satisfacción | `/api/print/comanda` | (incluido en estrategias) |

### Exclusión: Stickers Jaltech

Los stickers Jaltech **NO** pasan por PrinterServices. Usan un driver especial y siempre se imprimen directamente desde el POS. Si un pedido incluye stickers, PrinterServices imprime las comandas y el POS imprime los stickers por separado.

---

# PARTE 2: PANTALLAS Y MODALES DEL POS

---

## 8. Modales de Notificación de Impresión

Cuando PrinterServices está activo, el POS muestra ventanas flotantes (modales) en la **esquina inferior derecha** del escritorio para informar al usuario sobre el estado de las impresiones.

### 8.1 Modal "Imprimiendo" (Barra de Progreso Azul)

**Cuándo aparece:** Cuando se envían documentos a imprimir.

**Aspecto visual:**
```
┌──────────────────────────────────────────────────────────┐
│  Imprimiendo 3 documento(s)          📅 Fecha  🕐 Hora  │
│  ┌──────────────────────────────────────────────────────┐│
│  │ ████████████████████░░░░░░░░░░  Imprimiendo... 00:05 ││
│  └──────────────────────────────────────────────────────┘│
└──────────────────────────────────────────────────────────┘
```

**Información que muestra:**
- Cantidad de documentos pendientes de imprimir
- Fecha y hora de inicio
- Barra de progreso animada (azul)
- Tiempo transcurrido en formato MM:SS

**Comportamiento:**
- La barra avanza de 0% a 100% basada en un tiempo estimado (3 segundos por documento)
- Si se envían más documentos mientras se imprime, la barra se reinicia
- Cuando llega al 100%:
  - Si todo fue exitoso: la barra se pone **verde** con texto "¡Envío exitoso!"
  - Si hubo errores: la barra se pone **roja** con texto "¡Envío fallido!"

**Colores de la barra de progreso:**

| Color | Significado |
|---|---|
| Azul (`#76A8F9`) | Impresión en progreso |
| Verde (`#9AD791`) | Todas las impresiones exitosas |
| Rojo (`#F45B69`) | Al menos una impresión falló |

### 8.2 Modal "Impresión Fallida" (Alerta Roja)

**Cuándo aparece:** Automáticamente cuando al menos una impresión falló, después de cerrar el modal de progreso.

**Aspecto visual:**
```
┌────┬─────────────────────────────────────────────────────┐
│    │  Tienes 2 impresione(s) con error                  │
│ ⚠️ │  📅 15/03/2026        🕐 14:30                     │
│    │                                                     │
│    │  ┌──────────────┐  ┌──────────────────┐            │
│    │  │   Cancelar   │  │    Reintentar    │            │
│    │  └──────────────┘  └──────────────────┘            │
└────┴─────────────────────────────────────────────────────┘
```

**Información que muestra:**
- Cantidad de impresiones que fallaron
- Fecha y hora del error
- Dos botones de acción

**Botones de acción:**

| Botón | Qué hace |
|---|---|
| **Cancelar** | Cierra el modal (las impresiones fallidas quedan en la lista de cabecera) |
| **Reintentar** | Vuelve a intentar TODAS las impresiones fallidas, muestra barra de progreso |

**Comportamiento al reintentar:**
1. Los botones se deshabilitan
2. Aparece una barra de progreso
3. Se reintenta cada impresión fallida
4. Al terminar: verifica si quedan errores
5. Se auto-cierra después de 5 segundos

### 8.3 Badge de Cabecera (Campana/Indicador)

Además de los modales, la **cabecera del POS** muestra un indicador visual cuando hay impresiones fallidas:

- **Campana animada** (parpadeo) mientras haya errores pendientes
- **Badge numérico** con la cantidad de impresiones fallidas
- Se detiene cuando se resuelven todos los errores

**Ubicación:** En la barra superior del POS, visible desde cualquier pantalla.

Desde la cabecera, el usuario puede:
- Ver la lista de impresiones fallidas
- Reintentar cada una individualmente (botón por cada error)
- Cancelar/descartar errores individuales

### 8.4 Modal "Impresión Exitosa" (Badge Verde)

**Estado actual:** Este modal está diseñado pero **NO está activo** en el sistema. No se muestra en ningún flujo automático.

---

## 9. Pantallas donde se usa PrinterServices

### 9.1 Pantalla de Pedidos (Envío de Comandas)

**Pantalla:** Lista de Pedidos Temporales  
**Acción:** Enviar pedidos a cocina/barra  
**Clase:** `ListaPedidosTemporalesPresenter`

**Flujo con PrinterServices activo:**
1. Usuario presiona "Enviar" pedidos
2. Backend procesa el pedido y envía comandas a PrinterServices
3. PrinterServices registra los jobs y responde con `job_ids`
4. El POS muestra "Pedido enviado exitosamente"
5. Si PrinterServices no responde: muestra error "PrinterServiceLCA no está activo"

**Identificador clave:** La respuesta incluye `PrintJobResults` con la relación `job_id ↔ pedido_ids`

### 9.2 Pantalla de Cobro (Venta Rápida)

**Pantalla:** Detalle de Pago  
**Acción:** Registrar venta e imprimir comprobante + comandas  
**Clase:** `FragmentDetallePago`

**Flujo con PrinterServices activo:**
1. Usuario cobra la venta
2. Backend genera comprobante + comandas + sorteo + encuesta + precuenta
3. Todo se envía a PrinterServices (hasta 5 tipos de impresión)
4. El POS NO ejecuta ninguna impresión local

### 9.3 Pantalla de Delivery

**Pantalla:** Lista de Delivery Pendientes  
**Acción:** Confirmar delivery  
**Clase:** `ListaDeliveryPendientePresenter`

**Flujo con PrinterServices activo:**
1. Se confirma el delivery
2. Backend genera comprobante + precuenta delivery
3. Se envía a PrinterServices
4. El modal de progreso muestra el estado

### 9.4 Pantalla de Reimpresión

**Pantalla:** Detalle del Pedido  
**Acción:** Reimprimir comanda  

**Flujo con PrinterServices activo:**
1. Usuario solicita reimprimir
2. El sistema cancela el job anterior (si existe) para evitar duplicados
3. Se genera nueva comanda y se envía a PrinterServices
4. Se registra nuevo `job_id` en la tabla `pedidoprinterjob`

---

## 10. PSProgressService — Monitoreo en Tiempo Real

`PSProgressService` es el servicio del Frontend que monitorea el estado de los jobs enviados a PrinterServices en tiempo real usando callbacks gRPC.

**Flujo obligatorio:**
1. Backend envía comandas a PrinterServices → retorna `PrintJobResults`
2. Frontend detecta que hay `PrintJobResults` → llama `verificarPrintJobsPorComandas`
3. Se muestra el modal de progreso
4. PSProgressService escucha notificaciones de PrinterServices
5. Al completar → éxito o → muestra modal de error con reintento

**Regla crítica:** PSProgressService es el **ÚNICO** componente autorizado para abrir los modales de resultado de impresión. Ningún otro componente debe abrir estos modales por su cuenta.

---

# PARTE 3: API REST — CONSULTAS Y DIAGNÓSTICO

---

## 11. Cómo Acceder a la API

PrinterServices expone una API HTTP REST en el puerto **8090**. Puedes consultar esta API desde:

- **Navegador web**: Para endpoints GET (consultas)
- **Postman**: Para cualquier tipo de consulta (recomendado para soporte)
- **curl** (línea de comandos): Para scripts y diagnóstico rápido

**URL base:** `http://[IP_SERVIDOR]:8090`

Ejemplo: Si el servidor tiene IP `192.168.1.10`, la URL base sería `http://192.168.1.10:8090`

### 11.1 Health Check — ¿PrinterServices está vivo?

**La primera consulta que debes hacer siempre:**

```
GET http://localhost:8090/api/health
```

**Respuesta esperada:**
```json
{
  "status": "OK",
  "timestamp": "2026-03-15T14:30:00",
  "db_path": "C:\\Users\\...\\printerservice.db"
}
```

Si esta consulta **no responde**, PrinterServices no está corriendo o el puerto está bloqueado.

### 11.2 Estado de Impresoras

```
GET http://localhost:8090/api/printer/status
```

**Respuesta:**
```json
[
  {
    "impresora_id": "IMP-001",
    "nombre": "Cocina Principal",
    "ip": "192.168.1.100",
    "online": true,
    "disponible_para_imprimir": true,
    "tiene_papel": true,
    "tapa_abierta": false,
    "ultimo_check": "2026-03-15T14:29:55",
    "jobs_pendientes": 0
  },
  {
    "impresora_id": "IMP-002",
    "nombre": "Barra",
    "ip": "192.168.1.101",
    "online": false,
    "disponible_para_imprimir": false,
    "tiene_papel": true,
    "tapa_abierta": false,
    "ultimo_check": "2026-03-15T14:29:55",
    "jobs_pendientes": 3
  }
]
```

**Campos importantes para diagnóstico:**

| Campo | Qué verificar |
|---|---|
| `online` | `false` = Impresora no responde en la red |
| `disponible_para_imprimir` | `false` = Puede estar sin papel, tapa abierta o offline |
| `tiene_papel` | `false` = Sin papel — pedir al personal que recargue |
| `tapa_abierta` | `true` = Tapa abierta — pedir que la cierren |
| `jobs_pendientes` | Número > 0 = Hay trabajos esperando para esta impresora |

### 11.3 Trabajos Pendientes

```
GET http://localhost:8090/api/jobs/pending
```

**Respuesta:** Lista de trabajos que aún no se han impreso, con su estado y datos.

### 11.4 Detalle de un Trabajo Específico

```
GET http://localhost:8090/api/job/714
```

**Respuesta:** Información completa del job 714, incluyendo estado, reintentos, errores, y pedidos asociados.

### 11.5 Reintentar un Trabajo Fallido

```
POST http://localhost:8090/api/job/714/retry
```

Esto re-encola el trabajo fallido para un nuevo intento de impresión.

---

## 12. API de Configuración

PrinterServices permite consultar y modificar configuraciones en caliente (sin reiniciar el servicio).

### 12.1 Ver todas las configuraciones

```
GET http://localhost:8090/api/config
```

### 12.2 Ver una configuración específica

```
GET http://localhost:8090/api/config/MaxRetries
```

**Respuesta:**
```json
{
  "key": "MaxRetries",
  "value": "3",
  "defaultValue": "3",
  "description": "Reintentos máximos por job",
  "valueType": "int",
  "minValue": "0",
  "maxValue": "10"
}
```

### 12.3 Configuraciones más importantes

| Configuración | Default | Descripción | Cuándo modificar |
|---|---|---|---|
| `TcpConnectTimeoutMs` | 3000 | Timeout de conexión TCP (ms) | Aumentar si hay red lenta |
| `TcpSendTimeoutMs` | 5000 | Timeout de envío TCP (ms) | Aumentar si impresoras tardan |
| `MaxRetries` | 3 | Reintentos máximos por job | Aumentar en redes inestables |
| `RetryBackoffBaseMs` | 500 | Espera base entre reintentos (ms) | Aumentar si impresoras saturadas |
| `StatusCheckIntervalSeconds` | 15 | Intervalo de verificación de estado | Reducir para detección más rápida |
| `HttpPort` | 8090 | Puerto HTTP de la API | Solo si hay conflicto de puerto |
| `GrpcPort` | 50051 | Puerto gRPC de notificaciones | Solo si hay conflicto de puerto |

### 12.4 Modificar una configuración

```
PUT http://localhost:8090/api/config
Body: { "key": "MaxRetries", "value": "5" }
```

**Respuesta:**
```json
{ "status": "UPDATED", "key": "MaxRetries", "value": "5" }
```

> **Nota:** Los cambios se aplican inmediatamente, sin reiniciar el servicio.

### 12.5 Restablecer valores por defecto

```
POST http://localhost:8090/api/config/MaxRetries/reset
```

Para restablecer TODOS:
```
POST http://localhost:8090/api/config/reset-all
```

---

# PARTE 4: DIAGNÓSTICO Y RESOLUCIÓN DE PROBLEMAS

---

## 13. Guía de Diagnóstico Paso a Paso

### Paso 1: ¿PrinterServices está corriendo?

**Verificar servicio de Windows:**
1. Abrir "Servicios" de Windows (`services.msc`)
2. Buscar "PrinterServices"
3. Verificar que el estado sea "En ejecución"

**Verificar vía API:**
```
GET http://localhost:8090/api/health
```
- Si responde → el servicio está activo
- Si NO responde → el servicio está detenido o el puerto está bloqueado

**Acciones si no está corriendo:**
- Iniciar el servicio desde la consola de servicios
- O ejecutar: `PrinterServices.exe start`

### Paso 2: ¿El Feature Flag está activo?

**Verificar archivo:** `%LOCALAPPDATA%\QuipuNet\config_fla.cfg`

Buscar la línea:
```
USAR_PRINTER_SERVICE=true
```

Si dice `false` o no existe la línea, PrinterServices no se usará.

### Paso 3: ¿Las impresoras están conectadas?

```
GET http://localhost:8090/api/printer/status
```

Verificar para cada impresora:
- `online: true` → Conectada
- `disponible_para_imprimir: true` → Lista para imprimir
- `tiene_papel: true` → Tiene papel
- `tapa_abierta: false` → Tapa cerrada

### Paso 4: ¿Hay trabajos pendientes o fallidos?

```
GET http://localhost:8090/api/jobs/pending
```

Si hay trabajos en estado `WAITING` o `FAILED`, investigar la causa.

---

## 14. Problemas Comunes y Soluciones

### Problema 1: "No se imprimen las comandas"

**Diagnóstico:**
1. ¿PrinterServices está corriendo? → `GET /api/health`
2. ¿El flag está activo? → Verificar `config_fla.cfg`
3. ¿La impresora está online? → `GET /api/printer/status`
4. ¿Hay jobs pendientes? → `GET /api/jobs/pending`

**Soluciones comunes:**
- **Servicio detenido:** Iniciar PrinterServices desde servicios de Windows
- **Impresora offline:** Verificar cable de red, encendido, IP correcta
- **Sin papel:** Recargar papel en la impresora
- **Puerto bloqueado (8090):** Verificar firewall de Windows
- **Flag desactivado:** Activar `USAR_PRINTER_SERVICE=true` en `config_fla.cfg`

### Problema 2: "Las comandas se imprimen dos veces"

**Causa probable:** El flag está en un estado inconsistente entre terminales.

**Solución:**
- Verificar que `USAR_PRINTER_SERVICE` esté en el mismo estado en TODOS los archivos `config_fla.cfg`
- Si el flag está ON, las terminales cliente NO deben intentar imprimir localmente
- Verificar que no haya reimpresiones manuales en paralelo

### Problema 3: "El POS muestra 'PrinterServiceLCA no está activo'"

**Significado:** El backend intentó comunicarse con PrinterServices pero no obtuvo respuesta después de 2 intentos.

**Diagnóstico:**
1. ¿PrinterServices está corriendo?
2. ¿La IP y puerto son correctos en `config_fla.cfg`?
3. ¿Hay conectividad de red entre el servidor y PrinterServices?

**Solución:**
- Iniciar el servicio PrinterServices
- Verificar la IP configurada en `PRINTER_SERVICE_IP`
- Verificar que el puerto 8090 no esté bloqueado

### Problema 4: "La impresora aparece como offline pero está encendida"

**Causas posibles:**
1. **Cambio de IP por DHCP** — PrinterServices debería auto-detectar esto
2. **Cable de red desconectado** — Verificar conexión física
3. **IP incorrecta en configuración** — Verificar `GET /api/printer/status`

**Solución:**
- PrinterServices auto-resuelve cambios de IP usando la MAC address
- Si no se auto-resuelve, verificar que la impresora tenga MAC registrada
- Verificar logs de PrinterServices para mensajes de `[MONITOR]` o `[ARP]`

### Problema 5: "Sin papel" detectado pero la impresora tiene papel

**Causa:** La impresora reporta "sin papel" después de recargar y el sistema aún no detecta el cambio.

**Solución:**
- Esperar hasta 5 segundos — PrinterServices verifica automáticamente cada 5-15 segundos
- Si persiste, reintentar manualmente: `POST /api/job/{jobId}/retry`
- Verificar que la tapa de la impresora esté bien cerrada (a veces reporta "sin papel" con tapa abierta)

### Problema 6: "Jobs en estado WAITING permanentemente"

**Significado:** La impresora está offline y los jobs esperan a que vuelva.

**Diagnóstico:**
1. Verificar que la impresora esté encendida y conectada a la red
2. Verificar `GET /api/printer/status` para esa impresora
3. Revisar logs para mensajes de `[MONITOR]`

**Solución:**
- Resolver el problema de conectividad de la impresora
- PrinterServices re-encolará automáticamente los jobs cuando la impresora vuelva
- Si la impresora cambió de IP, PrinterServices intentará auto-resolver usando ARP

### Problema 7: "El servicio no se inicia"

**Diagnóstico:**
1. Verificar logs en la carpeta de PrinterServices
2. Verificar que `QuipuNetX.dll` esté accesible
3. Verificar permisos del usuario del servicio

**Solución:**
- Reinstalar el servicio: `PrinterServices.exe install`
- Verificar la ruta de `QuipuNetX.dll` en la configuración del proyecto
- Ejecutar como administrador si hay problemas de permisos

---

## 15. Cómo Interpretar los Logs

### Ubicación de los logs

Los logs de PrinterServices se guardan según la configuración de `log4net.config`. Buscar archivos `.log` en la carpeta del servicio.

### Prefijos de log más importantes

| Prefijo | Componente | Qué reporta |
|---|---|---|
| `[MONITOR]` | StatusMonitor | Estado de impresoras (online/offline/papel) |
| `[WORKER]` | PrintWorker | Procesamiento de jobs (impresión) |
| `[ARP]` | ArpHelper/ArpScanWorker | Detección de IPs y MACs |
| `[QUIPU-NOTIFIER]` | QuipuNetXNotifier | Notificaciones a QuipuNetX |
| `[NOTIF-RETRY]` | NotificationRetryWorker | Reintentos de notificaciones |
| `[SNMP]` | SnmpHelper | Monitoreo SNMP de impresoras |
| `[API]` | HTTP API | Requests recibidos |
| `[GRPC]` | gRPC Server | Notificaciones enviadas |

### Ejemplos de logs y su significado

**Impresión exitosa:**
```
[WORKER] Job 714 → Cocina1 (192.168.1.100) DONE
```
→ El job 714 se imprimió correctamente en la impresora Cocina1.

**Impresora offline:**
```
[MONITOR] ✗ Cocina1 (192.168.1.100) ERROR al verificar: Connection refused
```
→ La impresora Cocina1 no responde. Puede estar apagada o desconectada.

**Auto-resolución de IP:**
```
[MONITOR] ✅ Cocina1 AUTO-RESUELTA: 192.168.1.100 → 192.168.1.150
```
→ La impresora Cocina1 cambió de IP (DHCP). PrinterServices la encontró automáticamente en su nueva IP.

**Job fallido:**
```
[WORKER] Job 714 → Cocina1 (192.168.1.100) FAILED (3 reintentos)
```
→ El job 714 falló después de 3 intentos. Requiere intervención.

**PrinterServices no puede contactar a QuipuNetX:**
```
[QUIPU-NOTIFIER] 🔌 QuipuNetX offline/inalcanzable: No connection could be made
[QUIPU-NOTIFIER] 💾 Notificación encolada para retry: MAC AA:BB:CC... → 192.168.68.150
```
→ PrinterServices detectó un cambio de IP pero no pudo notificar a QuipuNetX. La notificación se guardó y se reintentará automáticamente cada 30 segundos.

**Notificación exitosa tras reintento:**
```
[NOTIF-RETRY] ✅ Notificación ID 42 enviada exitosamente (MAC AA:BB:CC...)
```
→ Una notificación pendiente fue enviada exitosamente a QuipuNetX.

---

# PARTE 5: ARQUITECTURA DE RED Y AUTO-RESOLUCIÓN

---

## 16. Monitoreo de Impresoras

PrinterServices monitorea constantemente el estado de todas las impresoras registradas usando dos técnicas:

### SNMP (Simple Network Management Protocol)

- **Qué es:** Un protocolo estándar para consultar el estado de dispositivos de red
- **Cómo funciona:** Envía 1 paquete UDP y recibe la respuesta (~15ms)
- **Qué detecta:** Estado del papel, tapa abierta/cerrada, online/offline, temperatura
- **Compatibilidad:** Impresoras de red modernas (Epson TM-T88VI, Star TSP650, etc.)

### DLE EOT (Data Link Escape / End of Transmission)

- **Qué es:** Un comando ESC/POS que pregunta el estado directamente a la impresora
- **Cómo funciona:** Establece conexión TCP al puerto 9100 (~75ms)
- **Qué detecta:** Papel, tapa, errores básicos
- **Compatibilidad:** Todas las impresoras ESC/POS (universal)

### Estrategia híbrida

PrinterServices usa ambas técnicas de manera inteligente:

| Cuándo | Técnica | Por qué |
|---|---|---|
| **Monitoreo continuo** (cada 5-15s) | SNMP primero, DLE EOT como fallback | SNMP es 5x más rápido y usa 80% menos red |
| **Antes de imprimir** | Siempre DLE EOT | Es más confiable para estado en tiempo real |
| **Impresora sin SNMP** | Solo DLE EOT | Compatible con todas las impresoras |

---

## 17. Auto-Resolución de IP (DHCP)

Uno de los problemas más comunes en restaurantes es que el router cambia la IP de una impresora (reasignación DHCP). PrinterServices resuelve esto automáticamente:

### Flujo de auto-resolución

```
1. Impresora registrada: IP 192.168.1.100, MAC AA:BB:CC:DD:EE:FF
2. Router DHCP le asigna nueva IP: 192.168.1.150
3. PrinterServices detecta que 192.168.1.100 no responde
4. StatusMonitor delega búsqueda a ArpScanWorker
5. ArpScanWorker escanea la red y encuentra la MAC en la nueva IP
6. PrinterServices actualiza automáticamente la IP en su base de datos
7. Los jobs en espera se reintentan con la nueva IP
8. PrinterServices notifica a QuipuNetX del cambio de IP
9. Todo esto ocurre en menos de 15 segundos sin intervención manual
```

### Verificar auto-resolución en logs

Buscar mensajes con `[MONITOR] ✅ AUTO-RESUELTA` o `[ARP]` en los logs.

### Campo de auditoría

En la base de datos, el campo `ip_resuelta_por_arp = 1` indica que la IP fue cambiada automáticamente por el sistema de auto-resolución. Esto es útil para auditoría.

---

## 18. Sincronización con QuipuNetX

PrinterServices y QuipuNetX mantienen sus datos sincronizados automáticamente:

### Flujo 1: Sincronización inicial (al arrancar)

Cuando QuipuNetX inicia su servidor web:
1. Consulta todas las impresoras activas con modo "SERVICIO" de su base de datos
2. Envía la lista a PrinterServices: `POST /api/printers/sync`
3. PrinterServices actualiza su catálogo de impresoras

### Flujo 2: Notificación de cambio de IP

Cuando PrinterServices detecta que una impresora cambió de IP:
1. Actualiza su propia base de datos
2. Notifica a QuipuNetX: `POST /api/rest/printers/update-ip`
3. Si QuipuNetX no responde, guarda la notificación y reintenta cada 30 segundos
4. Máximo 10 reintentos antes de marcar como fallido

---

# PARTE 6: AUDITORÍA Y TRAZABILIDAD

---

## 19. Base de Datos de PrinterServices

PrinterServices mantiene su propia base de datos SQLite para persistencia y auditoría.

**Ubicación:** `%AppData%\QuipuNet\printerservice.db`

### Tablas principales

| Tabla | Contenido | Uso en auditoría |
|---|---|---|
| `print_jobs` | Todos los trabajos de impresión | Consultar estado, histórico, errores |
| `print_log` | Historial de eventos por job | Trazabilidad completa de cada impresión |
| `printers` | Catálogo de impresoras | Estado actual, MAC, IP, modelo |
| `config_settings` | Configuraciones del servicio | Valores actuales vs defaults |
| `notifications` | Notificaciones pendientes | gRPC no entregadas |
| `network_config` | Configuración de red esperada | MAC del gateway, subred |
| `notificacionescambiosip` | Historial de cambios de IP | Auditoría de IPs que cambiaron |

### Consultas útiles para auditoría

**Jobs fallidos en las últimas 24 horas:**
```sql
SELECT * FROM print_jobs 
WHERE estado = 'FAILED' 
AND fecha_creacion > datetime('now', '-1 day')
ORDER BY fecha_creacion DESC;
```

**Historial de una impresora específica:**
```sql
SELECT * FROM print_log 
WHERE impresora_nombre = 'Cocina Principal'
ORDER BY fecha DESC
LIMIT 50;
```

**Impresoras con IP auto-resuelta:**
```sql
SELECT * FROM printers 
WHERE ip_resuelta_por_arp = 1;
```

**Notificaciones de cambio de IP pendientes:**
```sql
SELECT * FROM notificacionescambiosip 
WHERE estado = 'PENDIENTE';
```

---

## 20. Tabla pedidoprinterjob (QuipuNetX)

En QuipuNetX existe una tabla dedicada que relaciona pedidos con jobs de PrinterServices:

| Campo | Descripción |
|---|---|
| `PEDIDOID` | ID del pedido en QuipuNet |
| `JOBID` | ID del job en PrinterServices |
| `STATUS` | Estado: `SENT`, `ERROR`, `PRINTED` |
| `FECHACREACION` | Fecha/hora de creación |
| `MODALIDAD` | Tipo de operación (venta, delivery, etc.) |
| `DELIVERYID` | ID del delivery (si aplica) |

### Consultas útiles

**Verificar si un pedido tiene job asignado:**
```sql
SELECT * FROM pedidoprinterjob WHERE PEDIDOID = '1772';
```

**Listar todos los jobs de una sesión:**
```sql
SELECT * FROM pedidoprinterjob 
WHERE FECHACREACION > '2026-03-15 00:00:00'
ORDER BY FECHACREACION DESC;
```

---

## 21. Flujo Completo de Trazabilidad

Para rastrear una impresión de punta a punta:

### Paso 1: Encontrar el job del pedido

En la base de datos de QuipuNetX:
```sql
SELECT * FROM pedidoprinterjob WHERE PEDIDOID = '1772';
-- Resultado: JOBID = "714", STATUS = "SENT"
```

### Paso 2: Verificar el estado del job en PrinterServices

```
GET http://localhost:8090/api/job/714
```

O en la base de datos de PrinterServices:
```sql
SELECT * FROM print_jobs WHERE job_id = '714';
-- Verifica: estado, reintentos, error_mensaje, fecha_impresion
```

### Paso 3: Ver el historial del job

```sql
SELECT * FROM print_log WHERE job_id = '714' ORDER BY fecha;
-- Muestra todos los eventos: PENDING → PRINTING → DONE/FAILED
```

### Paso 4: Verificar el estado de la impresora

```
GET http://localhost:8090/api/printer/status/IMP-001
```

---

# PARTE 7: MODELOS DE IMPRESORA SOPORTADOS

---

## 22. Impresoras Compatibles

PrinterServices soporta múltiples marcas y modelos de impresoras térmicas ESC/POS:

| Driver | Modelos soportados | Comando de corte | Verificación de estado |
|---|---|---|---|
| **Epson** | TM-T20, TM-T88, TM-U220 | Corte parcial | DLE EOT + SNMP |
| **Star** | TSP, BSC10 | Corte completo | ASB (Auto Status Back) |
| **Bixolon** | SRP-270, SRP-350, SRP-720 | Corte parcial | DLE EOT |
| **Genérica** | Chinas, CBX, ZKT y otras | Corte parcial | DLE EOT |

### Conexiones soportadas

| Tipo | Descripción | Puerto |
|---|---|---|
| **Ethernet (TCP)** | Impresora de red (principal) | 9100 |
| **USB** | Impresora USB (vía spooler Windows) | N/A |
| **Serial (COM)** | Impresora por puerto serial | COM port |

> **Nota:** La mayoría de restaurantes usan impresoras Ethernet (TCP:9100). Las conexiones USB y Serial son para casos especiales.

---

# PARTE 8: INSTALACIÓN Y MANTENIMIENTO

---

## 23. Instalación de PrinterServices

PrinterServices se instala como servicio de Windows usando TopShelf:

### Comandos de instalación

```cmd
REM Instalar el servicio
PrinterServices.exe install

REM Iniciar el servicio
PrinterServices.exe start

REM Detener el servicio
PrinterServices.exe stop

REM Desinstalar el servicio
PrinterServices.exe uninstall
```

### Verificar instalación

1. Abrir `services.msc`
2. Buscar "PrinterServices"
3. Verificar que esté en "Automático" (inicia con Windows)
4. Verificar que esté "En ejecución"
5. Probar: `GET http://localhost:8090/api/health`

### Dependencias

PrinterServices necesita:
- **.NET Framework 4.5.2** o superior (viene con Windows 10/11)
- **QuipuNetX.dll** accesible en la ruta configurada
- Puertos **8090**, **50051**, **9999** disponibles

---

## 24. Mantenimiento Preventivo

### Verificaciones diarias (recomendadas)

1. **Health check:** `GET /api/health` — Verificar que responda
2. **Estado de impresoras:** `GET /api/printer/status` — Todas online
3. **Jobs pendientes:** `GET /api/jobs/pending` — Debería estar vacío al final del día

### Verificaciones semanales

1. Revisar logs de PrinterServices buscando errores recurrentes
2. Verificar espacio en disco (la BD SQLite crece con el historial)
3. Verificar que no haya notificaciones de cambio de IP fallidas

### Verificaciones mensuales

1. Revisar configuraciones: `GET /api/config` — valores esperados
2. Limpiar historial antiguo de `print_log` si la BD crece mucho
3. Verificar que el servicio se recupere tras reiniciar Windows

---

# PARTE 9: REFERENCIA RÁPIDA

---

## 25. Tabla de Referencia Rápida de API

| Acción | Método | Endpoint |
|---|---|---|
| ¿Servicio vivo? | `GET` | `/api/health` |
| Estado de impresoras | `GET` | `/api/printer/status` |
| Estado de una impresora | `GET` | `/api/printer/status/{id}` |
| Jobs pendientes | `GET` | `/api/jobs/pending` |
| Detalle de un job | `GET` | `/api/job/{jobId}` |
| Reintentar job | `POST` | `/api/job/{jobId}/retry` |
| Ver configuración | `GET` | `/api/config` |
| Ver un setting | `GET` | `/api/config/{key}` |
| Cambiar un setting | `PUT` | `/api/config` |
| Resetear un setting | `POST` | `/api/config/{key}/reset` |
| Resetear todos | `POST` | `/api/config/reset-all` |

---

## 26. Glosario de Términos

| Término | Definición |
|---|---|
| **Backend** | La parte del sistema que procesa lógica de negocio (QuipuNetX) |
| **Frontend / Front** | La interfaz visual del POS que usa el operador (QuipuNet.exe) |
| **Feature Flag** | Interruptor para activar/desactivar funcionalidades |
| **Job** | Un trabajo de impresión registrado en PrinterServices |
| **ESC/POS** | Lenguaje de comandos estándar para impresoras térmicas |
| **gRPC** | Protocolo de comunicación para notificaciones en tiempo real |
| **REST API** | Interfaz de programación accesible vía HTTP (navegador/Postman) |
| **SQLite** | Base de datos ligera que se guarda como archivo local |
| **DHCP** | Protocolo que asigna IPs automáticamente (puede cambiar IPs) |
| **MAC Address** | Dirección física única de un dispositivo de red (no cambia) |
| **ARP** | Protocolo que resuelve IPs a MACs y viceversa |
| **SNMP** | Protocolo para monitorear dispositivos de red |
| **DLE EOT** | Comando ESC/POS para consultar estado de la impresora |
| **TCP:9100** | Protocolo y puerto estándar para enviar datos a impresoras |
| **TopShelf** | Herramienta que permite instalar un programa como servicio Windows |
| **Comanda** | Orden de producción para cocina/barra con los platos del pedido |
| **Comprobante** | Boleta o factura de venta |
| **Precuenta** | Documento previo al cobro que muestra el detalle al comensal |
| **Servidor** | La PC principal que coordina todas las terminales del restaurante |
| **Cliente** | Terminal POS adicional (caja, salón, etc.) conectada al servidor |
| **Callback** | Respuesta asíncrona que llega después de una operación |
| **Cola** | Lista de trabajos que se procesan en orden (primero en entrar, primero en salir) |
| **Singleton** | Instancia única de un componente en todo el sistema |
| **Retry / Reintento** | Volver a intentar una operación que falló |
| **Timeout** | Tiempo máximo de espera antes de declarar una operación como fallida |

---

## 27. Checklist de Soporte — Paso a Paso

### Checklist: "El cliente reporta que no se imprimen las comandas"

- [ ] 1. ¿PrinterServices está corriendo? → `GET http://[IP]:8090/api/health`
- [ ] 2. ¿El flag está activo? → Verificar `config_fla.cfg` → `USAR_PRINTER_SERVICE=true`
- [ ] 3. ¿La impresora está online? → `GET http://[IP]:8090/api/printer/status`
- [ ] 4. ¿Tiene papel? → Campo `tiene_papel` en la respuesta anterior
- [ ] 5. ¿La tapa está cerrada? → Campo `tapa_abierta` = false
- [ ] 6. ¿Hay jobs pendientes? → `GET http://[IP]:8090/api/jobs/pending`
- [ ] 7. ¿El puerto 8090 está accesible? → Verificar firewall
- [ ] 8. ¿QuipuNet servidor está corriendo? → Verificar proceso en el servidor
- [ ] 9. ¿La IP de la impresora es correcta? → Comparar con configuración física
- [ ] 10. ¿Hay errores en los logs? → Revisar archivos `.log` de PrinterServices

### Checklist: "El cliente reporta impresiones duplicadas"

- [ ] 1. ¿El flag `USAR_PRINTER_SERVICE` está en el mismo estado en todas las terminales?
- [ ] 2. ¿Hay jobs duplicados? → Verificar `print_jobs` por `pedido_ids`
- [ ] 3. ¿El usuario reimprimió manualmente? → Verificar tabla `pedidoprinterjob`
- [ ] 4. ¿Hay múltiples instancias de QuipuNet intentando imprimir?

### Checklist: "El POS muestra error de impresión"

- [ ] 1. ¿Qué mensaje exacto muestra? (tomar screenshot)
- [ ] 2. Si dice "PrinterServiceLCA no está activo" → PrinterServices no responde
- [ ] 3. Verificar health check
- [ ] 4. Verificar conectividad de red entre servidor y PrinterServices
- [ ] 5. Verificar que IP y puerto en `config_fla.cfg` sean correctos

---

## 28. Contacto Escalación

Si el problema persiste después de seguir los pasos de diagnóstico:

1. **Recopilar información:**
   - Screenshot del error
   - Resultado de `GET /api/health`
   - Resultado de `GET /api/printer/status`
   - Resultado de `GET /api/jobs/pending`
   - Últimas 50 líneas de logs de PrinterServices
   - Contenido de `config_fla.cfg`
   - Modelo y marca de la impresora

2. **Escalar al equipo de desarrollo** con toda la información recopilada

---

*Documento generado el 18 de marzo de 2026*  
*Manual Técnico PrinterServices v1.0*
