# Vibe Engineering — Health Dashboard desde PrinterServices

## 1. Resumen

Extension del dashboard de PrinterServices (`/api/dashboard`) para convertirlo en un centro de monitoreo completo del ecosistema QuipuNet. El dashboard es el punto unico de observabilidad que muestra estado del servicio de impresion, red, terminal, dispositivos descubiertos, y estado de QuipuNet.exe (Front).

**Premisa:** PrinterServices siempre viene de una instalacion previa con BD preexistente. Las tablas nuevas se crean automaticamente al arrancar (patron `CreateTable` de sqlite-net). No requiere migracion manual.

---

## 2. Bug Fix: Estado de Red "undefined"

**Causa raiz:** Mismatch de propiedades entre API y HTML.
- `DashboardController.cs:716` envia `status`
- `dashboard.html:378` leia `netStatus.healthStatus`

**Fix:** Cambiar `healthStatus` → `status` en dashboard.html.

---

## 3. Arquitectura de las nuevas funcionalidades

```
┌──────────────────────────────────────────────────────────────────┐
│                    DASHBOARD (browser)                            │
│  /api/dashboard                                                  │
│                                                                  │
│  ┌─────────┐ ┌──────────┐ ┌──────────┐ ┌────────┐ ┌──────────┐ │
│  │Estado   │ │ Sistema  │ │Velocidad │ │Dispos. │ │QuipuNet  │ │
│  │de Red   │ │Terminal  │ │Red+Chart │ │en Red  │ │Health    │ │
│  │(fix)    │ │(CPU/RAM) │ │(2h/manual)││(1h/man)│ │(manual)  │ │
│  └─────────┘ └──────────┘ └──────────┘ └────────┘ └──────────┘ │
│  ┌──────────────┐ ┌──────────────┐ ┌───────────────────────┐    │
│  │SQL Console   │ │Actualizar    │ │Screenshot QuipuNet    │    │
│  │(modal,local) │ │Servicio      │ │(bajo demanda)         │    │
│  └──────────────┘ └──────────────┘ └───────────────────────┘    │
└──────────────────────────────────────────────────────────────────┘
         │ Proxy HTTP          │ Directo (SQLite)
         ▼                     ▼
┌─ PrinterServices ──────────────────────────────────────────────┐
│                                                                 │
│  DashboardController (endpoints proxy + directos)               │
│  ├─ HandleSpeedHistory()      ← lee network_speed_log           │
│  ├─ HandleNetworkDevices()    ← lee devices_on_network          │
│  ├─ HandleSqlQuery()          ← ejecuta SQL en SQLite           │
│  ├─ HandleUpdateRequest()     ← proxy POST a QuipuNetX          │
│  ├─ HandleQuipuNetHealth()    ← proxy GET a QuipuNetX           │
│  └─ HandleQuipuNetScreenshot()← proxy GET a QuipuNetX           │
│                                                                 │
│  Workers independientes (hilos LongRunning):                    │
│  ├─ NetworkSpeedWorker     (cada 2h + MeasureNow())             │
│  ├─ NetworkDiscoveryWorker (cada 1h + ScanNow())                │
│  ├─ NetworkWatcher         (existente, cada 30s)                │
│  ├─ StatusMonitor          (existente, cada 3-15s)              │
│  └─ ArpScanWorker          (existente, event-driven)            │
│                                                                 │
│  Tablas SQLite nuevas:                                          │
│  ├─ network_speed_log      (velocidad + latencia)               │
│  └─ devices_on_network     (dispositivos descubiertos)          │
│                                                                 │
│  Servicios nuevos:                                              │
│  ├─ SystemInfoCollector    (CPU/RAM/Disco via WMI, con cache)   │
│  └─ OuiLookup              (fabricante por MAC OUI)             │
└─────────────────────────────────────────────────────────────────┘
         │ Proxy HTTP (puerto 8081)
         ▼
┌─ QuipuNetX (Nancy) ────────────────────────────────────────────┐
│                                                                 │
│  HealthModule (Nancy, ruta base /api/health):                   │
│  ├─ GET /status              → version, uptime, modo, admin,   │
│  │                             RAM, VCS locales/remotos         │
│  ├─ GET /vcs                 → count VCS por tipo               │
│  ├─ GET /screenshot          → captura pantalla (JPEG base64)  │
│  ├─ POST /update-printer-service    → trigger actualizacion     │
│  └─ GET /update-printer-service-status → progreso               │
│                                                                 │
│  ScreenCaptureService:                                          │
│  └─ CaptureScreenAsBase64() → Graphics.CopyFromScreen()        │
│     (System.Drawing, NO WPF — captura todo incluyendo modales) │
│                                                                 │
│  Datos obtenidos sin dependencia de UI:                         │
│  ├─ VCS: Vcs.getCountVCSPorTipo() → SELECT count(*) FROM vcs   │
│  ├─ RAM: GC.GetTotalMemory(false) / (1024*1024)                │
│  ├─ Admin: WindowsPrincipal.IsInRole(Administrator)             │
│  ├─ Modo: Util.esModoServidor()                                │
│  └─ Version: Assembly.GetExecutingAssembly().GetName().Version  │
└─────────────────────────────────────────────────────────────────┘
```

---

## 4. Archivos nuevos (8)

| # | Archivo | Proyecto | Proposito |
|---|---------|----------|-----------|
| 1 | `Services/System/SystemInfoCollector.cs` | PrinterServices | CPU/RAM/Disco via WMI con cache (CPU=permanente, RAM=60s, Disco=1h) |
| 2 | `Data/Models/NetworkSpeedLogEntity.cs` | PrinterServices | Entidad SQLite: velocidad descarga + latencia gateway |
| 3 | `Workers/NetworkSpeedWorker.cs` | PrinterServices | Thread LongRunning cada 2h. Descarga parcial printer.zip + ping gateway |
| 4 | `Data/Models/DeviceOnNetworkEntity.cs` | PrinterServices | Entidad SQLite: dispositivo descubierto (IP, MAC, vendor, hostname) |
| 5 | `Workers/NetworkDiscoveryWorker.cs` | PrinterServices | Thread LongRunning cada 1h. ARP scan rango subred + DNS + OUI |
| 6 | `Services/Network/OuiLookup.cs` | PrinterServices | Diccionario ~100 OUI: Epson, Star, Bixolon, TP-Link, MikroTik, etc. |
| 7 | `ws/modules/HealthModule.cs` | QuipuNetX | Nancy module: status, vcs, screenshot, update-printer-service |
| 8 | `Services/Health/ScreenCaptureService.cs` | QuipuNetX | Graphics.CopyFromScreen() → JPEG base64 (~200-500KB) |

---

## 5. Archivos modificados (7)

| # | Archivo | Cambios |
|---|---------|---------|
| 1 | `Resources/dashboard.html` | Fix undefined + 5 secciones nuevas (Sistema, Dispositivos, QuipuNet Health, SQL Console modal, botones speed/scan/update/screenshot) |
| 2 | `Api/Controllers/DashboardController.cs` | +serviceStartTime constructor, +system/latestSpeed/networkDevices en CollectSystemData(), +8 metodos handler nuevos, +WriteJsonResponse publico |
| 3 | `Api/ApiRouter.cs` | +10 rutas nuevas, +campos estaticos SpeedWorker/DiscoveryWorker |
| 4 | `Data/PrinterServiceDb.cs` | +2 CreateTable (network_speed_log, devices_on_network) + 3 indices |
| 5 | `PrinterServicesHost.cs` | +2 workers Start/Stop (NetworkSpeedWorker, NetworkDiscoveryWorker) |
| 6 | `PrinterServices.csproj` | +Reference System.Management, +6 Compile Include |
| 7 | `Config/ConfigManager.cs` | +2 defaults (NetworkSpeedIntervalMinutes=120, NetworkDiscoveryIntervalMinutes=60) |

---

## 6. Tablas SQLite nuevas

### network_speed_log

```sql
CREATE TABLE network_speed_log (
    id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    download_speed_kbps REAL,
    latency_ms         INTEGER,
    gateway_ip         TEXT,
    network_id         TEXT,
    measured_at        TEXT
);
CREATE INDEX idx_speed_measured ON network_speed_log(measured_at);
```

### devices_on_network

```sql
CREATE TABLE devices_on_network (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    ip_address      TEXT,
    mac_address     TEXT,
    hostname        TEXT,
    vendor          TEXT,
    device_type     TEXT,
    first_seen_at   TEXT,
    last_seen_at    TEXT,
    is_online       INTEGER DEFAULT 0,
    network_id      TEXT,
    gateway_mac     TEXT
);
CREATE UNIQUE INDEX idx_device_mac ON devices_on_network(mac_address);
CREATE INDEX idx_device_online ON devices_on_network(is_online);
```

---

## 7. Threading e intervalos

| Worker | Intervalo | Hilo | Manual desde dashboard | Bloquea otros? |
|--------|-----------|------|------------------------|----------------|
| NetworkSpeedWorker | 2 horas | LongRunning | POST /api/dashboard/speed-measure | No |
| NetworkDiscoveryWorker | 1 hora | LongRunning | POST /api/dashboard/network-scan | No |
| SystemInfoCollector.Disco | 1 hora cache | En request thread | No | No |
| SystemInfoCollector.RAM | 60s cache | En request thread | No | No |
| SystemInfoCollector.CPU | Permanente cache | Primera llamada | No | No |

---

## 8. Seguridad

| Feature | Restriccion |
|---------|-------------|
| SQL Console | Solo localhost (127.0.0.1 / ::1). Blacklist: DROP TABLE, ALTER TABLE, PRAGMA journal_mode, ATTACH, DETACH. Write requiere confirmacion. Max 500 filas SELECT. Log4net audit. |
| Update Remoto | Requiere confirmacion del usuario (prompt + confirm). Proxy a QuipuNetX que ejecuta PrinterServiceInstaller ya existente. |
| Screenshot | Solo bajo demanda (boton). JPEG comprimido 70% calidad (~200-500KB). |

---

## 9. Endpoints nuevos

### PrinterServices (dashboard directo)

| Metodo | Ruta | Descripcion |
|--------|------|-------------|
| POST | `/api/dashboard/sql` | SQL Console (solo localhost) |
| GET | `/api/dashboard/speed-history?limit=48` | Historial velocidad |
| POST | `/api/dashboard/speed-measure` | Forzar medicion |
| GET | `/api/dashboard/network-devices` | Dispositivos descubiertos |
| POST | `/api/dashboard/network-scan` | Forzar escaneo |
| POST | `/api/dashboard/update` | Proxy: trigger update |
| GET | `/api/dashboard/update-status` | Proxy: estado update |
| GET | `/api/dashboard/quipunet-health` | Proxy: QuipuNet health |
| GET | `/api/dashboard/quipunet-screenshot` | Proxy: screenshot |

### QuipuNetX (Nancy, consumido por PrinterServices via proxy)

| Metodo | Ruta | Descripcion |
|--------|------|-------------|
| GET | `/api/health/status` | Version, uptime, modo, admin, RAM, VCS |
| GET | `/api/health/vcs` | VCS locales + remotos |
| GET | `/api/health/screenshot` | JPEG base64 pantalla completa |
| POST | `/api/health/update-printer-service` | Trigger actualizacion |
| GET | `/api/health/update-printer-service-status` | Estado actualizacion |

---

## 10. Migracion BD preexistente

Las tablas nuevas usan `CreateTable<Entity>()` de sqlite-net en `PrinterServiceDb.CreateTables()`:
- Si la tabla no existe → la crea completa con todas las columnas
- Si ya existe → agrega columnas nuevas automaticamente (ALTER TABLE ADD COLUMN interno)
- Indices con `CREATE INDEX IF NOT EXISTS` → idempotente
- **No requiere migracion manual** para tablas nuevas (solo para columnas en tablas existentes, que no aplica aqui)
