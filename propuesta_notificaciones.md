# Propuesta Tecnologica: Sistema de Notificaciones POS

## Aplicando la arquitectura de PrinterServices (Job + JobStatusManager + Eventos) al sistema de notificaciones bidireccional POS

---

## 1. Analogia arquitectonica: PrinterServices vs Notificaciones POS

La arquitectura de PrinterServices ya resolvio un problema identico: **comunicacion asincrona con feedback en tiempo real y resiliencia ante caidas**. Esta propuesta replica ese patron probado.

| Concepto PrinterServices | Equivalente Notificaciones POS | Rol |
|---|---|---|
| `PrintJob` | `Notificacion` (entidad) | Unidad de trabajo persistida en SQLite |
| `PrintJobManager` (cola + enqueue + dequeue) | `NotificacionService` (CRUD + query) | Gestiona el ciclo de vida |
| `PrintJobStatusManager` (RAM + eventos) | `NotificacionStatusManager` (RAM + eventos) | **Fuente de verdad para la UI** |
| `PrintWorker` (consume cola, imprime) | `NotificacionRetryService` (timer, reintenta) | Worker background que ejecuta |
| `QuipuNetXCallbackClient` (HTTP POST fire-and-forget) | `NotificarOrigenAsync` (HTTP POST al cliente) | Comunicacion de vuelta |
| `PSProgressService` (WPF binding) | `PSNotificacionProgressService` (WPF binding) | UI en tiempo real |
| `PrintJobStatus` enum (PENDING, DONE, FAILED, WAITING, EXPIRED) | `EstadoNotificacion` enum (Pendiente, Completada, Error, Timeout) | Estados del ciclo de vida |
| HTTP callback de PrinterServices → QuipuNetX | HTTP callback del POS → QuipuNetX | Canal de retorno |
| `pedidoprinterjob` (SQLite, backup) | `notificacion` (SQLite, persistencia + retry) | BD de respaldo |

---

## 2. Arquitectura propuesta: NotificacionStatusManager (RAM-first + BD backup)

Siguiendo el patron exacto de `PrintJobStatusManager`:

```
┌─ QuipuNetX (Nancy 8081) ─────────────────────────────────────────────────────┐
│                                                                               │
│  NotificacionStatusManager (Singleton en RAM)                                 │
│  ┌─────────────────────────────────────────────────────────────────────────┐  │
│  │ ConcurrentDictionary<notifId, NotificacionStatusInfo> ← verdad para UI │  │
│  │                                                                         │  │
│  │ RegisterNotificacion()  ← OrdenpedidoController.agregarAsync()          │  │
│  │ UpdateStatus()          ← endpoint HTTP callback (idempotente)          │  │
│  │ Initialize()            ← AL ARRANCAR: BD → RAM + sync pendientes       │  │
│  │                                                                         │  │
│  │ Eventos C#:                                                             │  │
│  │  ├─ StatusChanged(notifId, newStatus)    → Front actualiza UI           │  │
│  │  ├─ NotificacionCompletada(notifId)      → Front: exito                 │  │
│  │  ├─ NotificacionFallida(notifId, error)  → Front: error + reintento     │  │
│  │  ├─ NotificacionTimeout(notifId)         → Front: timeout alcanzado     │  │
│  │  ├─ OpenProgress                         → Front: abre indicador        │  │
│  │  └─ HideProgress                         → Front: cierra indicadores    │  │
│  │                                                                         │  │
│  │ PersistAsync()  ← Task.Run: BD en background, NO bloquea               │  │
│  └─────────────────────────────────────────────────────────────────────────┘  │
│                                                                               │
│  notificacion (SQLite) ← persistencia + recovery + retry + dashboard         │
│                                                                               │
│  Nancy: POST /api/rest/notificaciones/v1/recibirRespuesta                     │
│  Nancy: GET  /api/rest/notificaciones/v1/consultarEstado                      │
│                                                                               │
└───────────────────────────────────────────────────────────────────────────────┘
         │ Consulta estado POS              ▲ POST recibirRespuesta
         │ (solo en retry)                  │ (POS notifica resultado)
         ▼                                  │
┌─ POS Fisico (Java/QuipuPos) ─────────────────────────────────────────────────┐
│                                                                               │
│  Recibe cobro → procesa → responde a QuipuNetX via HTTP callback              │
│  Expone: GET /api/rest/ordenpedido/consultarTransaccion?id={id}               │
│                                                                               │
└───────────────────────────────────────────────────────────────────────────────┘
```

---

## 3. NotificacionStatusManager: Estructura completa

```csharp
// QuipuNetX/Notificaciones/NotificacionStatusManager.cs

public class NotificacionStatusManager
{
    // ─── Singleton (mismo patron que PrintJobStatusManager) ────
    private static readonly Lazy<NotificacionStatusManager> _instance = ...;
    public static NotificacionStatusManager Instance => _instance.Value;

    // ─── Almacen en RAM (fuente de verdad para la UI) ────────
    private readonly ConcurrentDictionary<string, NotificacionStatusInfo> _notificaciones;

    // ─── Eventos (mismo patron que PrintJobStatusManager) ──────
    public event Action<string, string> StatusChanged;             // (notifId, newStatus)
    public event Action<string> NotificacionCompletada;            // (notifId)
    public event Action<string, string> NotificacionFallida;       // (notifId, errorMsg)
    public event Action<string> NotificacionTimeout;               // (notifId)
    public event Action OpenProgress;                              // Abrir indicador UI
    public event Action HideProgress;                              // Cerrar indicadores

    // ─── Registro: llamado al crear la transaccion POS ──────
    public string RegisterNotificacion(CrearNotificacionRequest request)
    {
        // 1. Generar UUID v4
        // 2. Agregar al diccionario RAM con status=Pendiente
        // 3. Disparar OpenProgress
        // 4. Task.Run → guardar en SQLite (background, no bloquea)
        // 5. Retornar notificacionId
    }

    // ─── Actualizacion: llamado por endpoint HTTP callback ──
    public void UpdateStatus(string notifId, EstadoNotificacion estado, string mensaje = "", string ventaId = null)
    {
        // 1. Actualizar RAM → INMEDIATO
        // 2. Disparar StatusChanged
        // 3. Evaluar: ¿Completada? → NotificacionCompletada
        //           ¿Error?      → NotificacionFallida
        //           ¿Timeout?    → NotificacionTimeout
        // 4. Si estado terminal → HideProgress
        // 5. Task.Run → UPDATE SQLite (background)
        // NOTA: Idempotente — si ya tiene ese status, no hace nada
    }

    // ─── Recovery ante caida ─────────────────────────────────
    public void Initialize()
    {
        // Paso 1: BD → RAM
        //   SELECT * FROM notificacion WHERE ESTADONOTIFICACION = '1' (Pendiente)
        //   Poblar _notificaciones con las pendientes
        // Paso 2 (background): verificar con POS si alguna ya se resolvio
    }

    // ─── Consultas desde Front (todo en RAM, sin BD) ──────────
    public NotificacionStatusInfo GetNotificacion(string notifId) { ... }
    public List<NotificacionStatusInfo> GetPendientes() { ... }
    public List<NotificacionStatusInfo> GetFallidas() { ... }

    // ─── Limpieza ────────────────────────────────────────────
    public void Clear() { ... }
}
```

---

## 4. Flujo temporal completo (lo que siente el usuario)

### 4.1 Happy path (POS responde OK)

```
t=0ms      Usuario click "Pagar con POS"
t=50ms     OrdenpedidoController.agregarAsync()
           → NotificacionStatusManager.RegisterNotificacion() en RAM (Pendiente)
           → Task.Run → INSERT SQLite (background)
           → Dispara OpenProgress

t=100ms    Front recibe OpenProgress
           → Se suscribe a StatusChanged
           → Muestra indicador "Procesando pago en POS..."

t=200ms    QuipuNetX llama al POS via HTTP → LlamarPosApiAsync()

t=3000ms   POS responde OK → genera venta
           → NotificacionStatusManager.UpdateStatus(notifId, Completada)
           → RAM actualizada (~0ms)
           → Evento NotificacionCompletada dispara
           → Task.Run → UPDATE SQLite (background)

t=3000ms   Front recibe NotificacionCompletada
           → Indicador verde "Pago exitoso"
           → Auto-cierre en 2s

TOTAL PERCIBIDO: ~3s (solo el tiempo real del POS)
```

### 4.2 POS falla → retry automatico

```
t=0ms      Usuario click "Pagar con POS"
t=50ms     RegisterNotificacion() → RAM: Pendiente

t=200ms    LlamarPosApiAsync() → timeout / error
           → UpdateStatus(notifId, Error, "POS no responde")
           → Evento NotificacionFallida
           → Front: modal de error "El POS no respondio"

t=30s      NotificacionRetryService (timer background)
           → ObtenerPendientesExpiradas(30)
           → Encuentra notifId con estado Error, reintentos=0
           → DebeReintentar(0)? SI (max=3)
           → IncrementarReintento(notifId)
           → ConsultarEstadoEnPosAsync(posIp, ordenpedidoId)
           → POS respondio "completada"
           → UpdateStatus(notifId, Completada)
           → Evento NotificacionCompletada

t=60s      (si reintento 1 falla)
           → DebeReintentar(1)? SI → delay 60s

t=180s     (si reintento 2 falla)
           → DebeReintentar(2)? SI → delay 120s

t=300s     (si reintento 3 falla)
           → DebeReintentar(3)? NO → max alcanzado
           → UpdateStatus(notifId, Timeout)
           → Evento NotificacionTimeout
           → Dashboard muestra alerta TIMEOUT
```

### 4.3 QuipuNetX se cae y se reinicia

```
t=0     RegisterNotificacion() → BD: Pendiente, RAM: Pendiente
t=1     ⚡ QuipuNetX CRASH ⚡ → RAM perdida
t=2     POS procesa el cobro exitosamente (no sabe que QuipuNetX cayo)
t=3     QuipuNetX REINICIA
          → NotificacionStatusManager.Initialize():
            Paso 1 (BD → RAM): SELECT notificacion WHERE estado='1'
            → Recupera pendientes en RAM
          → NotificacionRetryService arranca
            → Detecta pendientes expiradas
            → ConsultarEstadoEnPosAsync → POS dice "completada"
            → UpdateStatus(Completada)
          → Ninguna notificacion se pierde
```

---

## 5. Mapa de clases y responsabilidades (SRP estricto)

Replicando el patron de PrinterServices donde cada clase tiene UNA sola responsabilidad:

| Clase | Responsabilidad | Equivalente en PrinterServices | NO hace |
|---|---|---|---|
| `NotificacionStatusManager` | Almacen RAM + eventos C# | `PrintJobStatusManager` | No persiste BD, no hace HTTP |
| `NotificacionService` | CRUD + queries en SQLite | `PrintJobStatusPersister` | No dispara eventos, no conoce UI |
| `NotificacionRetryService` | Timer background + reintentos | `PrintWorker` + `QuipuNetXCallbackClient` | No modifica RAM directamente |
| `NotificacionQueryService` | Consulta HTTP al POS | `QuipuNetXCallbackClient` | No modifica BD ni RAM |
| `ExponentialRetryPolicy` | Calculo de delays + max reintentos | (logica inline en PrintWorker) | No conoce notificaciones |
| `NotificacionController` | Coordinador: orquesta services | `PrintController` | No tiene logica propia |
| `NotificacionServer` | Parse JSON → controller | `PrinterNotificationServiceImpl` | Solo deserializa |
| `PSNotificacionProgressService` | Lee del Manager, binding WPF | `PSProgressService` | No modifica estados |
| `NotificacionDashboardModule` | HTML + API endpoints Nancy | (no existe en PrinterServices) | Solo consulta, no modifica |

---

## 6. Eventos: Comparativa lado a lado

```
PrintJobStatusManager                    NotificacionStatusManager
─────────────────────                    ─────────────────────────

JobStatusChanged(jobId, status)    →     StatusChanged(notifId, status)
AllJobsDone(pedidoIds)             →     NotificacionCompletada(notifId)
AnyJobFailed(jobId, error)         →     NotificacionFallida(notifId, error)
AnyJobExpired(jobId)               →     NotificacionTimeout(notifId)
OpenProgress                       →     OpenProgress
HideProgress                       →     HideProgress

// NUEVO (sin equivalente en PrinterServices):
                                         RetryIniciado(notifId, intento)
                                         // Para que el dashboard muestre
                                         // "Reintentando (2/3)..."
```

---

## 7. Modelo de datos

### 7.1 Tabla `notificacion` (SQLite existente de QuipuNetX)

```sql
CREATE TABLE IF NOT EXISTS notificacion (
    ID                    INTEGER PRIMARY KEY AUTOINCREMENT,
    NOTIFICACIONID        TEXT    NOT NULL UNIQUE,     -- UUID v4
    TIPNOTIFICACION       TEXT    NOT NULL,             -- "ordenpedido"|"delivery"|"venta"
    IDENTIFICADORASOCIADO TEXT    NOT NULL,             -- UUID del registro asociado
    IPORIGEN              TEXT,                         -- IP dispositivo cliente
    IPDESTINO             TEXT,                         -- IP del POS destino
    NOMBREDISPOSITIVO     TEXT,                         -- Nombre del dispositivo origen
    TIPOPAGO              TEXT,                         -- "izipay"|"credibanco"|"niubiz"
    ESTADONOTIFICACION    TEXT    NOT NULL DEFAULT '1', -- 1=Pendiente 2=Completada 3=Error 4=Timeout
    FECHACREACION         TEXT    NOT NULL,
    FECHARESPUESTA        TEXT,
    CANTIDADREINTENTOS    INTEGER NOT NULL DEFAULT 0,
    PAYLOADENVIO          TEXT,                         -- JSON body (para replay)
    MENSAJERESPUESTA      TEXT,
    VENTAID               TEXT
);

CREATE INDEX IF NOT EXISTS idx_notif_estado ON notificacion(ESTADONOTIFICACION);
CREATE INDEX IF NOT EXISTS idx_notif_tipo ON notificacion(TIPNOTIFICACION);
CREATE INDEX IF NOT EXISTS idx_notif_fecha ON notificacion(FECHACREACION);
```

### 7.2 Extension de `transaccionordenpedido`

```sql
ALTER TABLE transaccionordenpedido ADD COLUMN TRANSACCIONORDENPEDIDOID TEXT;  -- UUID unico
ALTER TABLE transaccionordenpedido ADD COLUMN NOTIFICACIONID TEXT;            -- FK a notificacion
ALTER TABLE transaccionordenpedido ADD COLUMN IPORIGEN TEXT;                  -- IP cliente origen
```

---

## 8. Endpoints REST

### 8.1 API operativa (puerto 8081)

| Metodo | Ruta | Descripcion | Quien llama |
|---|---|---|---|
| POST | `/api/rest/notificaciones/v1/recibirRespuesta` | POS notifica resultado | POS (Java) |
| GET | `/api/rest/notificaciones/v1/consultarEstado?id=UUID` | Estado de una notificacion | Front / POS |
| POST | `/api/rest/notificaciones/v1/reintentarManual` | Reintento manual | Dashboard |

### 8.2 API del Dashboard (puerto 8083)

| Metodo | Ruta | Descripcion |
|---|---|---|
| GET | `/notif/` | HTML dashboard completo |
| GET | `/notif/api/list?estado=X&tipo=Y&page=1` | JSON paginado con filtros |
| GET | `/notif/api/stats` | Contadores + datos graficas |
| GET | `/notif/api/devices` | Ping a DeviceClient + POS |
| GET | `/notif/api/detail/{notifId}` | Detalle con payload completo |
| POST | `/notif/api/retry` | Reintento manual |
| GET | `/notif/api/export/csv` | Exportar CSV |

---

## 9. Integracion con OrdenpedidoController (el punto de entrada)

Mismo patron que `PedidoController.addLista()` registra jobs en `PrintJobStatusManager`:

```csharp
// OrdenpedidoController.agregarAsync() — MODIFICACION

// ANTES de LlamarPosApiAsync:
string notifId = NotificacionStatusManager.Instance.RegisterNotificacion(
    new CrearNotificacionRequest
    {
        TipoNotificacion      = TipoNotificacion.OrdenPedido,
        IdentificadorAsociado = trxUuid,
        IpOrigen              = ordenpedido.Ordenpedido_iporigen,
        IpDestino             = posIp,
        NombreDispositivo     = ObtenerNombreDispositivo(ordenpedido.Ordenpedido_iporigen),
        TipoPago              = ordenpedido.PagoPos.ClaveJson,
        PayloadEnvio          = BuildPayloadParaNotificacion(ordenpedido, rMontos)
    });

// Si POS OK:
NotificacionStatusManager.Instance.UpdateStatus(notifId, EstadoNotificacion.Completada, "OK", ventaId);

// Si POS falla:
NotificacionStatusManager.Instance.UpdateStatus(notifId, EstadoNotificacion.Error, errorMsg);
```

---

## 10. Conexion con el Front (modales WPF)

Mismo patron que `PSProgressService` se suscribe a `PrintJobStatusManager`:

```
NotificacionStatusManager                Front (modales WPF)
─────────────────────────                ───────────────────

OpenProgress ──────────────────────► showProcesandoPOS()
                                     (abre modal "Procesando pago...")

StatusChanged(notifId, "Completada")──► Modal verde "Pago exitoso"
                                        Auto-cierre en 2s

NotificacionFallida(notifId, err) ───► showErrorPOS(err, onRetry)
                                       (modal rojo con boton reintento)

NotificacionTimeout(notifId) ────────► showTimeoutPOS()
                                       (modal: "Maximo de reintentos alcanzado"
                                        + boton reintento manual)
```

---

## 11. Mecanismo de reintentos (backoff exponencial)

```
ExponentialRetryPolicy:
  MaxReintentos = 3
  GetDelaySegundos(intento):
    intento 1 → 30s
    intento 2 → 60s
    intento 3 → 120s
    intento 4+ → NO reintentar → marcar TIMEOUT

NotificacionRetryService (timer background cada 30s):
  1. ObtenerPendientesExpiradas(umbralSegundos=30)
  2. Por cada notificacion pendiente/error:
     a. DebeReintentar(reintentos)?
        NO  → UpdateStatus(Timeout)
        SI  → IncrementarReintento()
              ConsultarEstadoEnPosAsync(posIp, ordenpedidoId)
                → Completada → UpdateStatus(Completada)
                → Pendiente  → esperar siguiente ciclo
                → Sin respuesta → esperar siguiente ciclo
```

### Comparativa con PrinterServices:

| Aspecto | PrinterServices | Notificaciones POS |
|---|---|---|
| Worker | `PrintWorker` (event-driven, SemaphoreSlim) | `NotificacionRetryService` (timer 30s) |
| Canal de retorno | HTTP callback fire-and-forget + cola retry | HTTP callback del POS + retry polling |
| Backoff | 500ms, 1s, 2s (3 reintentos rapidos) | 30s, 60s, 120s (3 reintentos lentos) |
| Estado terminal | DONE / FAILED / EXPIRED | Completada / Error / Timeout |
| Persistencia retry | `ConcurrentQueue` en PrinterServices | Tabla `notificacion` en QuipuNetX |

---

## 12. Arbol de archivos

```
QuipuNetX/
├── Notificaciones/
│   ├── Domain/
│   │   ├── EstadoNotificacion.cs        // Enum: Pendiente=1, Completada=2, Error=3, Timeout=4
│   │   └── TipoNotificacion.cs          // Constantes: "ordenpedido", "delivery", "venta"
│   ├── Interfaces/
│   │   ├── INotificacionService.cs      // Contrato CRUD + query
│   │   └── IRetryPolicy.cs             // Politica de reintento (ISP)
│   ├── Dto/
│   │   ├── CrearNotificacionRequest.cs  // DTO entrada
│   │   ├── RecibirRespuestaDto.cs       // DTO endpoint receptor
│   │   ├── NotificacionDto.cs           // DTO salida dashboard
│   │   └── NotificacionStatsDto.cs      // Estadisticas dashboard
│   ├── Services/
│   │   ├── NotificacionStatusManager.cs // RAM + eventos (~120 lineas)
│   │   ├── NotificacionService.cs       // CRUD SQLite (~80 lineas)
│   │   ├── ExponentialRetryPolicy.cs    // Backoff 30s/60s/120s (~30 lineas)
│   │   ├── NotificacionRetryService.cs  // Timer background (~100 lineas)
│   │   └── NotificacionQueryService.cs  // Consulta HTTP al POS (~50 lineas)
│   ├── controller/
│   │   └── NotificacionController.cs    // Coordinador (~60 lineas)
│   └── server/
│       └── NotificacionServer.cs        // Parse JSON → controller (~40 lineas)
├── entitybase/
│   └── NotificacionBase.cs              // Campos + Column + fromJSON/toJSON
├── entity/
│   └── Notificacion.cs                  // Hereda SugarRecord + queries
└── ws/modules/
    └── NotificacionDashboardModule.cs   // Nancy: HTML + API dashboard

QuipuNet/ (Front)
└── MainApp/Services/
    └── PSNotificacionProgressService.cs // Lee de NotificacionStatusManager, WPF binding
```

---

## 13. Inicializacion y registro

```
SyncService.start():
  → NotificacionStatusManager.Instance.Initialize()  // BD → RAM + sync pendientes
  → NotificacionRetryService.Instance.Start()         // Timer background cada 30s

SyncService.stopAllSync():
  → NotificacionRetryService.Instance.Stop()          // Detener timer

WebServerNewBootstrapper:
  → container.Register<NotificacionDashboardModule>() // Nancy dashboard

RestaurantpeApplication:
  → Notificacion.EnsureTable()                        // Crear tabla si no existe
```

---

## 14. Resumen de decisiones

| Pregunta | Decision | Justificacion |
|---|---|---|
| Patron arquitectonico | **RAM-first + BD backup + eventos C#** | Probado en PrintJobStatusManager: 0ms latencia UI |
| BD | **SQLite existente** | JOINs con transaccionordenpedido, SugarRecord ORM, zero infra |
| Reintentos | **Backoff exponencial 30s/60s/120s, max 3** | Balance entre agresividad y carga al POS |
| UI feedback | **Eventos C# → WPF binding** | Mismo patron PSProgressService, ya validado |
| Dashboard | **Nancy + HTML inline + Chart.js CDN, puerto 8083** | Compatible .NET 4.5.2, separado del operativo |
| Compatibilidad Java | **REST/JSON estandar, contratos versionados /v1/** | Sin acoplamiento a C# |
| Thread safety | **ConcurrentDictionary + lock en timer** | Mismo patron PrintJobStatusManager |
| Recovery ante caida | **Initialize(): BD → RAM + sync pendientes** | Identico a PrintJobStatusRecovery |
| Feature flag | **HABILITAR_RETRY_NOTIFICACIONES** (default: true) | Permite desactivar sin deploy |

---

## 15. Plan de fases

| Fase | Que | Dependencia | Estimado archivos |
|---|---|---|---|
| **0** | Contratos y modelos (enums, DTOs, interfaces) | Ninguna | 7 |
| **1** | Entidades y BD (NotificacionBase, Notificacion, ALTER TABLE) | Fase 0 | 2 |
| **2** | NotificacionStatusManager + NotificacionService + RetryPolicy | Fase 1 | 3 |
| **3** | NotificacionRetryService + NotificacionQueryService | Fase 2 | 2 |
| **4** | NotificacionController + NotificacionServer | Fase 3 | 2 |
| **5** | Integracion con OrdenpedidoController (el punto de entrada real) | Fase 4 | 1 (modificacion) |
| **6** | PSNotificacionProgressService (Front WPF) | Fase 2 | 1 |
| **7** | Dashboard Nancy + API | Fase 4 | 1 |
| **8** | Registro, inicializacion, feature flag | Fase 5 | 3 (modificaciones) |
| **9** | Tests | Todas | 3 |
