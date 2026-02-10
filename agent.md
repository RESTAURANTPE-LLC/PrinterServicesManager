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

---

## Estructura de carpetas y qué hace cada una

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
    ├── App.config                     # Puertos HTTP/gRPC/UDP, timeouts, config
    ├── log4net.config                 # Logging a archivo + consola
    ├── Program.cs                     # Bootstrap TopShelf + log4net
    ├── PrinterServicesHost.cs         # Start() / Stop() — orquesta todo.
    │                                  # Cada worker en su propio Task(LongRunning).
    │
    ├── Api/                           # ── HTTP API (self-hosted) ──
    │   │                              # System.Net.HttpListener
    │   ├── HttpApiServer.cs           # Escucha HTTP, despacha a controllers
    │   ├── ApiRouter.cs               # Mapeo de rutas → handlers
    │   └── Controllers/
    │       ├── HealthController.cs    # GET /api/health
    │       ├── PrintController.cs     # POST /api/print/comanda, /comandas, /venta
    │       ├── PrinterController.cs   # GET /api/printer/status, POST /api/printer/register
    │       ├── JobController.cs       # GET /api/job/{id}, /jobs/pending
    │       ├── NotificationController.cs  # GET /api/notifications/{deviceId}
    │       └── NetworkController.cs   # GET /api/network/status
    │
    ├── Queue/                         # ── COLA DE IMPRESIÓN (GestorDeColas) ──
    │   ├── PrintJobManager.cs         # ConcurrentQueue + SemaphoreSlim + SQLite backup
    │   └── PrintJob.cs               # Wrappea Impresion + jobId, estado, reintentos
    │
    ├── Workers/                       # ── THREADS DE BACKGROUND ──
    │   ├── PrintWorker.cs            # Consume cola, imprime, retry, notifica
    │   └── StatusMonitor.cs          # Timer 15s: DLE EOT a cada impresora
    │
    ├── Drivers/                       # ── DRIVERS ESC/POS POR MODELO ──
    │   ├── IPrinterDriver.cs         # Interfaz: corte, status, texto, QR, etc.
    │   ├── DriverFactory.cs          # Selecciona driver por Impresion.Printermodel
    │   ├── EpsonDriver.cs            # Corte: 0x1D 0x56 0x01 | Status: DLE EOT
    │   ├── StarDriver.cs             # Corte: 0x1B 0x64 0x02 | Status: ASB
    │   ├── BixolonDriver.cs          # Corte: 0x1D 0x56 0x42 | Status: DLE EOT
    │   └── GenericEscPosDriver.cs    # Corte: 0x1D 0x56 0x42 0x00 | Status: DLE EOT
    │
    ├── Transport/                     # ── CAPA DE TRANSPORTE FÍSICO ──
    │   ├── ITransport.cs             # Interfaz: Connect, Send, Receive, Dispose
    │   ├── TcpTransport.cs           # TCP:9100, retry 3x, timeout, pool
    │   └── SerialTransport.cs        # Puerto COM
    │
    ├── Notifications/
    │   └── NotificationManager.cs    # gRPC push → Servidor + Cliente
    │
    ├── Grpc/
    │   ├── Protos/
    │   │   └── printer_service.proto
    │   └── Services/
    │       └── PrinterGrpcService.cs
    │
    ├── Network/                       # ── DETECCIÓN DE RED (PROCESO PARALELO) ──
    │   │                              # ★ Thread dedicado, NO toca la cola
    │   │                              # ★ Todo fuertemente tipado
    │   ├── NetworkWatcher.cs          # Loop paralelo: gateway MAC + ARP scan
    │   ├── ArpHelper.cs              # P/Invoke: SendARP, GetIpNetTable
    │   ├── NetworkTypes.cs           # Enums + structs inmutables (MacAddress, etc.)
    │   └── NetworkAlertManager.cs    # Publica alertas → NotificationManager
    │
    ├── Discovery/
    │   └── UdpDiscoveryServer.cs     # Broadcast UDP :9999
    │
    ├── Status/
    │   ├── PrinterStatusChecker.cs   # DLE EOT / ASB
    │   └── PrinterStatusCache.cs     # ConcurrentDictionary thread-safe
    │
    └── Data/                          # ── PERSISTENCIA SQLITE ──
        │                              # BD: %AppData%\QuipuNet\printerservice.db
        ├── PrinterServiceDb.cs       # Hereda SQLiteConnection (patrón SugarDb)
        ├── Models/
        │   ├── PrintJobEntity.cs
        │   ├── PrintLogEntity.cs
        │   ├── PrinterEntity.cs      # Incluye mac_address
        │   ├── NotificationEntity.cs
        │   └── NetworkConfigEntity.cs
        └── Repositories/
            ├── PrintJobRepository.cs
            ├── PrintLogRepository.cs
            ├── PrinterRepository.cs
            ├── NotificationRepository.cs
            └── NetworkConfigRepository.cs
```

---

## Reglas para modificar este proyecto

### DO (Hacer)

- Seguir la arquitectura de 3 capas: **PrintJobManager → IPrinterDriver → ITransport**
- Usar `async/await` donde sea posible
- Usar `Impresion`, `Impresora`, `Respuesta` de QuipuNetX.dll (NO redefinirlos)
- Seguir el patrón de SugarDb para SQLite
- Logging con `log4net`: `private static readonly ILog Log = LogManager.GetLogger(typeof(MiClase));`
- Capturar excepciones, loguear, y devolver `Respuesta` con tipo ERROR
- Serializar con `Newtonsoft.Json` (JsonConvert)
- **Tipado fuerte siempre**: usar enums, no strings mágicos. Clases inmutables para value objects
- **NetworkWatcher es proceso paralelo**: nunca encolar, nunca tocar PrintJobManager desde ahí

### DON'T (No hacer)

- NO usar APIs de .NET Core / .NET 5+ (no disponibles en 4.5.2)
- NO copiar clases de QuipuNetX al proyecto — referenciar el DLL
- NO hacer que los Clientes envíen directamente a PrinterServices — solo el Servidor
- NO usar System.Text.Json — usar Newtonsoft.Json
- NO usar Channel<T> — usar ConcurrentQueue + SemaphoreSlim
- NO crear archivos fuera de la estructura definida
- NO modificar QuipuNetX.dll desde este proyecto
- NO mezclar lógica de red (NetworkWatcher) con lógica de impresión (PrintWorker)
- NO usar strings para estados/tipos — usar los enums definidos en NetworkTypes.cs

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

### Documentos de referencia

- `vibe_engeneering_printerservices.md` — Diseño completo, API, esquema BD, flujos
- `quipu/QuipuNetX/Util/print/PrintUtil.cs` — Código actual de impresión (a reemplazar)
- `quipu/QuipuNetX/sugar/Com/Orm/SugarDb.cs` — Patrón para SQLite
- `quipu/QuipuNetX/SQLite/FeatureFlagConfigReader.cs` — Feature flags y PRAGMAs
- `quipu/QuipuNetX/entity/extras/Impresion.cs` — Objeto Impresion (~2600 líneas)
