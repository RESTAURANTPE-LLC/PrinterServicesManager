# USB Driver Printer — Documentacion Tecnica

## Resumen

PrinterServices soporta impresoras termicas POS 80mm conectadas por **USB** ademas de las impresoras de **RED (TCP:9100)**. La impresion USB funciona **sin drivers de impresora** — se envian bytes ESC/POS crudos directamente al dispositivo USB usando la API de Windows (SetupAPI + CreateFile).

La clave del diseno es que cada impresora USB se identifica por su **VID+PID+Serial Number** (inmutable), no por el device path (que cambia si el usuario mueve la impresora a otro puerto USB).

---

## Analogia USB vs RED

El soporte USB sigue exactamente el mismo patron arquitectonico que las impresoras de red:

| Concepto | Impresora RED | Impresora USB |
|---|---|---|
| Identidad fisica inmutable | MAC Address | VID + PID + Serial Number |
| Ubicacion mutable | IP (cambia por DHCP) | Device Path (cambia por puerto USB) |
| Resolucion de identidad | ARP scan (MAC -> IP) | SetupAPI (UniqueKey -> DevicePath) |
| Transporte (impresion) | `TcpTransport` (TCP:9100) | `UsbTransport` (CreateFile raw, resuelve DevicePath) |
| Transporte (monitoreo) | Socket directo | `UsbTransportDirect` (DevicePath ya resuelto) |
| Monitor de estado | `PrinterStatusChecker` (DLE EOT via TCP) | `UsbPrinterStatusChecker` (DLE EOT via USB) |
| Protocolo de impresion | ESC/POS | ESC/POS (identico) |
| Deteccion de cambio | ArpScanWorker (nueva IP por MAC) | UsbDeviceEnumerator (nuevo DevicePath por UniqueKey) |

---

## Componentes

### 1. UsbDeviceIdentity (`Transport/UsbDeviceIdentity.cs`)

Clase que representa la identidad inmutable de una impresora USB.

**Propiedades:**

| Propiedad | Tipo | Descripcion |
|---|---|---|
| `Vid` | string | Vendor ID del fabricante (ej: `04B8` = Epson) |
| `Pid` | string | Product ID del modelo (ej: `0202`) |
| `SerialNumber` | string | Numero de serie unico por unidad |
| `DevicePath` | string | Path actual del dispositivo en Windows (cambia con el puerto) |
| `FriendlyName` | string | Nombre legible (ej: "EPSON TM-T20II Receipt") |
| `UniqueKey` | string | Clave unica: `{VID}_{PID}_{Serial}` (ej: `04B8_0202_J9SG012345`) |

**UniqueKey** es el identificador permanente. Si la impresora no tiene serial number (algunas chinas baratas), se usa `{VID}_{PID}` (ambiguo si hay 2 iguales del mismo modelo).

---

### 2. UsbDeviceEnumerator (`Transport/UsbDeviceEnumerator.cs`)

Enumera impresoras USB conectadas usando la **Windows SetupAPI** (P/Invoke). Analogo a `ArpHelper` / `PrinterIpResolver` para impresoras de red.

**API publica:**

```csharp
// Enumerar TODAS las impresoras USB conectadas ahora
List<UsbDeviceIdentity> devices = UsbDeviceEnumerator.EnumerateUsbPrinters();

// Buscar una impresora especifica por UniqueKey (resuelve DevicePath actual)
UsbDeviceIdentity device = UsbDeviceEnumerator.FindByUniqueKey("04B8_0202_J9SG012345");

// Verificar si esta conectada (rapido, no abre el dispositivo)
bool connected = UsbDeviceEnumerator.IsConnected("04B8_0202_J9SG012345");
```

**Como funciona internamente:**

1. Llama a `SetupDiGetClassDevs` con la GUID de la clase USB Print (`28d78fad-5a12-11d1-ae5b-0000f803a8c2`)
2. Itera cada interfaz con `SetupDiEnumDeviceInterfaces`
3. Obtiene el device path con `SetupDiGetDeviceInterfaceDetail`
4. Parsea VID, PID y Serial del device path usando regex
5. Obtiene el nombre amigable con `SetupDiGetDeviceRegistryProperty`

**Formato del device path de Windows:**

```
\\?\usb#vid_04b8&pid_0202#J9SG012345#{28d78fad-5a12-11d1-ae5b-0000f803a8c2}
      └─VID──┘ └─PID──┘ └─Serial──┘ └──────────GUID clase──────────────────┘
```

Cuando el usuario desconecta la impresora del puerto USB 1 y la conecta al puerto USB 3, el device path cambia (incluye info del hub/puerto), pero el VID+PID+Serial se mantiene identico.

---

### 3. UsbTransport y UsbTransportDirect (`Transport/UsbTransport.cs`)

Dos implementaciones de `ITransport` para dispositivos USB:

**`UsbTransport`** (publico) — Para impresion. Recibe un `UniqueKey` y resuelve el DevicePath automaticamente en cada conexion via `UsbDeviceEnumerator.FindByUniqueKey()`. Detecta si la impresora cambio de puerto USB.

```
ConnectAsync(ct)
  1. UsbDeviceEnumerator.FindByUniqueKey(uniqueKey)  -> obtiene DevicePath actual
  2. Si DevicePath cambio respecto al ultimo -> loguea "cambio de puerto USB"
  3. CreateFile(devicePath, GENERIC_WRITE | GENERIC_READ, ...)  -> abre dispositivo
  4. new FileStream(handle, ReadWrite)  -> stream para enviar/recibir bytes
```

**`UsbTransportDirect`** (internal) — Para monitoreo de estado. Recibe un DevicePath **ya resuelto** y lo abre directamente sin re-enumerar. Usado por `UsbPrinterStatusChecker` para evitar doble enumeracion: el checker ya hizo `FindByUniqueKey` para verificar conexion, y pasa el DevicePath obtenido a `UsbTransportDirect` sin volver a enumerar.

**Ejemplo de uso (impresion):**

```csharp
// PrintWorker usa UsbTransport (resuelve UniqueKey -> DevicePath automaticamente)
using (var transport = new UsbTransport("04B8_0202_J9SG012345"))
{
    await transport.ConnectAsync(ct);
    await transport.SendAsync(payloadEscPos, ct);
    transport.Disconnect();
}
```

El payload ESC/POS es **identico** al que se envia por red — los mismos bytes generados por `EscPosCommandBuilder`. Solo cambia el "tubo" (USB vs TCP).

---

### 4. UsbPrinterStatusChecker (`Monitoring/UsbPrinterStatusChecker.cs`)

Verifica el estado de impresoras USB. Analogo a `PrinterStatusChecker` para red.

**Proceso de verificacion (2 pasos, 1 sola enumeracion):**

| Paso | Que hace | Que verifica |
|---|---|---|
| 1. SetupAPI | `FindByUniqueKey()` | Esta conectada fisicamente? Obtiene DevicePath actual |
| 2. DLE EOT | `UsbTransportDirect(devicePath)` — abre con DevicePath ya resuelto | Papel, tapa, error, ready |

**Optimizacion:** El DevicePath resuelto en paso 1 se reutiliza en paso 2 (via `UsbTransportDirect`) y se retorna en `PrinterStatus.ResolvedUsbDevicePath` para que `StatusMonitor` detecte cambios de puerto sin re-enumerar.

**Comandos DLE EOT (identicos a red):**

| Comando | Bytes | Que consulta |
|---|---|---|
| DLE EOT 1 | `0x10 0x04 0x01` | Printer status (ready/not ready) |
| DLE EOT 2 | `0x10 0x04 0x02` | Offline causes (cover open) |
| DLE EOT 3 | `0x10 0x04 0x03` | Error status (recoverable) |
| DLE EOT 4 | `0x10 0x04 0x04` | Paper sensor (paper present/absent) |

**Resultado:** Retorna un `PrinterStatus` con: `Online`, `TienePapel`, `TapaAbierta`, `DisponibleParaImprimir`, `RawStatus`, `ResolvedUsbDevicePath`.

---

## Modelo de datos

### Campos USB en PrinterEntity (`Data/Models/PrinterEntity.cs`)

| Columna | Tipo | Default | Descripcion |
|---|---|---|---|
| `tipo_conexion` | TEXT | `"RED"` | `"RED"` o `"USB"` |
| `usb_unique_key` | TEXT | null | Identidad inmutable: `VID_PID_SERIAL` |
| `usb_device_path` | TEXT | null | Device path actual (se actualiza si cambia de puerto) |
| `usb_friendly_name` | TEXT | null | Nombre legible del dispositivo |

### Campo USB en PrinterStatus (`Monitoring/PrinterStatusChecker.cs`)

| Campo | Tipo | Descripcion |
|---|---|---|
| `ResolvedUsbDevicePath` | string | DevicePath resuelto por UsbPrinterStatusChecker. StatusMonitor lo compara con el almacenado en BD para detectar cambio de puerto sin re-enumerar USB |

**Indice:** `UNIQUE INDEX idx_printers_usb_key ON printers(usb_unique_key)` — una identidad USB = una sola impresora. SQLite permite multiples NULLs, asi que impresoras de RED no conflictuan.

**Migracion automatica:** `PrinterServiceDb.CreateTables()` agrega las columnas si no existen (ALTER TABLE), compatible con BD existentes.

---

## Integracion con StatusMonitor

En `StatusMonitor.CheckAllPrintersAsync()`, el query ahora incluye impresoras USB:

```sql
SELECT * FROM printers
WHERE (ip IS NOT NULL AND ip != '')
   OR (usb_unique_key IS NOT NULL AND usb_unique_key != '')
```

Para cada impresora, se elige el checker segun `TipoConexion`:

```
Si TipoConexion == "USB" y UsbUniqueKey no vacio:
  → UsbPrinterStatusChecker.CheckAsync(uniqueKey, timeout, ct)
     (internamente: 1 sola enumeracion USB + DLE EOT via UsbTransportDirect)
  → Si status.ResolvedUsbDevicePath != printer.UsbDevicePath → actualizar en BD
  → SNMP NO aplica a USB (se omite auto-deteccion SNMP)
  → ARP NO aplica a USB (se omite delegacion a ArpScanWorker)

Si TipoConexion == "RED" (o default):
  → PrinterStatusChecker.CheckAsync(ip, puerto, timeout, ct)
  → Flujo existente (SNMP, ARP, etc.)
```

Las transiciones de estado (ONLINE/OFFLINE, DISPONIBLE/NO_DISPONIBLE) funcionan igual para USB y RED: se loguean, se notifican via gRPC, se re-encolan jobs WAITING.

---

## Integracion con PrintWorker

En `PrintWorker.ProcessJobAsync()`, se consulta la BD **una sola vez** para obtener tanto el tipo de conexion como la IP actualizada (evita doble query):

```
1. Consultar PrinterEntity por ImpresoraId (1 sola query)
2. Si TipoConexion == "USB" → ProcessUsbJobAsync(job, usbUniqueKey)
3. Si TipoConexion == "RED" → flujo existente (reutiliza printerEntity para IP/puerto)
```

`ProcessUsbJobAsync` replica la misma logica que el flujo RED:

1. Pre-check con `UsbPrinterStatusChecker` (papel, tapa, conectividad)
2. Si OFFLINE → marcar WAITING (StatusMonitor lo re-encolara cuando vuelva)
3. Si sin papel o tapa abierta → marcar FAILED
4. Construir payload ESC/POS con `EscPosCommandBuilder` (identico)
5. Enviar con `SendUsbWithRetry` usando `UsbTransport` (resuelve DevicePath automaticamente)
6. Reintentos exponenciales (500ms, 1000ms, 2000ms)
7. Instrumentacion de latencias (Fase 8)
8. Notificaciones gRPC + callbacks HTTP (Fase 23)

---

## API HTTP

### Descubrir impresoras USB conectadas

```
GET /api/printer/usb/discover
```

**Respuesta:**

```json
{
  "count": 2,
  "printers": [
    {
      "vid": "04B8",
      "pid": "0202",
      "serialNumber": "J9SG012345",
      "uniqueKey": "04B8_0202_J9SG012345",
      "devicePath": "\\\\?\\usb#vid_04b8&pid_0202#J9SG012345#{28d78fad-...}",
      "friendlyName": "EPSON TM-T20II Receipt"
    },
    {
      "vid": "0483",
      "pid": "5743",
      "serialNumber": "ABC123",
      "uniqueKey": "0483_5743_ABC123",
      "devicePath": "\\\\?\\usb#vid_0483&pid_5743#ABC123#{28d78fad-...}",
      "friendlyName": "USB Printing Support"
    }
  ]
}
```

### Registrar impresora USB

```
POST /api/printer/register

{
  "impresora_id": "BARRA",
  "nombre": "Barra Principal",
  "tipo_conexion": "USB",
  "usb_unique_key": "04B8_0202_J9SG012345",
  "usb_friendly_name": "EPSON TM-T20II Receipt",
  "modelo": "EPSON_TM_T20II"
}
```

**Notas:**
- `ip` NO es requerido para impresoras USB (solo para RED)
- `usb_unique_key` es obligatorio para USB
- `tipo_conexion` debe ser `"USB"`

### Consultar estado (incluye info USB)

```
GET /api/printer/status
GET /api/printer/status/{impresoraId}
```

La respuesta ahora incluye campos USB:

```json
{
  "impresoraId": "BARRA",
  "nombre": "Barra Principal",
  "online": true,
  "tienePapel": true,
  "tapaAbierta": false,
  "tipoConexion": "USB",
  "usbUniqueKey": "04B8_0202_J9SG012345",
  "usbDevicePath": "\\\\?\\usb#vid_04b8&pid_0202#J9SG012345#{28d78fad-...}",
  "usbFriendlyName": "EPSON TM-T20II Receipt"
}
```

---

## Flujo completo: Alta y uso de impresora USB

```
PASO 1: DESCUBRIMIENTO
  Front llama GET /api/printer/usb/discover
  PrinterServices enumera dispositivos USB via SetupAPI
  Retorna lista con VID, PID, Serial, FriendlyName
  Front muestra lista al usuario para que elija

PASO 2: REGISTRO
  Usuario selecciona impresora de la lista
  Front llama POST /api/printer/register con tipo_conexion=USB y usb_unique_key
  PrinterServices guarda en BD con identidad USB inmutable

PASO 3: MONITOREO (automatico, cada N segundos)
  StatusMonitor detecta TipoConexion=USB
  UsbPrinterStatusChecker verifica (1 sola enumeracion USB):
    - SetupAPI: esta conectada? (busca por UniqueKey, no por DevicePath)
    - Si la encuentra: DLE EOT via UsbTransportDirect (DevicePath ya resuelto)
    - Si ResolvedUsbDevicePath cambio: loguea "cambio de puerto USB", actualiza BD
    - Si no la encuentra: marca OFFLINE
  Transiciones notificadas via gRPC a clientes QuipuNet

PASO 4: IMPRESION
  PrintWorker recibe job, consulta BD una sola vez
  Detecta TipoConexion=USB → ProcessUsbJobAsync
  Pre-check con UsbPrinterStatusChecker (papel, tapa, conectividad)
  UsbTransport resuelve DevicePath actual automaticamente
  Envia payload ESC/POS (identico al de red)
  Si falla: reintentos exponenciales
  Si impresora offline: WAITING (se re-encola cuando vuelva)

PASO 5: CAMBIO DE PUERTO USB (transparente)
  Usuario desconecta impresora de USB-1 y conecta en USB-3
  Proximo ciclo de StatusMonitor:
    - UsbPrinterStatusChecker.FindByUniqueKey() encuentra la impresora en nuevo puerto
    - ResolvedUsbDevicePath != UsbDevicePath almacenado → loguea, actualiza BD
    - Impresora sigue ONLINE, sin interrupcion
  Proxima impresion:
    - UsbTransport.ConnectAsync() resuelve DevicePath actual
    - Imprime normalmente
```

---

## Optimizaciones de rendimiento

El modulo USB esta optimizado para minimizar enumeraciones USB (operacion costosa):

| Componente | Enumeraciones USB por ciclo | Detalle |
|---|---|---|
| `UsbPrinterStatusChecker` | 1 | `FindByUniqueKey` enumera una vez. El DevicePath obtenido se reutiliza en `UsbTransportDirect` (sin re-enumerar) y se retorna en `ResolvedUsbDevicePath` |
| `StatusMonitor` | 0 | Usa `status.ResolvedUsbDevicePath` del checker para detectar cambio de puerto, no re-enumera |
| `PrintWorker` | 1 | `UsbTransport.ConnectAsync` enumera una vez para resolver DevicePath actual |
| `PrintWorker` (query BD) | 1 | Una sola query a BD para obtener tipo de conexion + IP actualizada (antes eran 2 queries separadas) |

---

## VIDs conocidos de fabricantes POS

| VID | Fabricante | Modelos comunes |
|---|---|---|
| `04B8` | Epson | TM-T20II, TM-T88V, TM-U220 |
| `0519` | Star Micronics | TSP143, SP700 |
| `1504` | Bixolon | SRP-270, SRP-350 |
| `0483` | STMicroelectronics | Muchas impresoras chinas genericas |
| `0FE6` | ICS / Xprinter | XP-58, XP-80 |
| `0416` | WinChipHead | CH340 (converter USB-Serial, impresoras chinas) |
| `1A86` | QinHeng Electronics | CH341 (converter USB-Serial, impresoras chinas) |
| `0DD4` | Citizen | CT-S310II |
| `0B00` | Custom | Kube, VKP80 |

---

## Limitaciones conocidas

1. **Serial Number ausente**: Algunas impresoras chinas baratas no reportan serial number. En ese caso, `UniqueKey` es solo `VID_PID`, lo cual es ambiguo si hay 2 impresoras identicas del mismo modelo conectadas al mismo equipo. Solucion: el usuario debe diferenciarlas manualmente.

2. **Acceso exclusivo**: Si otra aplicacion (driver de Windows, otro software POS) tiene abierto el dispositivo USB, `CreateFile` fallara con error de acceso. PrinterServices necesita acceso exclusivo al dispositivo.

3. **Solo Windows**: `UsbDeviceEnumerator` usa SetupAPI (Windows-only). Para Linux seria `/dev/usb/lp0` + `libusb`. La interfaz `ITransport` permite agregar implementaciones sin modificar la logica de impresion.

4. **DLE EOT via USB**: Algunas impresoras chinas muy baratas no responden a DLE EOT via USB (aunque si aceptan datos de impresion). En ese caso, `UsbPrinterStatusChecker` reportara `Online=true` pero con status parcial (bytes de respuesta = 0x00). La impresion funciona igual.

5. **SyncPrinters no soporta USB**: El endpoint `POST /api/printers/sync` (usado por QuipuNetX para sincronizar en batch) requiere IP, por lo que solo funciona con impresoras de RED. Las impresoras USB se registran via `POST /api/printer/register`.

---

## Archivos del modulo USB

| Archivo | Lineas | Responsabilidad |
|---|---|---|
| `Transport/UsbDeviceIdentity.cs` | ~40 | Modelo de identidad USB inmutable (VID, PID, Serial, UniqueKey) |
| `Transport/UsbDeviceEnumerator.cs` | ~305 | Enumeracion via SetupAPI (P/Invoke), FindByUniqueKey, IsConnected |
| `Transport/UsbTransport.cs` | ~290 | `UsbTransport` (resuelve UniqueKey→DevicePath) + `UsbTransportDirect` (DevicePath directo) |
| `Monitoring/UsbPrinterStatusChecker.cs` | ~125 | Status check: SetupAPI + DLE EOT via UsbTransportDirect (1 sola enumeracion) |

**Archivos modificados:**

| Archivo | Cambio |
|---|---|
| `Data/Models/PrinterEntity.cs` | +4 campos USB (`tipo_conexion`, `usb_unique_key`, `usb_device_path`, `usb_friendly_name`) |
| `Data/PrinterServiceDb.cs` | Migracion automatica de columnas USB + indice UNIQUE en `usb_unique_key` |
| `Monitoring/PrinterStatusChecker.cs` | +1 campo `ResolvedUsbDevicePath` en `PrinterStatus` |
| `Monitoring/StatusMonitor.cs` | Soporte USB en CheckAllPrintersAsync (checker, deteccion cambio puerto, skip SNMP/ARP) |
| `Workers/PrintWorker.cs` | Query BD unificada + ProcessUsbJobAsync + SendUsbWithRetry |
| `Api/Controllers/PrinterController.cs` | DiscoverUsbPrinters + campos USB en register/status |
| `Api/ApiRouter.cs` | Ruta `GET /api/printer/usb/discover` |
| `PrinterServices.csproj` | Referencias a los 4 archivos nuevos |
