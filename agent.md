# agent.md — PrinterServices

> Guía para que cualquier IA (o desarrollador) entienda la estructura,
> propósito y articulación de este proyecto.
> Léelo ANTES de modificar cualquier archivo.

---

## ¿Qué es este proyecto?

**PrinterServices** es un **Windows Service centralizado** que gestiona la impresión
ESC/POS para todos los terminales POS (`Quipunet.exe`) en una red local de restaurante.

- **Proyecto nuevo e independiente** — tiene su propio `.sln` y repositorio
- **Referencia QuipuNetX.dll** — reutiliza objetos `Impresion`, `Impresora`, `Respuesta`
- **Framework**: .NET Framework 4.5.2 (no .NET Core)
- **Solo el Servidor envía impresiones** — los Clientes envían pedidos al Servidor,
  y el Servidor delega a PrinterServices

## Articulación con otros proyectos

```
sourcecode/
├── front/                  # Frontend WPF (Quipunet.exe) — POS
│   └── QuipuNet/           # Cliente y Servidor son el mismo .exe con roles distintos
│
├── quipu/                  # Backend (QuipuNetX.dll) — Lógica de negocio
│   └── QuipuNetX/          # Genera QuipuNetX.dll que ESTE proyecto referencia
│       ├── entity/extras/Impresion.cs     ← Objeto de impresión (reutilizado)
│       ├── entity/Impresora.cs            ← Config de impresora (reutilizado)
│       ├── Util/Respuesta.cs              ← Resultado de operaciones (reutilizado)
│       ├── Util/Definitions.cs            ← Constantes (reutilizado)
│       ├── Util/print/PrintUtil.cs        ← Será modificado para delegar aquí
│       └── sugar/Com/Orm/SugarDb.cs       ← Patrón a seguir para SQLite
│
└── printerservices/        ★ ESTE PROYECTO
    ├── agent.md            ← Este archivo
    ├── vibe_engeneering_printerservices.md  ← Documento de diseño completo
    └── PrinterServices.sln
```

## Flujo simplificado

```
Cliente POS → enviaPedido → Servidor POS → HTTP REST → PrinterServices.exe
PrinterServices.exe → imprime → notifica vía gRPC → Servidor + Cliente
```

## Restricciones importantes

1. **Solo .NET Framework 4.5.2** — no usar APIs de .NET Core/.NET 5+
2. **Solo el Servidor (Quipunet.exe con rol servidor) envía impresiones** al servicio
3. **QuipuNetX.dll** se referencia como DLL externa, NO copiar código
4. **No crear archivos innecesarios** — seguir la estructura definida
5. **Verificar antes de crear** — usar grep/find para confirmar que algo no existe
6. **Newtonsoft.Json** para serialización (no System.Text.Json)
7. **log4net** para logging
8. **TopShelf** para Windows Service

### SQLite autónomo — NO hay instancia de QuipuNetX.dll

> **PrinterServices NO ejecuta QuipuNetX.dll.** No hay `local_id`, no hay
> contexto de seguridad, no hay `SugarDb.getInstance()`, no hay ningún
> singleton ni servicio de QuipuNetX corriendo en este proceso.

- **Solo se reutilizan clases como DTOs** (deserializadas desde JSON):
  `Impresion`, `Impresora`, `Respuesta`, `Linea`, `Definitions`
- **Namespace `PSQLite`** — ORM sqlite-net propio en `Data/Orm/PSQLite.cs`
  - Copia limpia de sqlite-net (MIT License) sin NINGUNA referencia a QuipuNetX
  - Namespace diferente (`PSQLite`) para evitar conflictos con `SQLite` de QuipuNetX.dll
  - P/Invoke directo a `sqlite3.dll` — NO pasa por QuipuNetX
  - Sin `Util.Capture`, sin `Logquipu`, sin `FeatureFlagConfigReader`, sin `Security`
  - Todas las entities usan `using PSQLite;` (NO `using SQLite;`)
- **PrinterServiceDb hereda `PSQLite.SQLiteConnection`** — 100% autónomo
- Esta es la **única duplicación de código aceptable** en el proyecto
- **NUNCA** usar `using SQLite;` — siempre `using PSQLite;`
- **NUNCA** intentar usar `SugarDb.getInstance()` ni asumir contexto QuipuNetX

---

## Estructura de carpetas (ACTUAL — archivos implementados)

```
printerservices/
├── agent.md                           # ★ ESTE ARCHIVO — guía para IA/devs
├── vibe_engeneering_printerservices.md # Diseño completo del proyecto
├── PrinterServices.sln                # Solución con UN solo proyecto
│
└── PrinterServices/                   # ── UN SOLO PROYECTO → PrinterServices.exe ──
    │                                  # .NET 4.5.2, OutputType: Exe
    │                                  # Referencia QuipuNetX.dll
    │
    ├── PrinterServices.csproj         # Proyecto único. Incluye TODOS los archivos.
    ├── packages.config                # TopShelf, log4net, Newtonsoft.Json
    ├── App.config                     # Solo binding redirects (Newtonsoft.Json v12→v13)
    ├── log4net.config                 # Logging a archivo + consola
    ├── Program.cs                     # Bootstrap TopShelf + log4net
    ├── PrinterServicesHost.cs         # Start() / Stop() — orquesta todo.
    │                                  # Inicializa: DB → ConfigManager → Queue → Worker
    │                                  #             → HTTP API → StatusMonitor
    │
    ├── Config/                        # ── CONFIGURACIÓN CENTRALIZADA ──
    │   └── ConfigManager.cs           # ★ Singleton, SQLite-backed, cache ConcurrentDict
    │                                  # Reemplaza ConfigurationManager.AppSettings
    │                                  # Todos los timeouts/puertos vienen de aquí
    │                                  # API: GetInt(), GetBool(), Set(), SetInt()
    │
    ├── Api/                           # ── HTTP API (self-hosted) ──
    │   │                              # System.Net.HttpListener, puerto desde ConfigManager
    │   ├── HttpApiServer.cs           # Escucha HTTP, despacha a ApiRouter
    │   ├── ApiRouter.cs               # Mapeo rutas → handlers + ApiResult helper
    │   └── Controllers/
    │       ├── HealthController.cs    # GET /api/health
    │       ├── PrintController.cs     # POST /api/print/comanda, /comandas, /venta, /precuenta
    │       ├── PrinterController.cs   # GET /api/printer/status, POST /api/printer/register
    │       ├── JobController.cs       # GET /api/job/{id}, /jobs/pending, POST retry
    │       └── ConfigController.cs    # GET/PUT /api/config — CRUD settings dinámico
    │                                  # (NotificationController.cs, NetworkController.cs → futuro)
    │
    ├── Queue/                         # ── COLA DE IMPRESIÓN (GestorDeColas) ──
    │   ├── PrintJobManager.cs         # ConcurrentQueue + SemaphoreSlim + SQLite backup
    │   │                              # Enqueue, DequeueAsync, MarkDone, MarkFailed,
    │   │                              # MarkWaiting, ReEnqueue, RecoverPending
    │   └── PrintJob.cs               # Estado: PENDING→PRINTING→DONE|FAILED|WAITING
    │                                  # Props: LineasImprimirJson, TamanioLetra, AbreGaveta,
    │                                  #        TipoGeneracion, QrData, CodigoCorte
    │
    ├── Workers/                       # ── THREAD DE IMPRESIÓN ──
    │   └── PrintWorker.cs            # 1. Pre-check DLE EOT (offline→WAITING, sin papel→WAITING)
    │                                  # 2. BuildPayload: modo LINEAS o modo CADENA
    │                                  # 3. SendWithRetry: exponential backoff desde ConfigManager
    │                                  # 4. LogPrint en print_log
    │
    ├── Monitoring/                    # ── MONITOREO DE IMPRESORAS ──
    │   ├── PrinterStatusChecker.cs   # DLE EOT 1-4 via raw TCP Socket
    │   │                              # CheckAsync(ip, port, timeout) → PrinterStatus
    │   │                              # CheckSync(ip, port, timeout) → PrinterStatus
    │   │                              # Retorna: Online, TienePapel, TapaAbierta, ErrorRecuperable
    │   └── StatusMonitor.cs          # Background loop (cada StatusCheckIntervalSeconds)
    │                                  # Chequea todas las printers registradas
    │                                  # Actualiza printers table (estado_online, tiene_papel, etc.)
    │                                  # Cuando printer pasa OFFLINE→ONLINE: re-encola jobs WAITING
    │
    ├── Drivers/                       # ── DRIVERS ESC/POS POR MODELO ──
    │   ├── IPrinterDriver.cs         # Interfaz: corte, status, texto, init, feed, cashDrawer
    │   ├── DriverFactory.cs          # Selecciona driver por Impresion.Printermodel
    │   ├── EpsonDriver.cs            # Corte: GS V 1    | Status: DLE EOT
    │   ├── StarDriver.cs             # Corte: ESC d 2   | Status: ASB
    │   ├── BixolonDriver.cs          # Corte: GS V 66 0 | Status: DLE EOT
    │   ├── GenericEscPosDriver.cs    # Corte: GS V 66 0 | Status: DLE EOT
    │   ├── EscPosCommandBuilder.cs   # ★ Fluent builder de bytes ESC/POS
    │   │                              # Init, Text, Bold, Alignment, FontSize, LetterSize,
    │   │                              # QR code, Barcode, CashDrawer, Cut, LineSpacing
    │   └── LineaParser.cs            # Parsea lineasimprimir JSON (formato Linea de QuipuNetX)
    │                                  # LineaData: texto, estilo(BOLD/CENTER/RIGHT), tamanio, barcode
    │                                  # BuildFromLineas(driver, lineas) → byte[] ESC/POS completo
    │
    ├── Transport/                     # ── CAPA DE TRANSPORTE FÍSICO ──
    │   ├── ITransport.cs             # Interfaz: ConnectAsync, SendAsync, ReceiveAsync, Dispose
    │   └── TcpTransport.cs           # TCP:9100, timeouts desde ConfigManager
    │                                  # (SerialTransport.cs, UsbTransport.cs → futuro)
    │
    └── Data/                          # ── PERSISTENCIA SQLITE ──
        │                              # BD: %AppData%\QuipuNet\printerservice.db
        ├── Orm/
        │   └── PSQLite.cs            # ★ ORM sqlite-net LIMPIO (namespace PSQLite)
        │                              # CERO dependencias de QuipuNetX.dll
        │                              # P/Invoke directo a sqlite3.dll
        │                              # Incluye: SQLiteConnection, TableMapping,
        │                              #   SQLiteCommand, TableQuery<T>, atributos,
        │                              #   SQLite3 P/Invoke — todo autónomo
        ├── PrinterServiceDb.cs       # Singleton, hereda PSQLite.SQLiteConnection
        │                              # CreateTables() al inicializar (6 tablas + índices)
        └── Models/                    # Todos usan: using PSQLite;
            ├── PrintJobEntity.cs      # print_jobs — incluye lineas_imprimir_json, tamanio_letra, etc.
            ├── PrintLogEntity.cs      # print_log
            ├── PrinterEntity.cs       # printers — incluye mac_address, estado_online, tiene_papel
            ├── ConfigSettingEntity.cs  # ★ config_settings — key/value/default/category/tipo/rango
            ├── NotificationEntity.cs  # notifications
            └── NetworkConfigEntity.cs # network_config
```

---

## Componentes implementados y su uso

### ConfigManager — Configuración centralizada

Reemplaza `ConfigurationManager.AppSettings`. Toda configuración dinámica vive en SQLite.

```csharp
// Inicialización (en PrinterServicesHost.Start())
var db = PrinterServiceDb.GetInstance();
var config = ConfigManager.GetInstance(db);  // singleton, seed defaults, carga cache

// Lectura dinámica (en cualquier componente, sin inyección)
var cfg = ConfigManager.Instance;
int timeout = cfg.GetInt("TcpConnectTimeoutMs", 3000);   // default si no existe
bool flag = cfg.GetBool("AutoLearnGatewayOnFirstRun", true);

// Escritura (actualiza cache + BD atómicamente)
cfg.SetInt("MaxRetries", 5);
cfg.Set("TcpConnectTimeoutMs", "5000");
```

### PrintWorker — Flujo de procesamiento de un job

```
DequeueAsync() → PrinterStatusChecker.CheckAsync(ip, port, timeout)
  ├─ OFFLINE → MarkWaiting("Impresora offline") → sale, NO retry
  ├─ SIN PAPEL → MarkWaiting("Sin papel") → sale, NO retry
  └─ ONLINE + PAPEL → continúa:
      DriverFactory.GetDriver(model) → BuildPayload(driver, job)
        ├─ job.LineasImprimirJson != null → LineaParser.BuildFromLineas(driver, lineas)
        └─ solo cadena → EscPosCommandBuilder: init + letterSize + text + cashDrawer + cut
      SendWithRetry(payload) → exponential backoff (500ms, 1s, 2s)
        ├─ Éxito → MarkDone()
        └─ Falla 3x → MarkFailed(error)
```

### EscPosCommandBuilder — Ejemplo de uso

```csharp
var driver = DriverFactory.GetDriver("EPSON_TM_T20II");
var builder = new EscPosCommandBuilder(driver);

byte[] payload = builder
    .Init()
    .SetAlignment(Alignment.Center)
    .SetBold(true)
    .Text("*** COCINA ***\n")
    .SetBold(false)
    .SetAlignment(Alignment.Left)
    .SetLetterSize("2")
    .Text("Mesa 5\n1x Lomo Saltado\n")
    .OpenCashDrawer()
    .Cut(CutType.Partial)
    .Build();
```

### StatusMonitor — Flujo de monitoreo

```
Cada N segundos (StatusCheckIntervalSeconds, default 15):
  → Query printers WHERE ip IS NOT NULL
  → Para cada impresora:
      PrinterStatusChecker.CheckAsync(ip, port, timeout)
        DLE EOT 1 → online/offline
        DLE EOT 2 → tapa abierta
        DLE EOT 3 → errores
        DLE EOT 4 → papel
      → UPDATE printers SET estado_online, tiene_papel, tapa_abierta, ultimo_check
      → Si cambio OFFLINE→ONLINE:
          → Query print_jobs WHERE impresora_id=X AND estado='WAITING'
          → ReEnqueue() cada uno → vuelven a la cola del worker
```

### API REST — Ejemplos completos con curl

```bash
# ── Health ──
curl http://localhost:8090/api/health

# ── Registrar impresora ──
curl -X POST http://localhost:8090/api/printer/register \
  -d '{"impresora_id":"cocina-01","nombre":"Cocina","ip":"10.0.0.50","printermodel":"EPSON_TM_T20II"}'

# ── Enviar comanda simple (modo CADENA) ──
curl -X POST http://localhost:8090/api/print/comanda \
  -d '{"impresora_id":"cocina-01","impresora_ip":"10.0.0.50","printermodel":"EPSON_TM_T20II","cadena":"*** COCINA ***\nMesa 5\n1x Lomo Saltado\n","impresora_tamanioletra":"2","abregaveta":"1"}'

# ── Enviar comanda estructurada (modo LINEAS) ──
curl -X POST http://localhost:8090/api/print/comanda \
  -d '{"impresora_id":"cocina-01","impresora_ip":"10.0.0.50","printermodel":"EPSON_TM_T20II","lineasimprimir":[{"texto":"COCINA","estilo":"BOLD,CENTER","tamanio":2},{"texto":"Mesa 5","estilo":"","tamanio":1},{"texto":"1x Lomo Saltado","estilo":"BOLD","tamanio":1}]}'

# ── Consultar estado de jobs ──
curl http://localhost:8090/api/jobs/pending
curl http://localhost:8090/api/job/abc123def456

# ── Reintentar job fallido ──
curl -X POST http://localhost:8090/api/job/abc123def456/retry

# ── Estado de impresoras ──
curl http://localhost:8090/api/printer/status
curl http://localhost:8090/api/printer/status/cocina-01

# ── Configuración dinámica ──
curl http://localhost:8090/api/config                    # ver todos
curl http://localhost:8090/api/config/category/network   # por categoría
curl http://localhost:8090/api/config/MaxRetries          # uno específico
curl -X PUT http://localhost:8090/api/config \
  -d '{"key":"TcpConnectTimeoutMs","value":"5000"}'      # actualizar
curl -X PUT http://localhost:8090/api/config/batch \
  -d '{"settings":{"MaxRetries":"5","RetryBackoffBaseMs":"1000"}}'  # batch
curl -X POST http://localhost:8090/api/config/MaxRetries/reset     # reset uno
curl -X POST http://localhost:8090/api/config/reset-all            # reset todos
```

---

## Reglas para modificar este proyecto

### DO (Hacer)

- Seguir la arquitectura de 3 capas: **PrintJobManager → IPrinterDriver → ITransport**
- Usar `ConfigManager.Instance` para toda configuración dinámica (nunca `ConfigurationManager.AppSettings`)
- Usar `async/await` donde sea posible
- Usar `Impresion`, `Impresora`, `Respuesta` de QuipuNetX.dll (NO redefinirlos)
- Seguir el patrón de SugarDb para SQLite
- Logging con `log4net`: `private static readonly ILog Log = LogManager.GetLogger(typeof(MiClase));`
- Capturar excepciones, loguear, y devolver `ApiResult` o `Respuesta` con tipo ERROR
- Serializar con `Newtonsoft.Json` (JsonConvert)
- **Tipado fuerte siempre**: usar enums, no strings mágicos
- **NetworkWatcher es proceso paralelo** (futuro): nunca encolar, nunca tocar PrintJobManager
- **Agregar nuevos .cs al .csproj** en `<Compile>` ItemGroup

### DON'T (No hacer)

- NO usar APIs de .NET Core / .NET 5+ (no disponibles en 4.5.2)
- NO usar `ConfigurationManager.AppSettings` — usar `ConfigManager.Instance`
- NO copiar clases de QuipuNetX al proyecto — referenciar el DLL
- NO hacer que los Clientes envíen directamente a PrinterServices — solo el Servidor
- NO usar System.Text.Json — usar Newtonsoft.Json
- NO usar Channel<T> — usar ConcurrentQueue + SemaphoreSlim
- NO crear archivos fuera de la estructura definida
- NO modificar QuipuNetX.dll desde este proyecto
- NO mezclar lógica de red (NetworkWatcher) con lógica de impresión (PrintWorker)
- NO hardcodear timeouts o puertos — siempre leer de ConfigManager

### Mapeo de modelos de impresora (IPrinterDriver)

El campo `Impresion.Printermodel` determina qué driver usar.
Este mapeo debe ser consistente con `PrintUtil.getPrinterObjectByModel()` en QuipuNetX:

```
Printermodel value    → IPrinterDriver
────────────────────────────────────────
GENERICA              → GenericEscPosDriver
BIXOLON_SRP270        → BixolonDriver
STAR_SP               → StarDriver
EPSON_TM_U220         → EpsonDriver
EPSON_TM_T20II        → EpsonDriver
START_BSC10           → StarDriver
CBX                   → GenericEscPosDriver
ZKT_ECO               → GenericEscPosDriver
default               → GenericEscPosDriver
```

### Fases del proyecto

| Fase | Estado |
|------|--------|
| **0** TopShelf + SQLite + health check | ✅ DONE |
| **1** HTTP API + PrintJobManager + PrintWorker | ✅ DONE |
| **2** EscPosCommandBuilder + LineaParser + formatting | ✅ DONE |
| **3** StatusMonitor (DLE EOT) + WAITING state | ✅ DONE |
| **C** ConfigManager centralizado (SQLite + API REST) | ✅ DONE |
| **4** Cola persistente SQLite completa | ✅ DONE |
| **5** gRPC NotificationManager | PENDIENTE |
| **6** UDP Discovery | PENDIENTE |
| **7** Feature flag en PrintUtil del Servidor | PENDIENTE |
| **8** NetworkWatcher + alertas UI + MonitoreoRemoto | PENDIENTE |

### Documentos de referencia

- `vibe_engeneering_printerservices.md` — Diseño completo, API, esquema BD, flujos
- `quipu/QuipuNetX/Util/print/PrintUtil.cs` — Código actual de impresión (a reemplazar)
- `quipu/QuipuNetX/sugar/Com/Orm/SugarDb.cs` — Patrón para SQLite
- `quipu/QuipuNetX/SQLite/FeatureFlagConfigReader.cs` — Feature flags y PRAGMAs
- `quipu/QuipuNetX/entity/extras/Impresion.cs` — Objeto Impresion (~2600 líneas)
- `quipu/QuipuNetX/entity/extras/Linea.cs` — Estructura de línea para formato estructurado
