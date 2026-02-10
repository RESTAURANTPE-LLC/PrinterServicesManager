# Vibe Engineering — PrinterServices

> Documento guía para el diseño, arquitectura y desarrollo del proyecto **PrinterServices**.
> Última actualización: 2026-02-10

---

## 1. Problema

Las comandas de producción (cocina, bar, etc.) a veces **no se imprimen** y nadie se entera.
Causas raíz identificadas:

- No hay verificación de estado de la impresora antes de enviar datos
- No hay reintentos automáticos ante fallos TCP (timeout, puerto ocupado, buffer lleno)
- No hay notificación al POS cuando una comanda falla
- Múltiples POS compiten por el puerto 9100 de la misma impresora (conflictos de socket)
- Los comandos de corte ESC/POS varían por modelo y no siempre son correctos
- Si `Quipunet.exe` se cierra o falla durante la impresión, los trabajos se pierden
- No hay monitoreo proactivo del estado de las impresoras (papel, tapa, conexión)

## 2. Visión

> "Toda comanda se imprime. Si no se puede, el POS se entera inmediatamente."

**PrinterServices.exe** es un **proyecto nuevo** — un servicio Windows centralizado que:

1. Recibe trabajos de impresión **exclusivamente del Quipunet.exe Servidor** vía HTTP REST
2. Los encola con persistencia en SQLite (GestorDeColas)
3. Los imprime con reintentos y verificación de estado (DLE EOT)
4. Notifica al Servidor vía gRPC el resultado (`notifyToServerStatusPrinter`)
5. Notifica al Cliente que generó el pedido vía gRPC (`notifyToPrinterListener`)

---

## 3. Decisiones Técnicas

| Decisión | Elección | Justificación |
|----------|----------|---------------|
| Framework | **.NET Framework 4.5.2** | Compatibilidad con QuipuNetX.dll |
| Comunicación Servidor→Service | **HTTP REST** (HttpListener self-hosted) | Reutiliza objetos `Impresion` vía JSON, depurable con Postman |
| Notificación Service→Servidor | **gRPC** (Grpc.Core 2.46.x) | Streaming bidireccional, baja latencia |
| Notificación Service→Cliente | **gRPC** (Grpc.Core 2.46.x) | Push directo al `PrinterListenerSent` del cliente |
| Cola de impresión | **ConcurrentQueue + SemaphoreSlim** | Async-friendly, disponible en 4.5.2 |
| Persistencia | **SQLite** | Mismo patrón que SugarDb del proyecto principal |
| Instalación | **TopShelf** | Fácil: `PrinterServices.exe install/start/stop` |
| Descubrimiento | **Broadcast UDP + IP fija** | Auto-descubrimiento en LAN, IP fija como fallback |
| Logging | **log4net** | Consistente con ecosistema existente |
| Despliegue | **Central (1 en la red)** | Un solo servicio gestiona todas las impresoras |

### Decisión crítica: SQLite autónomo — sin instancia de QuipuNetX.dll

> **PrinterServices NO ejecuta ninguna instancia de QuipuNetX.dll.**
> No existe `local_id`, no existe contexto de seguridad, no existe `SugarDb.getInstance()`,
> no existe ningún singleton ni servicio de QuipuNetX corriendo dentro de este proceso.

**Lo que SÍ se reutiliza** (solo como clases/estructuras de datos deserializadas desde JSON):
- `Impresion` — objeto de impresión recibido vía HTTP
- `Impresora` — configuración de impresora
- `Respuesta` — resultado de operaciones
- `Linea` — estructura de línea de impresión (formato estructurado)
- `Definitions` — constantes

**Lo que NO se reutiliza** (es código propio de PrinterServices):
- `Data/Orm/PSQLite.cs` — ORM sqlite-net limpio, namespace `PSQLite`, CERO refs a QuipuNetX
- `PrinterServiceDb` — hereda `PSQLite.SQLiteConnection`, BD propia (`printerservice.db`)
- Todas las entities usan `using PSQLite;` (NUNCA `using SQLite;`)
- Esta es la **única duplicación de código aceptable** en todo el proyecto

```
QuipuNetX.dll (referencia)            PrinterServices.exe (autónomo)
─────────────────────────             ─────────────────────────────
namespace SQLite (acoplado)           namespace PSQLite (limpio)
  ├── Util.Capture, Logquipu           ├── Sin deps de QuipuNetX
  ├── FeatureFlagConfigReader           ├── PRAGMAs hardcoded sensatos
  ├── SQLiteQueryMonitor                ├── P/Invoke directo a sqlite3.dll
  └── Security.LocalIdActual            └── Solo ORM puro

SugarDb → quipunet.db                 PrinterServiceDb → printerservice.db
  ├── local_id, security               ├── NO local_id, NO security
  ├── tablas de negocio                 ├── print_jobs, printers, config_settings
  └── instancia global                  └── instancia propia, independiente

Solo se reutilizan CLASES como DTOs:
  Impresion, Impresora, Respuesta, Linea, Definitions
  (deserializadas desde JSON, no instanciadas desde la DLL)
```

---

## 4. Diagrama de Arquitectura del Sistema

```
                        ┌─────────────┐
                        │   Cloud     │
                        │ Restaurantpe│
                        └──────┬──────┘
                               │
                    ┌──────────┴────────────────────┐
                    │                               │
                    │    MonitoreoRemotoDeImpresiones│
                    │    (api/getStatusPrinters)     │
                    │                               │
                    └──────────┬────────────────────┘
                               │
┌─────────────────────────┐    │    ┌─────────────────────────────┐
│ Quipunet.exe (Cliente)  │    │    │   PuertoInverso             │
│ 10.0.0.25               │    │    │   (Proyecto Existente)      │
│                         │    │    │   Quipunet.exe is Alive     │
│  ┌───────────────────┐  │    │    └─────────────────────────────┘
│  │                   │  │    │
│  │  updateUI         │  │    │
│  │                   │  │    │
│  ├───────────────────┤  │    │
│  │PrinterListenerSent│◄─┼────┼──── gRPC: notifyToPrinterListener(10.0.0.25)
│  └───────────────────┘  │    │
│                         │    │
└──────────┬──────────────┘    │
           │                   │
           │ enviaPedido(10.0.0.25, deviceId)
           │                   │
           ▼                   │
┌──────────────────────────────┴──────────────────────┐
│  Quipunet.exe (Servidor)                            │
│  10.0.0.21                                          │
│                                                     │
│  Keep Alive                                         │
│  PrinterServices (referencia)                       │
│  PuertoInverso                                      │
│                                                     │
│  ★ ÚNICO que envía impresiones a PrinterServices ★  │
└──────────┬──────────────────────────────────────────┘
           │
           │ Async HTTP REST: enviarComandas(id)
           │ POST /api/print/comandas
           │
           ▼
┌─────────────────────────────────────────────────────┐
│                                                     │
│              PrinterServices.exe                    │
│              (Proyecto Nuevo)                       │
│              Puerto HTTP: 8090                      │
│              Puerto gRPC: 50051                     │
│                                                     │
│  ┌───────────────────────────────────────────────┐  │
│  │              HTTP API (HttpListener)           │  │
│  │  POST /api/print/comanda                      │  │
│  │  POST /api/print/comandas                     │  │
│  │  GET  /api/printer/status                     │  │
│  │  GET  /api/notifications/{deviceId}           │  │
│  │  GET  /api/health                             │  │
│  └───────────────┬───────────────────────────────┘  │
│                  │                                   │
│  ┌───────────────▼───────────────────────────────┐  │
│  │         PrintJobManager (GestorDeColas)        │  │
│  │  Cola persistente + retry + estado             │  │
│  │  ConcurrentQueue + SemaphoreSlim + SQLite      │  │
│  └───────────────┬───────────────────────────────┘  │
│                  │                                   │
│  ┌───────────────▼───────────────────────────────┐  │
│  │           IPrinterDriver                       │  │
│  │  (Abstracción por modelo de impresora)         │  │
│  ├──────────┬──────────┬──────────┬──────────────┤  │
│  │ EpsonDrv │ StarDrv  │BixolonDrv│GenericEscPos │  │
│  │          │          │          │              │  │
│  │ Corte:   │ Corte:   │ Corte:   │ Corte:      │  │
│  │ 1D 56 01 │ 1B 64 02 │ 1D 56 42│ 1D 56 42 00 │  │
│  │          │          │          │              │  │
│  │ Status:  │ Status:  │ Status:  │ Status:      │  │
│  │ DLE EOT  │ ASB      │ DLE EOT  │ DLE EOT     │  │
│  ├──────────┴──────────┴──────────┴──────────────┤  │
│  │           ITransport                           │  │
│  │  (Capa de comunicación física)                 │  │
│  ├──────────┬──────────┬──────────┬──────────────┤  │
│  │ TcpTrans │ UsbTrans │SerialTrns│ BleTrans     │  │
│  │ +Retry   │          │          │              │  │
│  │ +Timeout │          │          │              │  │
│  │ +Pool    │          │          │              │  │
│  └──────────┴──────────┴──────────┴──────────────┘  │
│                                                     │
│  ┌─────────────────┐  ┌──────────────────────────┐  │
│  │ StatusMonitor   │  │ NotificationManager      │  │
│  │ (Timer 15s)     │  │ - gRPC → Servidor        │  │
│  │ checkStatus     │  │ - gRPC → Cliente         │  │
│  │ Printer         │  │   (PrinterListenerSent)  │  │
│  └────────┬────────┘  └──────────────────────────┘  │
│           │                                         │
│  ┌──────────────────────────────────────────────┐   │
│  │ ★ NetworkWatcher (PROCESO PARALELO)          │   │
│  │   Thread dedicado — NO toca la cola          │   │
│  │   - Verifica MAC del gateway cada 30s        │   │
│  │   - ARP scan: descubre MACs de impresoras    │   │
│  │   - Auto-resuelve IP si MAC cambió (DHCP)    │   │
│  │   - Genera NetworkAlert (fuertemente tipado) │   │
│  └──────────────────────────────────────────────┘   │
│           │                                         │
│  ┌────────▼──────────────────────────────────────┐  │
│  │  printerservice.db (SQLite)                    │  │
│  │  - print_jobs (cola persistente)               │  │
│  │  - print_log (historial / PrinterLog)          │  │
│  │  - printers (registro + mac_address)           │  │
│  │  - notifications (pendientes de entregar)      │  │
│  │  - network_config (MAC gateway esperado)       │  │
│  └───────────────────────────────────────────────┘  │
│                                                     │
│  ┌───────────────────────────────────────────────┐  │
│  │  gRPC Server (Grpc.Core, puerto 50051)         │  │
│  │  - notifyToServerStatusPrinter(10.0.0.21)     │  │
│  │  - notifyToPrinterListener(10.0.0.25)         │  │
│  │  - api/getStatusPrinters → Cloud              │  │
│  └───────────────────────────────────────────────┘  │
│                                                     │
│  ┌───────────────────────────────────────────────┐  │
│  │  UDP Discovery Server (:9999)                  │  │
│  └───────────────────────────────────────────────┘  │
│                                                     │
└──────────────────────────┬──────────────────────────┘
                           │
              ┌────────────┼────────────┐
              ▼            ▼            ▼
         ┌─────────┐ ┌─────────┐ ┌─────────┐
         │Impresora│ │Impresora│ │Impresora│
         │ Cocina  │ │  Bar    │ │  Caja   │
         │TCP:9100 │ │TCP:9100 │ │TCP:9100 │
         └─────────┘ └─────────┘ └─────────┘
```

---

## 5. Flujo de Impresión Completo

```
1. Cliente (10.0.0.25) genera un pedido
   └→ enviaPedido(10.0.0.25, deviceId) al Servidor (10.0.0.21)

2. Servidor (10.0.0.21) procesa el pedido
   └→ Genera objeto(s) Impresion con la cadena de texto/HTML
   └→ POST /api/print/comandas → PrinterServices.exe (HTTP REST)
      Body: List<Impresion> serializado con Newtonsoft.Json

3. PrinterServices.exe recibe la petición
   └→ HTTP API deserializa los objetos Impresion (de QuipuNetX.dll)
   └→ Crea PrintJob por cada Impresion
   └→ Persiste en SQLite (estado: PENDING)
   └→ Encola en PrintJobManager (ConcurrentQueue)
   └→ Responde 200 OK con jobIds al Servidor

4. PrintWorker consume la cola
   └→ Agrupa jobs por impresora IP (serializa por impresora)
   └→ Para cada job:
       a. StatusMonitor → DLE EOT → ¿impresora lista?
          - Si offline → marca WAITING, notifica, reintenta cuando vuelva
          - Si sin papel → notifica SIN_PAPEL
          - Si OK → continúa
       b. Selecciona IPrinterDriver según modelo (Epson, Star, Bixolon, etc.)
       c. Driver genera bytes ESC/POS (texto + corte correcto para el modelo)
       d. ITransport envía bytes por TCP:9100 (con retry + timeout)
       e. Si éxito → estado DONE
       f. Si falla → retry (3x con backoff 500ms/1s/2s)
       g. Si falla todo → estado FAILED
       h. Registra en PrinterLog (SQLite)

5. NotificationManager envía resultados
   └→ gRPC notifyToServerStatusPrinter(10.0.0.21):
      "Comanda X impresa en Cocina" o "Comanda X FALLÓ en Cocina (3 reintentos)"
   └→ gRPC notifyToPrinterListener(10.0.0.25):
      Notifica directamente al Cliente que generó el pedido
   └→ Cliente → PrinterListenerSent → updateUI
      (Muestra estado en pantalla: ✅ impresa / ❌ falló / ⚠ sin papel)

6. MonitoreoRemotoDeImpresiones (Cloud)
   └→ GET api/getStatusPrinters desde la nube
   └→ PrinterServices responde con estado de todas las impresoras
```

---

## 6. Arquitectura de Capas de Impresión (Detalle)

### Capa 1: PrintJobManager (GestorDeColas)

Responsabilidad: Recibir, encolar, persistir y despachar trabajos de impresión.

```
PrintJobManager
├── Enqueue(PrintJob job)         → Encola + persiste en SQLite
├── Dequeue()                     → Consume siguiente job (async)
├── Retry(string jobId)           → Re-encola un job fallido
├── GetPending()                  → Lista jobs pendientes
├── GetByStatus(JobStatus status) → Filtra por estado
└── Properties:
    ├── ConcurrentQueue<PrintJob> _queue      (in-memory)
    ├── SemaphoreSlim _signal                 (señalización async)
    └── PrintJobRepository _repo              (SQLite backup)
```

Estados del job: `PENDING → PRINTING → DONE | FAILED | WAITING`

### Capa 2: IPrinterDriver (Abstracción por modelo)

Responsabilidad: Generar los bytes ESC/POS correctos según el modelo de impresora.

```csharp
public interface IPrinterDriver
{
    string ModelName { get; }
    byte[] GetInitSequence();
    byte[] GetCutCommand(CutType type);     // Parcial o Total
    byte[] GetStatusQuery();                 // DLE EOT o ASB
    PrinterStatus ParseStatusResponse(byte[] response);
    byte[] GetCashDrawerCommand();
    byte[] GetTextCommand(string text, PrintStyle style);
    byte[] GetBitmapCommand(byte[] bitmapData);
    byte[] GetQrCodeCommand(string data, int moduleSize);
    byte[] GetFeedCommand(int lines);
}
```

| Driver | Modelos soportados | Corte | Status |
|--------|-------------------|-------|--------|
| `EpsonDriver` | TM-T20, TM-T88, TM-U220 | `0x1D 0x56 0x01` | DLE EOT |
| `StarDriver` | TSP, BSC10 | `0x1B 0x64 0x02` | ASB (Automatic Status Back) |
| `BixolonDriver` | SRP-270, SRP-350, SRP-720 | `0x1D 0x56 0x42` | DLE EOT |
| `GenericEscPosDriver` | Chinas genéricas, CBX, ZKT | `0x1D 0x56 0x42 0x00` | DLE EOT |

Selección del driver: usa `Impresion.Printermodel` (mismo campo que `PrintUtil.getPrinterObjectByModel()`).

### Capa 3: ITransport (Comunicación física)

Responsabilidad: Enviar y recibir bytes a/desde la impresora física.

```csharp
public interface ITransport : IDisposable
{
    Task<bool> ConnectAsync(CancellationToken ct);
    Task SendAsync(byte[] data, CancellationToken ct);
    Task<byte[]> ReceiveAsync(int length, int timeoutMs, CancellationToken ct);
    bool IsConnected { get; }
    void Disconnect();
}
```

| Transporte | Clase | Configuración | Retry |
|-----------|-------|---------------|-------|
| **TCP** (Ethernet) | `TcpTransport` | IP:Puerto (default 9100) | 3x con backoff + connection pool |
| **Serial** (COM) | `SerialTransport` | COM port, baud rate | Reconexión automática |
| **USB** | `UsbTransport` | Nombre impresora (spooler) | Via servicio Windows |
| **BLE** | `BleTransport` | MAC address | Futuro (no implementar aún) |

`TcpTransport` detalle de retry:
```
Intento 1: conectar + enviar → si falla, esperar 500ms
Intento 2: reconectar + enviar → si falla, esperar 1000ms
Intento 3: reconectar + enviar → si falla, marcar FAILED
```

---

## 7. Dependencia crítica: QuipuNetX.dll

El proyecto **referencia directamente** `QuipuNetX.dll` para reutilizar objetos existentes.

| Clase | Namespace | Uso en PrinterServices |
|-------|-----------|------------------------|
| `Impresion` | `QuipuNetX.entity.extras` | Objeto principal que llega por HTTP REST |
| `Impresora` | `QuipuNetX.entity` | Configuración de impresora (modelo, IP, modo) |
| `Respuesta` | `QuipuNetX.util` | Resultado de operaciones (tipo + mensajes) |
| `Definitions` | `QuipuNetX.util` | Constantes: MODOIMPRESION_ETHERNET, modelos, etc. |
| `Pedido` | `QuipuNetX.entity.extras` | Productos dentro de una Impresion |
| `HistorialImpresion` | `QuipuNetX.entity.extras` | Registro de historial |
| `Logimpresion` | `QuipuNetX.entity` | Log de impresiones exitosas |

**Ruta del DLL**: `quipu\QuipuNetX\bin\Debug\QuipuNetX.dll`

Referencia en `.csproj`:
```xml
<Reference Include="QuipuNetX">
  <HintPath>..\..\quipu\QuipuNetX\bin\Debug\QuipuNetX.dll</HintPath>
  <Private>True</Private>
</Reference>
```

---

## 8. Comunicación gRPC (Notificaciones)

### Service → Servidor (Quipunet.exe 10.0.0.21)

```
notifyToServerStatusPrinter:
  - Comanda impresa exitosamente
  - Comanda falló después de N reintentos
  - Impresora cambió de estado (online/offline/sin papel)
```

### Service → Cliente (Quipunet.exe 10.0.0.25)

```
notifyToPrinterListener:
  - Resultado de la comanda que ESE cliente generó
  - El Cliente tiene un PrinterListenerSent que actualiza la UI
```

### Contrato gRPC (Grpc.Core 2.46.x para .NET 4.5.2)

```protobuf
syntax = "proto3";
package printerservices;

service PrinterNotification {
  // Stream: el Servidor se suscribe a notificaciones de estado
  rpc SuscribirNotificacionesServidor(SuscripcionRequest)
      returns (stream NotificacionEvent);

  // Stream: un Cliente se suscribe a notificaciones de sus comandas
  rpc SuscribirNotificacionesCliente(SuscripcionRequest)
      returns (stream NotificacionEvent);

  // Unary: consultar estado de todas las impresoras
  rpc GetStatusPrinters(Empty) returns (StatusPrintersResponse);
}

message SuscripcionRequest {
  string device_id = 1;
  string ip = 2;
  string rol = 3;    // "SERVIDOR" o "CLIENTE"
}

message NotificacionEvent {
  string tipo = 1;              // IMPRESA, FALLIDA, OFFLINE, ONLINE, SIN_PAPEL
  string comanda_id = 2;
  string impresora_id = 3;
  string impresora_nombre = 4;
  string device_id_origen = 5;  // quién generó el pedido original
  string ip_origen = 6;
  string mensaje = 7;
  string timestamp = 8;
  int32 reintentos = 9;
  string job_id = 10;
}

message StatusPrintersResponse {
  repeated PrinterStatusInfo printers = 1;
}

message PrinterStatusInfo {
  string impresora_id = 1;
  string nombre = 2;
  string ip = 3;
  bool online = 4;
  bool tiene_papel = 5;
  bool tapa_abierta = 6;
  string ultimo_check = 7;
  int32 jobs_pendientes = 8;
}

message Empty {}
```

---

## 9. HTTP REST API

Self-hosted con `System.Net.HttpListener` (incluido en .NET 4.5.2).

**Solo el Servidor (10.0.0.21) llama a estos endpoints.**

| Método | Ruta | Descripción | Body/Response |
|--------|------|-------------|---------------|
| `POST` | `/api/print/comanda` | Enviar 1 comanda | `Impresion` → `{ jobId, aceptada }` |
| `POST` | `/api/print/comandas` | Enviar lote de comandas | `List<Impresion>` → `{ jobIds[], count }` |
| `POST` | `/api/print/venta` | Ticket de venta | `Impresion` → `{ jobId, aceptada }` |
| `POST` | `/api/print/precuenta` | Precuenta | `Impresion` → `{ jobId, aceptada }` |
| `GET` | `/api/printer/status` | Estado de todas las impresoras | → `List<PrinterStatus>` |
| `GET` | `/api/printer/status/{id}` | Estado de una impresora | → `PrinterStatus` |
| `GET` | `/api/job/{jobId}` | Estado de un trabajo | → `PrintJobStatus` |
| `GET` | `/api/jobs/pending` | Trabajos pendientes | → `List<PrintJobStatus>` |
| `POST` | `/api/job/{jobId}/retry` | Reintentar job fallido | → `Respuesta` |
| `POST` | `/api/printer/register` | Registrar/actualizar impresora | `Impresora` → `Respuesta` |
| `GET` | `/api/health` | Health check | → `{ status, uptime, version, printersOnline }` |
| `GET` | `/api/config` | Todos los settings centralizados | → `{ count, settings[] }` |
| `GET` | `/api/config/{key}` | Un setting específico | → `{ key, value, defaultValue, ... }` |
| `GET` | `/api/config/category/{cat}` | Settings por categoría | → `{ category, settings[] }` |
| `PUT` | `/api/config` | Actualizar un setting (validado) | `{ key, value }` → `{ status, key, value }` |
| `PUT` | `/api/config/batch` | Actualizar múltiples settings | `{ settings: {k:v,...} }` → `{ status, count }` |
| `POST` | `/api/config/{key}/reset` | Resetear a valor default | → `{ status, key, value }` |
| `POST` | `/api/config/reset-all` | Resetear todos a defaults | → `{ status: "ALL_RESET" }` |
| `GET` | `/api/network/status` | Estado de red y resolución MAC | → `NetworkStatusResponse` |

---

## 10. Esquema SQLite — `printerservice.db`

**Ubicación**: `%AppData%\QuipuNet\printerservice.db`

Sigue el patrón de `SugarDb.getInstance()` en `quipu\QuipuNetX\sugar\Com\Orm\SugarDb.cs`.

### PRAGMAs (al crear conexión)

```sql
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA page_size=4096;
PRAGMA cache_size=10000;
```

Soporta `config_fla.cfg` si `SQLITE_CONFIG_CUSTOM=true` (mismo patrón que `FeatureFlagConfigReader`).

### Tablas

```sql
CREATE TABLE IF NOT EXISTS print_jobs (
    job_id              TEXT PRIMARY KEY,
    comanda_id          TEXT,
    impresora_id        TEXT NOT NULL,
    impresora_ip        TEXT NOT NULL,
    impresora_nombre    TEXT,
    printer_model       TEXT,
    modo_impresion      TEXT,
    contenido           TEXT,
    contenido_html      TEXT,
    device_id_origen    TEXT,
    ip_origen           TEXT,
    copias              INTEGER DEFAULT 1,
    prioridad           INTEGER DEFAULT 2,
    estado              TEXT DEFAULT 'PENDING',
    reintentos          INTEGER DEFAULT 0,
    max_reintentos      INTEGER DEFAULT 3,
    error_mensaje       TEXT,
    fecha_creacion      TEXT,
    fecha_impresion     TEXT,
    tipo_impresion      TEXT,
    lineas_imprimir_json TEXT,          -- JSON array de Linea (formato estructurado)
    tamanio_letra       TEXT,           -- "1","2","3" → setLetterSize
    abre_gaveta         INTEGER DEFAULT 0,
    tipo_generacion     TEXT,           -- tipogeneracion de Impresion
    qr_data             TEXT,           -- datos para QR code
    codigo_corte        TEXT            -- código de corte personalizado
);

CREATE TABLE IF NOT EXISTS print_log (
    log_id              INTEGER PRIMARY KEY AUTOINCREMENT,
    job_id              TEXT,
    impresora_id        TEXT,
    impresora_nombre    TEXT,
    impresora_ip        TEXT,
    estado              TEXT,
    mensaje             TEXT,
    reintentos          INTEGER,
    fecha               TEXT,
    device_id_origen    TEXT
);

CREATE TABLE IF NOT EXISTS printers (
    impresora_id        TEXT PRIMARY KEY,
    nombre              TEXT,
    ip                  TEXT,
    puerto              INTEGER DEFAULT 9100,
    mac_address         TEXT,              -- ★ MAC fija, identifica la impresora física
    modelo              TEXT,
    modo_impresion      TEXT,
    estado_online       INTEGER DEFAULT 0,
    tiene_papel         INTEGER DEFAULT 1,
    tapa_abierta        INTEGER DEFAULT 0,
    ip_resuelta_por_arp INTEGER DEFAULT 0, -- 1 si la IP fue auto-resuelta por ARP
    ultimo_check        TEXT,
    fecha_registro      TEXT
);

-- Tabla: network_config (red esperada — NetworkWatcher)
CREATE TABLE IF NOT EXISTS network_config (
    config_id           INTEGER PRIMARY KEY,
    gateway_mac         TEXT NOT NULL,      -- ★ MAC del router correcto (inmutable)
    gateway_ip          TEXT,               -- IP del gateway (puede cambiar)
    network_name        TEXT,               -- SSID o nombre de red
    subnet              TEXT,               -- Ej: "10.0.0"
    auto_learned        INTEGER DEFAULT 1,  -- 1 si se aprendió automáticamente
    fecha_registro      TEXT,
    activa              INTEGER DEFAULT 1
);

CREATE TABLE IF NOT EXISTS notifications (
    notif_id            INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id           TEXT NOT NULL,
    tipo                TEXT NOT NULL,
    comanda_id          TEXT,
    job_id              TEXT,
    impresora_id        TEXT,
    impresora_nombre    TEXT,
    mensaje             TEXT,
    entregada           INTEGER DEFAULT 0,
    fecha               TEXT
);

-- Tabla: config_settings (configuración centralizada dinámica)
CREATE TABLE IF NOT EXISTS config_settings (
    key                 TEXT PRIMARY KEY,
    value               TEXT,
    default_value       TEXT,
    description         TEXT,
    category            TEXT,           -- network, queue, api, monitoring
    value_type          TEXT,           -- int, bool, string
    min_value           TEXT,
    max_value           TEXT,
    updated_at          TEXT
);

CREATE INDEX IF NOT EXISTS idx_jobs_estado ON print_jobs(estado);
CREATE INDEX IF NOT EXISTS idx_jobs_impresora ON print_jobs(impresora_id);
CREATE INDEX IF NOT EXISTS idx_notif_device ON notifications(device_id, entregada);
CREATE INDEX IF NOT EXISTS idx_log_fecha ON print_log(fecha);
CREATE INDEX IF NOT EXISTS idx_printers_mac ON printers(mac_address);
```

---

## 11. Broadcast UDP — Auto-descubrimiento

```
1. PrinterServices.exe arranca → escucha UDP puerto 9999
2. Quipunet.exe (Servidor) arranca → broadcast UDP: "QUIPU_PRINTER_DISCOVERY"
3. PrinterServices.exe responde unicast: "QUIPU_PRINTER_SERVICE|10.0.0.21|8090|50051"
   (IP | puerto HTTP | puerto gRPC)
4. Servidor guarda IP:puertos → usa HTTP REST + gRPC
5. Si no hay respuesta en 3s → usa IP fija de configuración (fallback)
6. Heartbeat: re-descubre cada 60s para detectar cambio de IP
```

---

## 12. Escenarios de Uso

| # | Escenario | Comportamiento |
|---|-----------|----------------|
| 1 | **Happy path** | Servidor POST comanda → PrinterServices imprime → gRPC notifica SUCCESS al Servidor y al Cliente |
| 2 | **Impresora offline** | StatusMonitor detecta → encola como WAITING → notifica OFFLINE → cuando vuelve, imprime automáticamente |
| 3 | **Impresora sin papel** | DLE EOT detecta → notifica SIN_PAPEL al Servidor y Cliente |
| 4 | **Timeout TCP** | Retry 3x con backoff → si falla todo → FAILED → notifica al Servidor y Cliente |
| 5 | **Múltiples comandas, misma impresora** | Cola serializada por IP → sin conflictos de puerto 9100 |
| 6 | **PrinterServices.exe se reinicia** | Lee jobs PENDING/WAITING de SQLite → los reimprime |
| 7 | **Modelo con corte diferente** | IPrinterDriver selecciona bytes correctos por modelo |
| 8 | **10 comandas a 3 impresoras** | Agrupa por IP → paraleliza entre impresoras → serializa por impresora |
| 9 | **Cloud consulta estado** | MonitoreoRemotoDeImpresiones → GET api/getStatusPrinters |
| 10 | **Cliente quiere saber si su comanda se imprimió** | gRPC notifyToPrinterListener → PrinterListenerSent → updateUI |
| 11 | **Servidor cambia de WiFi (gateway MAC diferente)** | NetworkWatcher detecta MAC gateway distinta → `NetworkAlert.GatewayChanged` → alerta al Servidor y Cloud, modo degradado |
| 12 | **Impresora recibió nueva IP por DHCP** | ARP scan: MAC conocida con IP diferente → auto-actualiza `printers.ip` + marca `ip_resuelta_por_arp=1` → sigue imprimiendo |
| 13 | **Nueva impresora desconocida en la red** | ARP scan detecta MAC no registrada respondiendo en :9100 → `NetworkAlert.UnknownPrinterFound` → sugiere registro |

---

## 13. Estrategia de Migración (Fases)

| Fase | Entregable | Criterio de éxito | Estado |
|------|-----------|-------------------|--------|
| **0** | Proyecto creado, TopShelf, BD SQLite, health check | `PrinterServices.exe install && start`, GET /api/health responde | ✅ DONE |
| **1** | HTTP API + PrintJobManager + PrintWorker (happy path) | POST /api/print/comanda → imprime en impresora real | ✅ DONE |
| **2** | EscPosCommandBuilder + LineaParser + formatting avanzado | Bold, size, alignment, QR, barcode, lineasimprimir JSON | ✅ DONE |
| **3** | StatusMonitor (DLE EOT) + WAITING state + pre-check | Detecta offline/sin papel antes de imprimir, re-encola automático | ✅ DONE |
| **C** | ConfigManager centralizado (SQLite-backed, API REST) | GET/PUT /api/config, valores dinámicos sin reiniciar | ✅ DONE |
| **4** | Cola persistente SQLite completa | Reiniciar servicio no pierde jobs pendientes | ✅ DONE |
| **5** | gRPC NotificationManager | Servidor y Cliente reciben notificaciones push | PENDIENTE |
| **6** | UDP Discovery | Servidor descubre PrinterServices automáticamente | PENDIENTE |
| **7** | Feature flag en PrintUtil del Servidor | `USAR_PRINTER_SERVICE=true` → delega al servicio | PENDIENTE |
| **8** | NetworkWatcher + alertas UI + MonitoreoRemoto | Cloud ve estado, POS muestra indicadores en tiempo real | PENDIENTE |

---

## 14. Articulación con Proyectos Existentes

```
sourcecode/
├── front/                  # Frontend WPF (Quipunet.exe)
│   └── QuipuNet/           # Proyecto principal del POS
│       └── (Referencia futura a PrinterServices.Client.dll)
│
├── quipu/                  # Backend (QuipuNetX.dll)
│   └── QuipuNetX/
│       ├── entity/extras/Impresion.cs     ← Reutilizado por PrinterServices
│       ├── entity/Impresora.cs            ← Reutilizado por PrinterServices
│       ├── Util/Respuesta.cs              ← Reutilizado por PrinterServices
│       ├── Util/print/PrintUtil.cs        ← Será modificado en Fase 7
│       └── sugar/Com/Orm/SugarDb.cs       ← Patrón para PrinterServiceDb
│
└── printerservices/        ★ ESTE PROYECTO NUEVO
    ├── agent.md
    ├── vibe_engeneering_printerservices.md
    ├── PrinterServices.sln
    └── (sub-proyectos...)
```

### Modificación en Fase 7 (PrintUtil.cs del Servidor)

```csharp
// Al inicio de imprimirComandasEthernet():
if (FeatureFlagConfigReader.IsEnabled("USAR_PRINTER_SERVICE"))
{
    var client = PrinterServiceClient.Instance;
    var response = await client.EnviarComandasAsync(impresionComandasList);
    if (OnPrintManagerResponse != null)
        OnPrintManagerResponse(response);
    return;
}
// ... código actual de impresión directa (fallback)
```

---

## 15. NuGet Packages (.NET 4.5.2 compatibles)

```
TopShelf                     4.3.0      # Windows Service
Newtonsoft.Json              13.0.3     # Serialización (ya usado en QuipuNetX)
System.Data.SQLite.Core      1.0.118    # SQLite
log4net                      2.0.15     # Logging
Grpc.Core                   2.46.6      # gRPC server/client
Google.Protobuf              3.21.12    # Serialización protobuf
Grpc.Tools                   2.46.6     # Code-gen desde .proto
```

---

## 16. Configuración Centralizada — ConfigManager

> **Decisión**: Toda la configuración dinámica vive en SQLite (`config_settings`),
> no en `App.config`. Esto permite actualizar valores en caliente via API REST
> sin reiniciar el servicio.

### Arquitectura

```
App.config (solo binding redirects)
     │
ConfigManager (singleton)
     ├── ConcurrentDictionary<string,string> _cache  ← lectura rápida in-memory
     ├── PrinterServiceDb (SQLite)                   ← persistencia
     ├── SeedDefaults()                              ← inserta defaults en primera ejecución
     └── Set(key, value) → actualiza cache + BD simultáneamente
```

### Settings disponibles (sembrados automáticamente)

| Key | Default | Categoría | Tipo | Rango | Descripción |
|-----|---------|-----------|------|-------|-------------|
| `TcpConnectTimeoutMs` | 3000 | network | int | 500-30000 | Timeout conexión TCP a impresoras |
| `TcpSendTimeoutMs` | 5000 | network | int | 1000-30000 | Timeout envío TCP |
| `DefaultPrinterPort` | 9100 | network | int | 1-65535 | Puerto TCP default impresoras |
| `MaxRetries` | 3 | queue | int | 0-10 | Reintentos máximos por job |
| `RetryBackoffBaseMs` | 500 | queue | int | 100-10000 | Base backoff exponencial (ms) |
| `HttpPort` | 8090 | api | int | 1024-65535 | Puerto HTTP API |
| `StatusCheckIntervalSeconds` | 15 | monitoring | int | 5-300 | Intervalo check DLE EOT |
| `GrpcPort` | 50051 | api | int | 1024-65535 | Puerto gRPC |
| `UdpDiscoveryPort` | 9999 | network | int | 1024-65535 | Puerto UDP discovery |
| `NetworkWatcherIntervalSeconds` | 30 | network | int | 10-600 | Intervalo scan de red |
| `ArpScanIntervalSeconds` | 60 | network | int | 15-600 | Intervalo scan ARP |
| `AutoLearnGatewayOnFirstRun` | 1 | network | bool | — | Auto-aprender gateway MAC |

### Uso en código (dinámico, sin campos readonly)

```csharp
// Lectura — siempre obtiene el valor más reciente de la BD
var cfg = ConfigManager.Instance;
int timeout = cfg.GetInt("TcpConnectTimeoutMs", 3000);
bool autoLearn = cfg.GetBool("AutoLearnGatewayOnFirstRun", true);
string value = cfg.GetString("CustomKey", "default");

// Escritura — actualiza cache + BD atómicamente
cfg.Set("TcpConnectTimeoutMs", "5000");
cfg.SetInt("MaxRetries", 5);
cfg.SetBool("AutoLearnGatewayOnFirstRun", false);
```

### API REST — Ejemplos con curl

```bash
# ── Leer todos los settings ──
curl http://localhost:8090/api/config
# → {"count":12,"settings":[{"key":"TcpConnectTimeoutMs","value":"3000",...},...]}

# ── Leer por categoría ──
curl http://localhost:8090/api/config/category/network
# → {"category":"network","settings":[...]}

# ── Leer uno específico ──
curl http://localhost:8090/api/config/MaxRetries
# → {"key":"MaxRetries","value":"3","defaultValue":"3","description":"...","valueType":"int","minValue":"0","maxValue":"10"}

# ── Actualizar un setting (con validación de rango) ──
curl -X PUT http://localhost:8090/api/config -d '{"key":"TcpConnectTimeoutMs","value":"5000"}'
# → {"status":"UPDATED","key":"TcpConnectTimeoutMs","value":"5000"}
# ⚠ Si value < minValue o > maxValue → 400 Bad Request

# ── Actualizar múltiples settings de una vez ──
curl -X PUT http://localhost:8090/api/config/batch \
  -d '{"settings":{"MaxRetries":"5","RetryBackoffBaseMs":"1000","TcpConnectTimeoutMs":"4000"}}'
# → {"status":"UPDATED","count":3}

# ── Resetear un setting a su default ──
curl -X POST http://localhost:8090/api/config/MaxRetries/reset
# → {"status":"RESET","key":"MaxRetries","value":"3"}

# ── Resetear TODOS a defaults ──
curl -X POST http://localhost:8090/api/config/reset-all
# → {"status":"ALL_RESET"}
```

### App.config (mínimo — solo binding redirects)

```xml
<configuration>
  <runtime>
    <assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1">
      <dependentAssembly>
        <assemblyIdentity name="Newtonsoft.Json" publicKeyToken="30ad4fe6b2a6aeed" culture="neutral" />
        <bindingRedirect oldVersion="0.0.0.0-13.0.0.0" newVersion="13.0.0.0" />
      </dependentAssembly>
    </assemblyBinding>
  </runtime>
  <startup>
    <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.5.2" />
  </startup>
</configuration>
```

### Feature Flags (config_fla.cfg del Servidor)

```ini
# Habilitar PrinterServices (Fase 7)
USAR_PRINTER_SERVICE=false
PRINTER_SERVICE_IP=10.0.0.21
PRINTER_SERVICE_HTTP_PORT=8090
PRINTER_SERVICE_GRPC_PORT=50051
```

---

## 17. NetworkWatcher — Detección de Red por MAC (Proceso Paralelo)

### Principio de diseño

> **NetworkWatcher corre en un thread completamente separado del proceso principal de impresión.**
> Nunca toca `PrintJobManager`, nunca encola, nunca bloquea la impresión.
> Solo **observa**, **detecta** y **notifica** mediante tipos fuertemente tipados.

### Proceso paralelo — Ciclo de vida

```
PrinterServicesHost.Start()
│
├─ Task.Factory.StartNew(PrintWorker.Run, LongRunning)       ← Proceso principal (cola)
├─ Task.Factory.StartNew(StatusMonitor.Run, LongRunning)     ← Proceso de status DLE EOT
├─ Task.Factory.StartNew(NetworkWatcher.Run, LongRunning)    ← ★ PROCESO PARALELO DE RED
│                                                               Completamente independiente
└─ HttpApiServer.Start()                                     ← HTTP listener

Los 3 Tasks corren en threads dedicados del ThreadPool (LongRunning).
No comparten estado mutable entre sí.
Se comunican solo mediante:
  - ConcurrentDictionary (PrinterStatusCache) — lectura thread-safe
  - Eventos C# (NetworkAlert) — para NotificationManager
  - SQLite (cada uno con su propia conexión)
```

### Tipos fuertemente tipados (Core/Network/NetworkTypes.cs)

```csharp
// ═══════════════════════════════════════════════════
// Enums — sin strings mágicos en ningún lugar
// ═══════════════════════════════════════════════════

public enum NetworkStatus
{
    Healthy,              // Gateway MAC coincide, impresoras resueltas
    GatewayChanged,       // MAC del gateway es diferente a la esperada
    GatewayUnreachable,   // No se puede obtener MAC del gateway
    Degraded,             // Gateway OK pero alguna impresora no resuelta
    NotInitialized        // Primera ejecución, aún no se ha aprendido
}

public enum GatewayCheckResult
{
    Match,                // MAC actual == MAC almacenada → red correcta
    Mismatch,             // MAC actual != MAC almacenada → red equivocada
    Unreachable,          // No se pudo obtener MAC (ARP timeout)
    FirstRun              // No hay MAC almacenada → auto-aprender
}

public enum PrinterResolutionResult
{
    Resolved,             // MAC encontrada con la misma IP → todo OK
    IpChanged,            // MAC encontrada con IP diferente → auto-actualizar
    NotFound,             // MAC no aparece en tabla ARP → offline o en otra VLAN
    NewPrinterFound       // MAC desconocida respondiendo en :9100
}

public enum NetworkAlertType
{
    GatewayChanged,       // Red equivocada
    GatewayRestored,      // Volvió a la red correcta
    GatewayUnreachable,   // No se puede resolver gateway
    PrinterIpChanged,     // Impresora cambió de IP (auto-resuelto)
    PrinterNotFound,      // Impresora no visible en ARP
    PrinterRestored,      // Impresora volvió a ser visible
    UnknownPrinterFound   // Nueva impresora detectada en la red
}

// ═══════════════════════════════════════════════════
// Value Objects — inmutables
// ═══════════════════════════════════════════════════

/// <summary>
/// Representa una dirección MAC de 6 bytes. Inmutable, comparable por valor.
/// </summary>
public struct MacAddress : IEquatable<MacAddress>
{
    private readonly byte[] _bytes;  // Siempre 6 bytes

    public MacAddress(byte[] bytes)
    {
        if (bytes == null || bytes.Length != 6)
            throw new ArgumentException("MAC address must be 6 bytes");
        _bytes = (byte[])bytes.Clone();
    }

    public static MacAddress Parse(string mac)
    {
        // Acepta: "AA:BB:CC:DD:EE:FF" o "AA-BB-CC-DD-EE-FF"
        var parts = mac.Split(':', '-');
        if (parts.Length != 6)
            throw new FormatException("Invalid MAC format: " + mac);
        return new MacAddress(parts.Select(p => Convert.ToByte(p, 16)).ToArray());
    }

    public static readonly MacAddress Empty = new MacAddress(new byte[6]);

    public bool IsEmpty => _bytes == null || _bytes.All(b => b == 0);

    public bool Equals(MacAddress other)
        => _bytes != null && other._bytes != null && _bytes.SequenceEqual(other._bytes);

    public override bool Equals(object obj) => obj is MacAddress m && Equals(m);
    public override int GetHashCode() => _bytes == null ? 0 :
        _bytes[0] ^ (_bytes[1] << 8) ^ (_bytes[2] << 16) ^ (_bytes[3] << 24);

    public static bool operator ==(MacAddress a, MacAddress b) => a.Equals(b);
    public static bool operator !=(MacAddress a, MacAddress b) => !a.Equals(b);

    public override string ToString()
        => _bytes == null ? "00:00:00:00:00:00"
        : string.Join(":", _bytes.Select(b => b.ToString("X2")));
}

/// <summary>
/// Información del gateway actual. Inmutable.
/// </summary>
public sealed class GatewayInfo
{
    public IPAddress Ip { get; }
    public MacAddress Mac { get; }
    public string NetworkName { get; }
    public DateTime CheckedAt { get; }

    public GatewayInfo(IPAddress ip, MacAddress mac, string networkName)
    {
        Ip = ip;
        Mac = mac;
        NetworkName = networkName ?? string.Empty;
        CheckedAt = DateTime.Now;
    }
}

/// <summary>
/// Entrada ARP de una impresora en la subred. Inmutable.
/// </summary>
public sealed class PrinterArpEntry
{
    public string ImpresoraId { get; }
    public string Nombre { get; }
    public IPAddress CurrentIp { get; }
    public IPAddress PreviousIp { get; }
    public MacAddress Mac { get; }
    public PrinterResolutionResult Resolution { get; }
    public DateTime ResolvedAt { get; }

    public PrinterArpEntry(string impresoraId, string nombre,
        IPAddress currentIp, IPAddress previousIp,
        MacAddress mac, PrinterResolutionResult resolution)
    {
        ImpresoraId = impresoraId;
        Nombre = nombre;
        CurrentIp = currentIp;
        PreviousIp = previousIp;
        Mac = mac;
        Resolution = resolution;
        ResolvedAt = DateTime.Now;
    }
}

/// <summary>
/// Alerta de red generada por NetworkWatcher. Inmutable.
/// Es el único tipo que sale del NetworkWatcher hacia otros componentes.
/// </summary>
public sealed class NetworkAlert
{
    public NetworkAlertType Type { get; }
    public NetworkStatus CurrentStatus { get; }
    public string Message { get; }
    public GatewayInfo Gateway { get; }
    public PrinterArpEntry Printer { get; }    // null si la alerta es de gateway
    public DateTime CreatedAt { get; }

    public NetworkAlert(NetworkAlertType type, NetworkStatus status,
        string message, GatewayInfo gateway,
        PrinterArpEntry printer = null)
    {
        Type = type;
        CurrentStatus = status;
        Message = message;
        Gateway = gateway;
        Printer = printer;
        CreatedAt = DateTime.Now;
    }
}

/// <summary>
/// Snapshot completo del estado de red. Expuesto por GET /api/network/status.
/// </summary>
public sealed class NetworkStatusSnapshot
{
    public NetworkStatus Status { get; }
    public GatewayCheckResult GatewayResult { get; }
    public GatewayInfo CurrentGateway { get; }
    public GatewayInfo ExpectedGateway { get; }
    public IReadOnlyList<PrinterArpEntry> PrinterResolutions { get; }
    public DateTime LastCheck { get; }

    public NetworkStatusSnapshot(NetworkStatus status,
        GatewayCheckResult gatewayResult,
        GatewayInfo currentGateway, GatewayInfo expectedGateway,
        IReadOnlyList<PrinterArpEntry> printerResolutions)
    {
        Status = status;
        GatewayResult = gatewayResult;
        CurrentGateway = currentGateway;
        ExpectedGateway = expectedGateway;
        PrinterResolutions = printerResolutions;
        LastCheck = DateTime.Now;
    }
}
```

### ARP Helper (Core/Network/ArpHelper.cs)

```csharp
/// <summary>
/// P/Invoke a iphlpapi.dll para obtener MACs via ARP.
/// .NET 4.5.2 compatible. Sin dependencias externas.
/// </summary>
public static class ArpHelper
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(
        int destIp, int srcIp, byte[] macAddr, ref int macAddrLen);

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpNetTable(
        IntPtr pIpNetTable, ref int pdwSize, bool bOrder);

    /// <summary>
    /// Obtiene la MAC de una IP específica via ARP.
    /// Retorna MacAddress.Empty si no se puede resolver.
    /// </summary>
    public static MacAddress GetMacAddress(IPAddress ip) { ... }

    /// <summary>
    /// Lee la tabla ARP completa del sistema operativo.
    /// Retorna pares IP↔MAC de todos los dispositivos conocidos en la subred.
    /// </summary>
    public static IReadOnlyList<ArpTableEntry> GetArpTable() { ... }

    /// <summary>
    /// Obtiene la IP y MAC del default gateway activo.
    /// Usa NetworkInterface.GetAllNetworkInterfaces() + SendARP.
    /// </summary>
    public static GatewayInfo GetCurrentGateway() { ... }
}

public struct ArpTableEntry
{
    public IPAddress Ip { get; }
    public MacAddress Mac { get; }
    public ArpEntryType Type { get; }
}

public enum ArpEntryType
{
    Dynamic,
    Static,
    Invalid,
    Other
}
```

### NetworkWatcher (Core/Network/NetworkWatcher.cs) — Loop paralelo

```
NetworkWatcher.Run(CancellationToken ct):
│
│  // ══ INICIALIZACIÓN ══
│  ¿Existe registro en network_config?
│  ├─ NO (primera ejecución):
│  │   └─ GatewayInfo actual = ArpHelper.GetCurrentGateway()
│  │   └─ Guardar en network_config (auto_learned = 1)
│  │   └─ Log: "Auto-aprendido gateway MAC: AA:BB:CC:DD:EE:01"
│  │
│  └─ SÍ:
│      └─ Cargar MacAddress esperada de network_config
│
│  // ══ LOOP PRINCIPAL (cada 30s) ══
│  while (!ct.IsCancellationRequested)
│  │
│  ├─ 1. VERIFICAR GATEWAY
│  │   └─ GatewayInfo actual = ArpHelper.GetCurrentGateway()
│  │   └─ Comparar actual.Mac con esperada.Mac
│  │   ├─ Match → GatewayCheckResult.Match
│  │   ├─ Mismatch → GatewayCheckResult.Mismatch
│  │   │   └─ Emitir NetworkAlert(GatewayChanged, Degraded, ...)
│  │   │   └─ Log.Error: "RED EQUIVOCADA. Gateway MAC esperado: X, actual: Y"
│  │   └─ Unreachable → GatewayCheckResult.Unreachable
│  │       └─ Emitir NetworkAlert(GatewayUnreachable, ...)
│  │
│  ├─ 2. ARP SCAN DE IMPRESORAS (cada 60s, intercalado)
│  │   └─ arpTable = ArpHelper.GetArpTable()
│  │   └─ Para cada impresora registrada (con mac_address):
│  │   │   ├─ Buscar MAC en arpTable
│  │   │   ├─ Encontrada con misma IP → Resolved (OK)
│  │   │   ├─ Encontrada con IP diferente → IpChanged
│  │   │   │   └─ UPDATE printers SET ip = newIp, ip_resuelta_por_arp = 1
│  │   │   │   └─ Emitir NetworkAlert(PrinterIpChanged, ...)
│  │   │   │   └─ Log.Warn: "Impresora Cocina cambió IP: 10.0.0.50 → 10.0.0.55"
│  │   │   └─ No encontrada → NotFound
│  │   │       └─ Emitir NetworkAlert(PrinterNotFound, ...)
│  │   │
│  │   └─ Detectar MACs desconocidas respondiendo en :9100 (TCP probe)
│  │       └─ Si hay nueva → Emitir NetworkAlert(UnknownPrinterFound, ...)
│  │
│  ├─ 3. ACTUALIZAR SNAPSHOT
│  │   └─ _currentSnapshot = new NetworkStatusSnapshot(...)
│  │   └─ (GET /api/network/status lee este snapshot)
│  │
│  └─ await Task.Delay(intervalMs, ct)
```

### Comunicación con otros componentes (sin acoplamiento)

```
NetworkWatcher                    NotificationManager
     │                                    │
     │  evento C#: OnNetworkAlert         │
     ├───────────────────────────────────►│
     │  (NetworkAlert, fuertemente tipado)│
     │                                    │
     │                                    ├─► gRPC → Servidor
     │                                    ├─► gRPC → Cliente (si aplica)
     │                                    └─► SQLite (notifications table)
     │
     │  lectura directa (thread-safe)
     │
     │  NetworkStatusSnapshot
     ├──────────────────────────────────► GET /api/network/status
     │  (readonly, inmutable)             (NetworkController.cs)
```

**No hay dependencia inversa**: PrintJobManager, PrintWorker y StatusMonitor **ignoran** la existencia de NetworkWatcher. Son procesos completamente aislados.

### Evento C# (tipado fuerte)

```csharp
public class NetworkWatcher
{
    // ═══ Evento fuertemente tipado ═══
    public event Action<NetworkAlert> OnNetworkAlert;

    // ═══ Snapshot para la API (lectura thread-safe) ═══
    private volatile NetworkStatusSnapshot _currentSnapshot;
    public NetworkStatusSnapshot CurrentSnapshot => _currentSnapshot;

    // ═══ Thread dedicado ═══
    private readonly CancellationTokenSource _cts;

    public void Start()
    {
        Task.Factory.StartNew(
            () => RunLoop(_cts.Token),
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private async Task RunLoop(CancellationToken ct)
    {
        // ... loop descrito arriba
    }

    public void Stop()
    {
        _cts.Cancel();
    }
}
```

### Respuesta de GET /api/network/status

```json
{
  "status": "Healthy",
  "gatewayResult": "Match",
  "currentGateway": {
    "ip": "10.0.0.1",
    "mac": "AA:BB:CC:DD:EE:01",
    "networkName": "RESTAURANT_WIFI",
    "checkedAt": "2026-02-10T12:30:00"
  },
  "expectedGateway": {
    "ip": "10.0.0.1",
    "mac": "AA:BB:CC:DD:EE:01",
    "networkName": "RESTAURANT_WIFI"
  },
  "printerResolutions": [
    {
      "impresoraId": "cocina-01",
      "nombre": "Cocina",
      "currentIp": "10.0.0.50",
      "previousIp": "10.0.0.50",
      "mac": "00:11:22:33:44:50",
      "resolution": "Resolved"
    },
    {
      "impresoraId": "bar-01",
      "nombre": "Bar",
      "currentIp": "10.0.0.55",
      "previousIp": "10.0.0.51",
      "mac": "00:11:22:33:44:51",
      "resolution": "IpChanged"
    }
  ],
  "lastCheck": "2026-02-10T12:30:00"
}
```
