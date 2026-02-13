# Vibe Engineering — Acople QuipuNet ↔ PrinterServices

## 1. Objetivo

Migrar **toda la orquestación de impresión** (estrategias, workflows, lógica de decisión) desde el **Front (QuipuNet.exe)** al **Backend (QuipuNetX)**, para que cuando el feature flag `USAR_PRINTER_SERVICE` esté activo y la máquina sea **SERVIDOR**, el backend ejecute las mismas estrategias de impresión que hoy ejecuta el Front, pero delegando la impresión física a **PrinterServices.exe** vía HTTP en lugar de usar `PrintUtil` → `ESCPOSPrinterEthernet` → TCP:9100.

### 1.1 Principio rector: Camino A

**Mover las estrategias al backend**, no interceptar en un punto intermedio. Esto garantiza que:
- El backend orquesta TODO el flujo de impresión (comprobantes, comandas, sorteo, encuesta, motorizado)
- El Front queda "tonto" cuando el flag está activo (solo envía pedido/venta, no imprime nada)
- PrinterServices.exe es solo el ejecutor físico (recibe jobs, imprime, notifica)
- Las estrategias se reutilizan, no se duplican con lógica parcheada

---

## 2. Arquitectura actual (SIN PrinterServices)

```
┌───────────────────────────────────────────────────────────────────────────┐
│                        FRONT (QuipuNet.exe - WPF)                         │
│                                                                           │
│  Presenter.enviarPedidos() / FragmentDetallePago.registrarVenta()         │
│    └→ Iterator → Router ──────────────────────────────┐                   │
│                                                       │ HTTP / directo    │
│                                                       ▼                   │
│                                            ┌──────────────────┐           │
│                                            │  BACKEND (quipu) │           │
│                                            │  PedidoController │          │
│                                            │  VentaController  │          │
│                                            └────────┬─────────┘           │
│                                                     │ Respuesta           │
│                                                     │ .ImpresionList      │
│                                                     │ .Impresion          │
│                                                     │ .ImpresionSorteo    │
│                                                     │ .ImpresionEncuesta  │
│                                                     │ .ImpresionMotorizado│
│                                                     ▼                     │
│  Presenter recibe Respuesta → arma ImpresionContext                       │
│    └→ ImpresionService.Instance.ImprimirAsync(context)                    │
│         └→ ImpresionStrategyFactory.CrearEstrategia(context)              │
│              └→ strategy.EjecutarAsync(context)                           │
│                   ├→ PrintUtil.imprimirVenta()          (comprobante)     │
│                   ├→ PrintUtil.imprimirComandasEthernet() (comandas)      │
│                   ├→ PrintUtil.imprimirPromociones()     (sorteo)         │
│                   ├→ PrintUtil.imprimirEncuesta()        (encuesta)       │
│                   └→ PrintUtil.imprimirPrecuenta()       (motorizado)     │
│                        └→ ProcesarModoEthernet()                          │
│                             └→ ESCPOSPrinterEthernet.imprimir()           │
│                                  └→ conectar() → TCP:9100                 │
└───────────────────────────────────────────────────────────────────────────┘
```

### 2.1 Flujo detallado de imprimirComandasEthernet (PrintUtil.cs:1060)

```csharp
foreach Impresion in impresionComandasList:
    1. Impresora impresoraObj = Impresora.findById(impresion.Impresora_id)  // ← BD local
    2. Setear propiedades desde impresoraObj:
       - Impresora_tamanioqr, Abregaveta, Cashdrawer, Printermodel, Tipoimpresora
    3. switch(impresion.Impresora_modoimpresion):
       - ETHERNET → ProcesarModoEthernet(impresion, impresoraObj, respuesta)
       - SERVICIO → ProcesarModoServicio(impresion, impresoraObj, respuesta)
       - SERIAL   → ProcesarModoSerial(impresion, respuesta)
```

### 2.2 ProcesarModoEthernet (PrintUtil.cs:1178)

```csharp
var printer = getPrinterObjectByModel(printermodel);
printer.init(impresion);
printer.setLetterSize(impresoraObj.Impresora_tamanio);

if (NUEVO_FORMATO_COMANDA_ETHERNET_MEJORADA):
    htmlInput = CadenaHTML ?? Cadena
    Bitmap bmp = HtmlBitmapRenderer.RenderSimpleHtmlAsBitmap(htmlInput)
    Bitmap resized = ResizeIfNeeded(bmp, 576)
    printer.PrintBitmap(resized)              // ← BITMAP PATH
else:
    printer.printString(Cadena)               // ← TEXT PATH

printer.feedAndCut(2)
printer.imprimir() → conectar(commands.ByteArray, 9100, true) → TCP socket
```

---

## 3. Arquitectura nueva (CON PrinterServices) — Camino A

```
┌───────────────────────────────────────────────────────────────────────────┐
│                        FRONT (QuipuNet.exe - WPF)                         │
│                                                                           │
│  Presenter.enviarPedidos() / FragmentDetallePago.registrarVenta()         │
│    └→ Iterator → Router ──────────────────────────────┐                   │
│                                                       │ HTTP / directo    │
│                                                       ▼                   │
│                                            ┌──────────────────────────┐   │
│                                            │     BACKEND (QuipuNetX)  │   │
│                                            │                          │   │
│                                            │  ┌─ FLAG OFF ──────────┐ │   │
│                                            │  │ Flujo actual:       │ │   │
│                                            │  │ retorna Respuesta   │ │   │
│                                            │  │ con ImpresionList   │ │   │
│                                            │  │ al Front            │ │   │
│                                            │  └─────────────────────┘ │   │
│                                            │                          │   │
│                                            │  ┌─ FLAG ON ───────────┐ │   │
│                                            │  │ 1. Genera datos     │ │   │
│                                            │  │ 2. Arma contexto    │ │   │
│                                            │  │ 3. Ejecuta Strategy │ │   │
│                                            │  │    (en backend)     │ │   │
│                                            │  │ 4. Strategy llama   │ │   │
│                                            │  │    PrinterService-  │ │   │
│                                            │  │    Client (HTTP)    │ │   │
│                                            │  │ 5. Respuesta al     │ │   │
│                                            │  │    Front SIN        │ │   │
│                                            │  │    ImpresionList    │ │   │
│                                            │  └────────┬────────────┘ │   │
│                                            └───────────┼──────────────┘   │
│                                                        │                  │
│                                                        ▼ HTTP POST        │
│                                            ┌──────────────────────────┐   │
│                                            │  PrinterServices.exe     │   │
│                                            │  Cola → PrintWorker      │   │
│                                            │  → ESC/POS → TCP:9100    │   │
│                                            │  → gRPC notificaciones   │   │
│                                            └──────────────────────────┘   │
│                                                                           │
│  FRONT (FLAG ON):                                                         │
│    - NO recibe ImpresionList → NO imprime nada localmente                 │
│    - Solo muestra "Pedido registrado" o error                             │
│    - Recibe notificaciones de estado vía gRPC (opcional)                  │
└───────────────────────────────────────────────────────────────────────────┘
```

---

## 4. Estrategias de impresión — Inventario y migración

### 4.1 Todas las IPrintStrategy actuales (en Front)

| # | Estrategia | Archivo Front | Llama a PrintUtil.* | Usa Iterators |
|---|---|---|---|---|
| 1 | **ComandasPrintStrategy** | `Strategies/ComandasPrintStrategy.cs` | `imprimirComandasEthernet()` | No |
| 2 | **VentaRapidaPrintStrategy** | `Strategies/VentaRapidaPrintStrategy.cs` | `imprimirVenta()`, `imprimirComandasEthernet()`, `imprimirPromociones()`, `imprimirEncuesta()`, `imprimirPrecuenta()` | `VentaIterator`, `AjusteConfiguracionIterator` |
| 3 | **VentaSalonPrintStrategy** | `Strategies/VentaSalonPrintStrategy.cs` | Delega a `VentaImpresionWorkflow` | `VentaIterator`, `AjusteConfiguracionIterator` |
| 4 | **DeliveryPrintStrategy** | `Strategies/DeliveryPrintStrategy.cs` | `imprimirVenta()`, `imprimirPromociones()`, `imprimirEncuesta()`, `imprimirPrecuenta()` | `AjusteConfiguracionIterator`, `VentaIterator` |
| 5 | **VentaProveedorPrintStrategy** | `Strategies/VentaProveedorPrintStrategy.cs` | `imprimirVenta()` | `AjusteConfiguracionIterator`, `VentaIterator` |
| 6 | **AnularCorregirPrintStrategy** | `Strategies/AnularCorregirPrintStrategy.cs` | Delega a `VentaImpresionWorkflow` + `ReimprimirAsync()` | Indirecto vía Workflow |

### 4.2 Métodos de PrintUtil afectados

| Método | Estrategias que lo llaman | Tipo |
|---|---|---|
| `imprimirComandasEthernet()` | Comandas, VentaRapida | Comandas |
| `imprimirVenta()` | VentaRapida, VentaSalon, Delivery, VentaProveedor, AnularCorregir | Comprobantes |
| `imprimirPromociones()` | VentaRapida, VentaSalon, Delivery | Sorteo |
| `imprimirEncuesta()` | VentaRapida, VentaSalon, Delivery | Encuesta |
| `imprimirPrecuenta()` | VentaRapida, Delivery | Motorizado |

### 4.3 Workflow compartido

```
VentaSalonPrintStrategy ──┐
                          ├──→ VentaImpresionWorkflow.EjecutarAsync()
AnularCorregirPrintStrategy──┘     └→ PrintUtil.imprimirVenta()
                                   └→ PrintUtil.imprimirPromociones()
                                   └→ PrintUtil.imprimirEncuesta()
```

### 4.4 Qué pasa con cada estrategia en backend (Camino A)

En la versión backend, cada estrategia:
- Donde llamaba `PrintUtil.imprimirX()` → llama `PrinterServiceClient.EnviarX()`
- Donde llamaba `VentaIterator.reImprimir()` → llama `VentaController.reImprimir()` directo
- Donde llamaba `AjusteConfiguracionIterator.traerAjuste()` → llama `Configuracionlocalpos.findById()` directo
- Donde usaba callbacks UI → simplemente retorna resultado (sin UI)
- Donde llamaba `Util.setImpresion()` → no aplica (PrinterServices maneja reintentos)

---

## 5. Mapa de dependencias: Front → Backend

### 5.1 Hallazgo clave: Los Iterators son wrappers triviales

Los Routers en QuipuNetX ya tienen lógica dual:

```csharp
// VentaRouter.reImprimir() — YA EXISTE EN QuipuNetX
if (Util.esModoServidor())
    return VentaController.reImprimir(venta_id, copiaSimple);  // ← DIRECTO
else
    VentaClient.reImprimir(...);  // ← HTTP al servidor (modo cliente)
```

Los Iterators del Front (`VentaIterator`, `AjusteConfiguracionIterator`) son wrappers que llaman a Routers que llaman a Controllers. En el backend, llamamos a los Controllers directamente.

### 5.2 Tabla completa de dependencias

| Componente en Front | Vive en | Equivalente directo en Backend | Estado |
|---|---|---|---|
| `VentaIterator.reImprimir()` | Front → Router → Controller | `VentaController.reImprimir()` | ✅ Ya existe |
| `VentaIterator.ImprimirVentaRapida()` | Front → Router → Controller | `VentaController.reImprimir(id, PARAM_NO)` | ✅ Ya existe |
| `AjusteConfiguracionIterator.traerAjuste()` | Front → Router → Controller | `Configuracionlocalpos.findById()` | ✅ Ya existe |
| `ConfiguracionImpresionAdapter` | Front (llama a `Util.estaConfiguracionActivadaPOS()`) | Mover a QuipuNetX (deps ya están ahí) | 🔄 Mover |
| `ImpresionContext` | Front, data class con entities QuipuNetX | Mover a QuipuNetX | 🔄 Mover |
| `PrintResultDto` + `PrintErrorType` | Front, data class pura | Mover a QuipuNetX | 🔄 Mover |
| `IPrintStrategy` | Front, interfaz | Mover a QuipuNetX | 🔄 Mover |
| `ImpresionStrategyFactory` | Front, factory simple | Mover a QuipuNetX | 🔄 Mover |
| `VentaImpresionWorkflow` | Front, lógica de workflow | Mover a QuipuNetX (adaptar) | 🔄 Mover |
| `PrinterQueueManager` | **QuipuNetX.PrinterManager** | Ya está en backend | ✅ Ya existe |
| `Util.setImpresion()` | **QuipuNetX.util.Util** | Ya está en backend | ✅ Ya existe |
| `RestaurantpeApplication.Instance` | **QuipuNetX.applications** | Ya está en backend | ✅ Ya existe |
| `NLog` | Front | Usar logging de QuipuNetX | 🔄 Adaptar |
| `Application.Current.Dispatcher` (UI) | Front WPF | **NO APLICA** — backend no tiene UI | ❌ Eliminar |
| Popups de confirmación | Front WPF | **NO APLICA** — backend no pide confirmación | ❌ Eliminar |

### 5.3 Conclusión

**El 90% de las dependencias ya están en QuipuNetX**. Solo hay que:
1. Mover 7 clases puras (data classes + interfaz + factory + adapter + workflow)
2. Adaptar 6 estrategias (Iterator→Controller, PrintUtil→PrinterServiceClient, sin UI)
3. Crear 1 clase nueva (PrinterServiceClient)

---

## 6. Feature Flag — Diseño dual (Front + Backend)

### 6.1 ¿Por qué en AMBOS lados?

1. **Backend (QuipuNetX)**: Si `USAR_PRINTER_SERVICE=true`:
   - PedidoController.addLista() → ejecuta estrategia de comandas en backend → envía a PrinterServices → **NO retorna `ImpresionList`**
   - VentaController (al cobrar) → ejecuta estrategia de comprobante en backend → envía a PrinterServices → **NO retorna objetos de impresión**

2. **Front (QuipuNet.exe)**: Si `USAR_PRINTER_SERVICE=true`:
   - `ListaPedidosTemporalesPresenter.enviarPedidos()` → **NO espera `ImpresionList`**, no llama `imprimirComandas()`
   - `FragmentDetallePago` (cobro) → **NO ejecuta `ImpresionService`**, no imprime nada

### 6.2 Mecanismo

Usa `FeatureFlagConfigReader` existente en `QuipuNetX/SQLite/FeatureFlagConfigReader.cs`:

- Archivo: `%LOCALAPPDATA%\QuipuNet\config_fla.cfg`
- Formato: `USAR_PRINTER_SERVICE=true`
- Lectura: `FeatureFlagConfigReader.IsEnabled("USAR_PRINTER_SERVICE")`
- Solo servidor: `Util.esModoServidor() && FeatureFlagConfigReader.IsEnabled("USAR_PRINTER_SERVICE")`

### 6.3 Agregar a flags definidos

```csharp
// En FeatureFlagConfigReader.GetAllDefinedFlags()
{ "USAR_PRINTER_SERVICE", "Delega impresión al servicio PrinterServices.exe (solo servidor)" }
```

---

## 7. Clases a migrar al Backend (QuipuNetX)

### 7.1 Namespace nuevo: `QuipuNetX.Services.Print`

Crear carpeta `QuipuNetX/Services/Print/` con la siguiente estructura:

```
QuipuNetX/Services/Print/
├── ImpresionContext.cs                    ← mover desde Front (sin cambios)
├── PrintResultDto.cs                      ← mover desde Front (sin cambios)
├── ConfiguracionImpresionAdapter.cs       ← mover desde Front (sin cambios, deps ya en QuipuNetX)
├── IPrintStrategy.cs                      ← mover desde Front (sin cambios)
├── ImpresionStrategyFactory.cs            ← mover desde Front (sin cambios)
├── PrinterServiceClient.cs               ← NUEVO: cliente HTTP a PrinterServices
├── ServerImpresionService.cs              ← NUEVO: orquestador en backend
├── Strategies/
│   ├── ServerComandasStrategy.cs          ← adaptación de ComandasPrintStrategy
│   ├── ServerVentaRapidaStrategy.cs       ← adaptación de VentaRapidaPrintStrategy
│   ├── ServerVentaSalonStrategy.cs        ← adaptación de VentaSalonPrintStrategy
│   ├── ServerDeliveryStrategy.cs          ← adaptación de DeliveryPrintStrategy
│   ├── ServerVentaProveedorStrategy.cs    ← adaptación de VentaProveedorPrintStrategy
│   └── ServerAnularCorregirStrategy.cs    ← adaptación de AnularCorregirPrintStrategy
└── Workflows/
    └── ServerVentaImpresionWorkflow.cs    ← adaptación de VentaImpresionWorkflow
```

### 7.2 Clases que se mueven SIN cambios (solo cambio de namespace)

| Clase | Motivo |
|---|---|
| `ImpresionContext` | Data class pura, solo usa entities de QuipuNetX |
| `PrintResultDto` + `PrintErrorType` | Data class pura, sin dependencias externas |
| `IPrintStrategy` | Interfaz con 2 miembros (`NombreEstrategia`, `EjecutarAsync`) |
| `ConfiguracionImpresionAdapter` | Singleton, todas sus llamadas ya están en QuipuNetX |
| `ImpresionStrategyFactory` | Factory switch-case, sin dependencias externas |

### 7.3 Clases que se crean NUEVAS

| Clase | Responsabilidad |
|---|---|
| `PrinterServiceClient` | Singleton HTTP client que envía jobs a PrinterServices |
| `ServerImpresionService` | Orquestador: crea contexto + ejecuta estrategia (equivalente a `ImpresionService` del Front pero sin UI) |

---

## 8. Adaptación detallada de cada estrategia

### 8.1 Patrones de adaptación (aplican a TODAS)

#### Patrón 1: Iterator → Controller directo

```
FRONT:  VentaIterator.reImprimir(ventaId, callback)
          → VentaRouter.reImprimir()
            → VentaController.reImprimir()

BACKEND: Respuesta resp = VentaController.reImprimir(ventaId, copiaSimple)  // DIRECTO
```

#### Patrón 2: AjusteConfiguracionIterator → BD directa

```
FRONT:  AjusteConfiguracionIterator.traerAjuste(CONFIG_ID, callback)
          → ConfiguracionlocalposRouter.getConfiguracionlocalpos()
            → ConfiguracionlocalposController

BACKEND: var config = Configuracionlocalpos.findById(CONFIG_ID)  // DIRECTO
```

#### Patrón 3: PrintUtil → PrinterServiceClient

```
FRONT:  await PrintUtil.imprimirComandasEthernet(impresionList, callback)
          → ProcesarModoEthernet() → ESCPOSPrinterEthernet → TCP:9100

BACKEND: var resultado = await PrinterServiceClient.Instance
             .EnviarComandasAsync(impresionList)  // HTTP POST a PrinterServices
```

#### Patrón 4: Sin UI

```
FRONT:  tcs.TrySetResult(PrintResultDto.CreateError(...))
        Util.setImpresion(impresion)  // guardar para reintento manual UI

BACKEND: return PrintResultDto.CreateError(...)  // retornar directo
         // PrinterServices maneja reintentos automáticos (cola SQLite)
```

### 8.2 ServerComandasStrategy

**Complejidad**: ⭐ Baja — la más simple

| Cambio | De (Front) | A (Backend) |
|---|---|---|
| Imprimir directo | `PrintUtil.imprimirComandasEthernet()` | `PrinterServiceClient.EnviarComandasAsync()` |
| Cola impresión | `PrinterQueueManager.Enqueue()` | `PrinterServiceClient.EnviarComandasAsync()` (PS tiene cola propia) |
| Logging | `NLog` | QuipuNetX logging |

### 8.3 ServerVentaRapidaStrategy

**Complejidad**: ⭐⭐⭐ Alta — la más compleja (5 tipos de impresión)

| Cambio | De (Front) | A (Backend) |
|---|---|---|
| Comprobante | `PrintUtil.imprimirVenta()` | `PrinterServiceClient.EnviarVentaAsync()` |
| Comandas | `PrintUtil.imprimirComandasEthernet()` | `PrinterServiceClient.EnviarComandasAsync()` |
| Sorteo | `PrintUtil.imprimirPromociones()` | `PrinterServiceClient.EnviarPromocionAsync()` |
| Encuesta | `PrintUtil.imprimirEncuesta()` | `PrinterServiceClient.EnviarEncuestaAsync()` |
| Motorizado | `PrintUtil.imprimirPrecuenta()` | `PrinterServiceClient.EnviarPrecuentaAsync()` |
| Delivery reprint | `VentaIterator.ImprimirVentaRapida()` | `VentaController.reImprimir(id, PARAM_NO)` |
| Doble comprobante | `AjusteConfiguracionIterator.traerAjuste()` | `Configuracionlocalpos.findById()` |
| Reimpresión | `VentaIterator.reImprimir()` | `VentaController.reImprimir()` |
| Reintento UI | `Util.setImpresion()` | No aplica |

### 8.4 ServerVentaSalonStrategy

**Complejidad**: ⭐ Baja — delega a ServerVentaImpresionWorkflow

### 8.5 ServerDeliveryStrategy

**Complejidad**: ⭐⭐ Media — similar a VentaRapida pero sin comandas

| Cambio | De (Front) | A (Backend) |
|---|---|---|
| Comprobante | `PrintUtil.imprimirVenta()` | `PrinterServiceClient.EnviarVentaAsync()` |
| Sorteo | `PrintUtil.imprimirPromociones()` | `PrinterServiceClient.EnviarPromocionAsync()` |
| Encuesta | `PrintUtil.imprimirEncuesta()` | `PrinterServiceClient.EnviarEncuestaAsync()` |
| Motorizado | `PrintUtil.imprimirPrecuenta()` | `PrinterServiceClient.EnviarPrecuentaAsync()` |
| Doble comprobante | `AjusteConfiguracionIterator` | `Configuracionlocalpos.findById()` |
| Reimpresión | `VentaIterator.reImprimir()` | `VentaController.reImprimir()` |

### 8.6 ServerVentaProveedorStrategy

**Complejidad**: ⭐⭐ Media — solo comprobante con CUFE/UUID

| Cambio | De (Front) | A (Backend) |
|---|---|---|
| Comprobante | `PrintUtil.imprimirVenta()` | `PrinterServiceClient.EnviarVentaAsync()` |
| Doble comprobante | `AjusteConfiguracionIterator` | `Configuracionlocalpos.findById()` |
| Reimpresión | `VentaIterator.reImprimir()` | `VentaController.reImprimir()` |

### 8.7 ServerAnularCorregirStrategy

**Complejidad**: ⭐ Baja — delega a ServerVentaImpresionWorkflow + reimpresión

### 8.8 ServerVentaImpresionWorkflow

**Complejidad**: ⭐⭐ Media — workflow compartido por Salón y AnularCorregir

| Cambio | De (Front) | A (Backend) |
|---|---|---|
| Comprobante | `PrintUtil.imprimirVenta()` | `PrinterServiceClient.EnviarVentaAsync()` |
| Sorteo | `PrintUtil.imprimirPromociones()` | `PrinterServiceClient.EnviarPromocionAsync()` |
| Encuesta | `PrintUtil.imprimirEncuesta()` | `PrinterServiceClient.EnviarEncuestaAsync()` |
| Doble comprobante | `AjusteConfiguracionIterator` | `Configuracionlocalpos.findById()` |
| Reimpresión | `VentaIterator.reImprimir()` | `VentaController.reImprimir()` |
| TaskCompletionSource | callbacks async | llamadas directas con await |

---

## 9. PrinterServiceClient — Cliente HTTP (NUEVO en QuipuNetX)

### 9.1 Ubicación

`QuipuNetX/Services/Print/PrinterServiceClient.cs`

### 9.2 Rol simplificado

El **único rol** de `PrinterServiceClient` es **enviar** las `Impresion` a PrinterServices.exe vía HTTP.
**NO** orquesta impresión, **NO** maneja reintentos, **NO** espera a que se imprima.

PrinterServices.exe **solo responde** si logró registrar el Job en su base de datos SQLite, devolviendo los `job_id` asociados a los `pedido_ids`.

### 9.3 Responsabilidades concretas

- Singleton
- HTTP POST a PrinterServices (varios endpoints)
- Descubre IP via `config_fla.cfg` o UDP Discovery o localhost:8090
- Enriquece cada `Impresion` con datos de `Impresora` (BD local) antes de enviar
- Serializa `IList<Impresion>` → JSON compatible con `PrintController.ParsePrintJob()`
- Envía y **recibe respuesta con `job_id ↔ pedido_ids`** (ver §9.6)
- Timeout: 10s
- Si PrinterServices no responde → retorna `Respuesta` tipo ERROR

### 9.4 Métodos públicos

| Método | Endpoint PrinterServices | Equivale a PrintUtil.* |
|---|---|---|
| `EnviarComandasAsync(IList<Impresion>)` | `POST /api/print/comandas` | `imprimirComandasEthernet()` |
| `EnviarVentaAsync(Impresion)` | `POST /api/print/venta` | `imprimirVenta()` |
| `EnviarPromocionAsync(Impresion)` | `POST /api/print/comanda` | `imprimirPromociones()` |
| `EnviarEncuestaAsync(Impresion)` | `POST /api/print/comanda` | `imprimirEncuesta()` |
| `EnviarPrecuentaAsync(Impresion)` | `POST /api/print/precuenta` | `imprimirPrecuenta()` |
| `IsAvailableAsync()` | `GET /api/health` | Health check |

### 9.5 Enriquecimiento de Impresion

Antes de serializar cada `Impresion`, enriquecer con datos de `Impresora`:

```csharp
private void EnriquecerImpresion(Impresion imp)
{
    Impresora obj = Impresora.findById(imp.Impresora_id);
    if (obj == null) return;
    imp.Impresora_tamanioqr    = obj.Impresora_tamanioqr;
    imp.Abregaveta             = obj.Impresora_abregaveta;
    imp.Cashdrawer             = obj.Impresora_cashdrawer;
    imp.Printermodel           = obj.Impresora_printermodel;
    imp.Tipoimpresora          = obj.Impresora_tipo;
    imp.Impresora_tamanioletra = obj.Impresora_tamanio;
}
```

### 9.6 Contrato de respuesta: job_id ↔ pedido_ids

#### Nueva propiedad en Impresion.cs (QuipuNetX)

```csharp
// Impresion.cs — NUEVA propiedad (en implementación)
public string Pedidos_ids_printer_asociados { get; set; }
// Formato: "1772,1773,1774,1780" — IDs de pedidos que viajan en esta Impresión
```

Cada objeto `Impresion` contiene N pedidos internamente (una comanda puede tener varios pedidos). Al enviar a PrinterServices, **1 Impresion = 1 Job**.

#### Ejemplo concreto

```
QuipuNetX envía 2 objetos Impresion a PrinterServices:

  Impresion1 (impresora Epson cocina):
    Pedidos_ids_printer_asociados = "1772,1773"
    → PrinterServices crea Job en SQLite → job_id = 714

  Impresion2 (impresora Epson bar):
    Pedidos_ids_printer_asociados = "1771,1774"
    → PrinterServices crea Job en SQLite → job_id = 715
```

#### Respuesta HTTP de PrinterServices

PrinterServices **solo confirma** que registró los Jobs en SQLite. NO espera a que se impriman:

```json
// POST /api/print/comandas → Response 200
{
  "status": "OK",
  "jobs": [
    { "job_id": "714", "pedido_ids": ["1772", "1773"] },
    { "job_id": "715", "pedido_ids": ["1771", "1774"] }
  ]
}
```

#### Mapeo HTTP Response → Respuesta.cs (QuipuNetX)

`PrinterServiceClient` parsea el JSON de PrinterServices y construye un objeto `Respuesta`
usando sus propiedades reales (ver `QuipuNetX/Util/Respuesta.cs`):

##### Caso ÉXITO (HTTP 200 + status "OK"):

```csharp
// PrinterServiceClient — ver implementación completa en §13.2:
var respuesta = new Respuesta();
respuesta.Tipo        = Util.SUCCESS;
respuesta.Mensajes    = new List<string> { "Imprimiendo " + tipoImpresion + "..." };  // ver §13.3
respuesta.Data        = jobsParsed;  // List<PrintJobResult> con {JobId, PedidoIds[]}
respuesta.ImpresionList = stickersParaFront;  // solo stickers (ver §14), vacío si no hay
// NOTA: SUCCESS = "se ha empezado a imprimir", NO "ya se imprimió" (ver §13.4)
```

##### Caso ERROR (HTTP 500, timeout, o status "ERROR"):

```csharp
var respuesta = new Respuesta();
respuesta.Tipo        = Util.ERROR;
respuesta.CodigoError = "PRINTER_SERVICE_UNAVAILABLE";  // o "PRINTER_SERVICE_REGISTER_FAILED"
respuesta.Mensajes    = new List<string> { "No se pudo registrar la impresión en PrinterServices" };
respuesta.Data        = null;
respuesta.ImpresionList = null;  // sin impresiones para el Front
```

##### Propiedades de Respuesta.cs relevantes:

| Propiedad Respuesta.cs | Tipo | Uso en este contexto |
|---|---|---|
| `Tipo` | `string` | `Util.SUCCESS` o `Util.ERROR` |
| `CodigoError` | `string` | `"PRINTER_SERVICE_UNAVAILABLE"` / `"PRINTER_SERVICE_REGISTER_FAILED"` |
| `Mensajes` | `IList<string>` | Mensajes informativos o de error |
| `Data` | `object` | `List<PrintJobResult>` con `{JobId, PedidoIds[]}` |
| `ImpresionList` | `IList<Impresion>` | Solo stickers Jaltech para flujo local (ver §14) |
| `ImpresionErrorList` | `IList<Impresion>` | Impresiones que no se pudieron registrar |
| `PedidoList` | `IList<Pedido>` | No se usa en este flujo |
| `Flag` | `bool` | No se usa en este flujo |

##### DTO auxiliar nuevo: PrintJobResult

```csharp
// Clase nueva en QuipuNetX.Services.Print
public class PrintJobResult
{
    public string JobId { get; set; }
    public List<string> PedidoIds { get; set; }
}
```

#### Qué hace QuipuNetX con la Respuesta exitosa

```csharp
// En ServerComandasStrategy, después de recibir Respuesta SUCCESS:
var jobs = respuesta.Data as List<PrintJobResult>;
foreach (var job in jobs)
{
    foreach (var pedidoId in job.PedidoIds)
    {
        PedidoPrintJobTracker.Asociar(pedidoId, job.JobId);
    }
}
// Retorna la Respuesta al caller (PedidoController → Front)
```

#### Si PrinterServices NO puede registrar el Job

```json
// Response 500 o timeout
{
  "status": "ERROR",
  "message": "No se pudo registrar el job en la base de datos"
}
```

PrinterServiceClient construye `Respuesta` con `Tipo = Util.ERROR` y propaga al Front.

### 9.7 Mapeo Impresion → JSON

| Propiedad Impresion (QuipuNetX) | Campo JSON (PrinterServices) |
|---|---|
| `Impresora_id` | `impresora_id` |
| `Impresora_ip` | `impresora_ip` |
| `Impresora` (nombre) | `impresora_nombre` |
| `Printermodel` | `printermodel` |
| `Impresora_modoimpresion` | `impresora_modoimpresion` |
| `Cadena` | `cadena` |
| `cadenaHTML` | `cadenaHTML` |
| `Uniqueid` | `uniqueid` |
| `Tipo` | `tipo` |
| `Abregaveta` | `abregaveta` |
| `Cashdrawer` | `cashdrawer` |
| `Tipoimpresora` | `tipoimpresora` |
| `Impresora_tamanioletra` | `impresora_tamanioletra` |
| `Impresora_tamanioqr` | `impresora_tamanioqr` |
| `Areaproduccion_numerocopias` | `areaproduccion_numerocopias` |
| `lineasimprimir` | `lineasimprimir` |
| `impresora_tipogeneracion` | `impresora_tipogeneracion` |
| `Codigocorte` | `codigocorte` |
| `qrData` | `qrData` |
| `qrEncuesta` | `qrEncuesta` |
| `Pedidos_ids_printer_asociados` | `pedido_ids` |

### 9.8 Descubrimiento de IP

```
Prioridad:
1. config_fla.cfg → PRINTER_SERVICE_URL=http://192.168.1.10:8090
2. UDP Discovery (QUIPU_PRINTER_DISCOVERY → respuesta con IP)
3. Fallback: http://localhost:8090
```

---

## 10. Trazabilidad: job_id ↔ pedidos (notificación de fallos)

### 10.1 Flujo completo de trazabilidad

```
┌─────────────────────────────────────────────────────────────────────┐
│ 1. QuipuNetX envía Impresion[] a PrinterServices                    │
│    Cada Impresion tiene Pedidos_ids_printer_asociados = "1772,1773" │
│                                                                     │
│ 2. PrinterServices registra Jobs en SQLite                          │
│    Job 714 → pedido_ids: [1772, 1773]                               │
│    Job 715 → pedido_ids: [1771, 1774]                               │
│                                                                     │
│ 3. PrinterServices responde con job_id ↔ pedido_ids                 │
│    QuipuNetX almacena: pedido 1772 → job_id 714                     │
│                        pedido 1773 → job_id 714                     │
│                        pedido 1771 → job_id 715                     │
│                        pedido 1774 → job_id 715                     │
│                                                                     │
│ 4. PrinterServices IMPRIME (asíncrono, en su cola)                  │
│    Job 714 → ✅ SUCCESS → gRPC: NotifyPrintSuccess(job_id=714)      │
│    Job 715 → ❌ FAILED  → gRPC: NotifyPrintFailed(job_id=715)       │
│                                                                     │
│ 5. QuipuNet.exe recibe gRPC NotifyPrintFailed(job_id=715)           │
│    → Busca pedidos asociados a job 715: [1771, 1774]                │
│    → Marca pedidos 1771 y 1774 como "impresión fallida"             │
│    → Muestra al usuario qué pedidos fallaron                        │
└─────────────────────────────────────────────────────────────────────┘
```

### 10.2 Cambios necesarios en PrinterServices

#### PrintJob.cs — Nuevo campo

```csharp
public string PedidoIds { get; set; }  // "1772,1773" — viene del Impresion
```

#### PrintJobEntity.cs — Nueva columna

```csharp
public string pedido_ids { get; set; }  // TEXT en SQLite
```

#### PrintController.ParsePrintJob() — Leer pedido_ids

```csharp
job.PedidoIds = GetString(json, "pedido_ids");
```

#### Respuesta del endpoint /api/print/comandas

Actualmente `PrintController` solo responde "OK". Debe cambiar para retornar los `job_id` generados:

```csharp
// Después de Enqueue() cada job:
var jobResults = new List<object>();
foreach (var job in jobs)
{
    _jobManager.Enqueue(job);
    jobResults.Add(new {
        job_id = job.Id.ToString(),
        pedido_ids = (job.PedidoIds ?? "").Split(',')
    });
}
return JsonResponse(200, new { status = "OK", jobs = jobResults });
```

#### gRPC NotifyPrintFailed — Incluir job_id y pedido_ids

En `PrinterNotificationServiceImpl`, cuando se notifica un fallo, incluir:
- `job_id` (ya existe en la notificación)
- `pedido_ids` (nuevo: leer del Job para que QuipuNet sepa qué pedidos fallaron)

### 10.3 Cambios necesarios en QuipuNetX

#### Almacenar job_id por pedido

Después de recibir la respuesta de PrinterServices:

```csharp
// Pseudocódigo en ServerComandasStrategy o PrinterServiceClient:
foreach (var jobResult in response.Jobs)
{
    foreach (var pedidoId in jobResult.PedidoIds)
    {
        // Guardar relación: pedido_id → job_id
        // Para que cuando llegue gRPC NotifyPrintFailed(job_id)
        // podamos saber qué pedidos fallaron
        PedidoPrintJobTracker.Asociar(pedidoId, jobResult.JobId);
    }
}
```

#### Recibir notificación gRPC de fallo

```csharp
// En el listener gRPC del Front (QuipuNet.exe):
void OnPrintFailed(string jobId, string pedidoIds)
{
    // Marcar pedidos como "impresión fallida"
    foreach (var pedidoId in pedidoIds.Split(','))
    {
        MarcarPedidoComoFalloImpresion(pedidoId);
    }
}
```

### 10.4 Importante: PrinterServices NO espera a imprimir

La respuesta de `POST /api/print/comandas` se envía **inmediatamente después de registrar los Jobs en SQLite**. La impresión física ocurre **después**, en el `PrintWorker` (cola asíncrona). El resultado de la impresión llega vía **gRPC push** (NotifyPrintSuccess / NotifyPrintFailed).

---

## 11. Renderizado HTML → Bitmap (en PrinterServices)

### 11.1 Problema

`PrintUtil.ProcesarModoEthernet()` líneas 1206-1211 renderiza HTML a bitmap en QuipuNetX:

```csharp
string htmlInput = preimpresion.CadenaHTML ?? preimpresion.Cadena;
using (Bitmap bmp = HtmlBitmapRenderer.RenderSimpleHtmlAsBitmap(htmlInput))
using (Bitmap resized = ResizeIfNeeded(bmp, 576))
    printer.PrintBitmap(resized);
```

### 11.2 Solución: Renderizar DENTRO de PrinterServices

El renderizado HTML → Bitmap se hace **dentro de PrinterServices**, NO en el backend QuipuNetX.
El backend solo envía `cadenaHTML` como texto plano; PrinterServices renderiza localmente.

**Ventajas**:
- No se transmite base64 pesado en el JSON HTTP
- PrinterServices tiene control total del renderizado
- El backend se mantiene liviano (solo serializa texto)

### 11.3 Análisis de dependencias de HtmlBitmapRenderer.cs

La clase `HtmlBitmapRenderer` (QuipuNetX/Util/print/HtmlBitmapRenderer.cs) es **100% auto-contenida**:

| Dependencia | Tipo | ¿Disponible en PrinterServices? |
|---|---|---|
| `System` | .NET Framework | ✅ Sí |
| `System.Collections.Generic` | .NET Framework | ✅ Sí |
| `System.Drawing` | GDI+ (.NET Framework) | ✅ Sí |
| `System.Text.RegularExpressions` | .NET Framework | ✅ Sí |
| QuipuNetX.* | — | ❌ **NO se usa** |
| Variables globales | — | ❌ **NO se usa** |
| Util.* | — | ❌ **NO se usa** |

**Resultado: Se puede copiar directamente sin modificaciones.**

### 11.4 Archivos a crear en PrinterServices

#### `Rendering/HtmlBitmapRenderer.cs`

Copia exacta de `QuipuNetX/Util/print/HtmlBitmapRenderer.cs` cambiando solo el namespace:

```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.RegularExpressions;

namespace PrinterServices.Rendering
{
    public static class HtmlBitmapRenderer
    {
        private class HtmlRun
        {
            public string Text;
            public int FontSize;
            public FontStyle Style;
            public HtmlRun(string text, int fontSize, FontStyle style)
            { Text = text; FontSize = fontSize; Style = style; }
        }

        // --- Método principal: RenderSimpleHtmlAsBitmap() ---
        // Copia EXACTA del método en QuipuNetX (465 líneas)
        // Parsea HTML simple (<h1>-<h5>, <b>, <i>, <br>)
        // Renderiza con GDI+ (System.Drawing) a 192 DPI
        // Retorna Bitmap con ancho máximo 584px

        // --- ParseLine() ---
        // Parsea tags HTML con pilas de fontSizeStack y styleStack

        // --- SplitRunToFitWidth() ---
        // Corta runs largos en múltiples líneas si exceden maxWidthPx
    }
}
```

> **NOTA**: Se copia la clase completa (465 líneas). Solo se cambia `namespace QuipuNetX.util.print` → `namespace PrinterServices.Rendering`.

#### `Rendering/BitmapResizer.cs`

Copia del método `ResizeIfNeeded` de `PrintUtil.cs`:

```csharp
using System.Drawing;

namespace PrinterServices.Rendering
{
    public static class BitmapResizer
    {
        public static Bitmap ResizeIfNeeded(Bitmap bmp, int maxWidth)
        {
            if (bmp.Width <= maxWidth)
                return bmp;

            float scale = (float)maxWidth / bmp.Width;
            int newHeight = (int)(bmp.Height * scale);
            Bitmap resized = new Bitmap(maxWidth, newHeight);
            resized.SetResolution(192, 192);

            using (Graphics g = Graphics.FromImage(resized))
            {
                g.Clear(Color.White);
                g.DrawImage(bmp, 0, 0, maxWidth, newHeight);
            }

            return resized;
        }
    }
}
```

### 11.5 Integración en PrintWorker

`PrintWorker` usa `HtmlBitmapRenderer` cuando el Job tiene `cadenaHTML`:

```csharp
// PrintWorker.ProcessJob():
if (!string.IsNullOrEmpty(job.ContenidoHtml))
{
    // Renderizar HTML → Bitmap localmente en PrinterServices
    using (Bitmap bmp = HtmlBitmapRenderer.RenderSimpleHtmlAsBitmap(job.ContenidoHtml))
    using (Bitmap resized = BitmapResizer.ResizeIfNeeded(bmp, 576))
    {
        builder.AddBitmapFromImage(resized);  // ESC/POS GS v 0
    }
}
else
{
    builder.AddText(job.Contenido);
}

builder.FeedAndCut(2);
transport.Send(builder.Build());
```

### 11.6 Consecuencia: NO se envía BitmapBase64 en el JSON

El campo `bitmap_base64` **NO existe** en el JSON que viaja de QuipuNetX → PrinterServices.
Solo viaja `cadenaHTML` (texto plano). PrinterServices renderiza localmente.

---

## 12. Cambios en el Front (QuipuNet.exe)

### 12.1 Cambios mínimos — el Front solo deja de imprimir

Con Camino A, el Front **no necesita nuevas estrategias ni lógica**. Solo necesita:

#### ListaPedidosTemporalesPresenter.enviarPedidos() — Línea ~50

```
PSEUDOCÓDIGO:
━━━━━━━━━━━━
// Callback de PedidoIterator.enviarPedidos():
if (respuesta.Tipo == SUCCESS):
    // ... código existente mesa, cache, etc. ...

    if (USAR_PRINTER_SERVICE && ES_MODO_SERVIDOR):
        // Backend ya ejecutó estrategia y envió a PrinterServices
        // Respuesta NO tiene ImpresionList
        view.showSuccessRegister()
    else:
        // Flujo actual: imprimir localmente
        if (respuesta.ImpresionList != null && respuesta.ImpresionList.Count > 0):
            imprimirComandas(respuesta.ImpresionList)
        view.showSuccessRegister()
```

### 12.2 Otros presenters que reciben ImpresionList

Aplicar la misma lógica de feature flag en TODOS los presenters que reciben `ImpresionList`:

| Presenter | Método | Línea ref |
|---|---|---|
| `ListaPedidosTemporalesPresenter` | `enviarPedidos()` | ~50 |
| `ListaPedidosTemporalesPresenter` | `enviarPedidosOld()` | ~181 |
| `ListaPedidosTemporalesPresenter` | `enviarPedidosSelfService()` | ~232 |
| Otros presenters que llamen `imprimirComandas()` | Buscar con grep | — |

### 12.3 Presenters de cobro (ventas)

Para comprobantes, el feature flag también debe interceptar en los presenters de cobro:

| Presenter / Fragment | Método | Qué hace hoy |
|---|---|---|
| `FragmentDetallePago` | `registrarVenta()` callback | Recibe `Respuesta` → arma `ImpresionContext` → `ImpresionService.ImprimirAsync()` |
| Otros fragments de cobro | Similar | Similar |

Con FLAG ON: el callback **NO ejecuta `ImpresionService`** porque el backend ya envió todo a PrinterServices.

### 12.4 El Front mantiene sus estrategias (FLAG OFF)

Las estrategias existentes en el Front **NO se eliminan**. Siguen funcionando cuando:
- FLAG OFF → flujo actual completo
- Máquina CLIENTE → siempre flujo actual (PrinterServices solo corre en servidor)

---

## 13. Manejo de errores y reintentos

### 13.1 Principio: CERO excepciones no controladas

**Regla**: `PrinterServiceClient` **NUNCA lanza excepciones**. Todos sus métodos públicos
retornan `Respuesta`. El caller simplemente evalúa `respuesta.Tipo` — sin try/catch.

Esto evita:
- Caídas de software por excepciones no controladas
- Try/catch obligatorio en cada Strategy que llame al cliente
- Código inmanejable con catches anidados

### 13.2 Método interno: PostConReintentoAsync (retorna Respuesta, no HttpResponseMessage)

```csharp
// PrinterServiceClient — método interno que NUNCA lanza excepciones:
private const int MAX_INTENTOS = 2;
private const int TIMEOUT_MS = 10000;       // 10 segundos por intento
private const int DELAY_ENTRE_REINTENTOS = 1000;  // 1 segundo entre reintentos

private async Task<Respuesta> PostConReintentoAsync(string url, StringContent jsonContent,
                                                     string tipoImpresion)
{
    string ultimoError = "";

    for (int intento = 1; intento <= MAX_INTENTOS; intento++)
    {
        try
        {
            var cts = new CancellationTokenSource(TIMEOUT_MS);
            var httpResponse = await _httpClient.PostAsync(url, jsonContent, cts.Token);

            if (httpResponse.IsSuccessStatusCode)
            {
                // ✅ PrinterServices registró los jobs
                var json = await httpResponse.Content.ReadAsStringAsync();
                var jobsParsed = ParseJobResults(json);

                var respuesta = new Respuesta();
                respuesta.Tipo     = Util.SUCCESS;
                respuesta.Mensajes = new List<string> { "Imprimiendo " + tipoImpresion + "..." };
                respuesta.Data     = jobsParsed;
                return respuesta;
            }
            else
            {
                // HTTP 500, 400, etc. — no reintentar (error de lógica, no de red)
                var respuesta = new Respuesta();
                respuesta.Tipo        = Util.ERROR;
                respuesta.CodigoError = "PRINTER_SERVICE_REGISTER_FAILED";
                respuesta.Mensajes    = new List<string>
                {
                    "No se pudo registrar la impresión de " + tipoImpresion
                    + " (HTTP " + (int)httpResponse.StatusCode + ")"
                };
                return respuesta;
            }
        }
        catch (Exception ex)
        {
            // Cualquier excepción de red/timeout → capturar y reintentar
            ultimoError = ex.Message;

            if (intento < MAX_INTENTOS)
            {
                await Task.Delay(DELAY_ENTRE_REINTENTOS);
                continue;  // Reintentar
            }
            // Último intento falló → NO lanzar, retornar Respuesta ERROR
        }
    }

    // Agotados los reintentos — retornar ERROR (NUNCA throw)
    var respuestaFinal = new Respuesta();
    respuestaFinal.Tipo        = Util.ERROR;
    respuestaFinal.CodigoError = "PRINTER_SERVICE_UNAVAILABLE";
    respuestaFinal.Mensajes    = new List<string>
    {
        "No hay servicio de impresión activo (PrinterServices no responde después de "
        + MAX_INTENTOS + " intentos: " + ultimoError + ")"
    };
    return respuestaFinal;
}
```

### 13.3 Método público: EnviarComandasAsync — SIN try/catch

El caller **no necesita try/catch** porque `PostConReintentoAsync` siempre retorna `Respuesta`:

```csharp
public async Task<Respuesta> EnviarComandasAsync(IList<Impresion> impresionList,
                                                  string tipoImpresion = "comandas")
{
    // 1. Enriquecer impresiones con datos de Impresora
    foreach (var imp in impresionList)
        EnriquecerImpresion(imp);

    // 2. Serializar a JSON
    var jsonContent = SerializarImpresiones(impresionList);

    // 3. Enviar con reintento — SIEMPRE retorna Respuesta, NUNCA lanza
    var respuesta = await PostConReintentoAsync(
        _baseUrl + "/api/print/comandas",
        jsonContent,
        tipoImpresion
    );

    // 4. Si hay stickers, agregarlos al ImpresionList de la respuesta
    if (respuesta.Tipo == Util.SUCCESS)
        respuesta.ImpresionList = stickersParaFront;  // solo stickers (ver §14)

    return respuesta;
}
```

### 13.4 Uso desde las Strategies — limpio, sin try/catch

```csharp
// En ServerComandasStrategy.EjecutarAsync():
var respuesta = await PrinterServiceClient.Instance
    .EnviarComandasAsync(context.ImpresionComandas, "comandas");

// Sin try/catch — evaluar directo:
if (respuesta.Tipo == Util.SUCCESS)
{
    var jobs = respuesta.Data as List<PrintJobResult>;
    foreach (var job in jobs)
        foreach (var pedidoId in job.PedidoIds)
            PedidoPrintJobTracker.Asociar(pedidoId, job.JobId);
}

return respuesta;  // Propagar al caller (SUCCESS o ERROR, nunca excepción)
```

### 13.5 Mensajes dinámicos por Strategy

Cada Strategy invoca al cliente con su `tipoImpresion`:

| Strategy | tipoImpresion | Mensaje Respuesta.Mensajes |
|---|---|---|
| `ServerComandasStrategy` | `"comandas"` | `"Imprimiendo comandas..."` |
| `ServerVentaRapidaStrategy` | `"comprobante de venta"` | `"Imprimiendo comprobante de venta..."` |
| `ServerVentaSalonStrategy` | `"comprobante de salón"` | `"Imprimiendo comprobante de salón..."` |
| `ServerDeliveryStrategy` | `"comprobante de delivery"` | `"Imprimiendo comprobante de delivery..."` |
| `ServerVentaProveedorStrategy` | `"factura electrónica"` | `"Imprimiendo factura electrónica..."` |
| `ServerAnularCorregirStrategy` | `"anulación/corrección"` | `"Imprimiendo anulación/corrección..."` |
| (sorteo) | `"sorteo"` | `"Imprimiendo sorteo..."` |
| (encuesta) | `"encuesta"` | `"Imprimiendo encuesta..."` |
| (precuenta) | `"precuenta motorizado"` | `"Imprimiendo precuenta motorizado..."` |

### 13.6 Propagación al Front — Semántica: "se ha empezado a imprimir"

**IMPORTANTE**: La `Respuesta` con `Tipo = SUCCESS` que llega al Front **NO significa que ya se imprimió**. Significa que **PrinterServices recibió y registró los jobs** — la impresión física es asíncrona.

##### Caso ÉXITO → "Se ha empezado a imprimir"

```
Respuesta al Front:

  Tipo           = "SUCCESS"                            // Respuesta.Tipo
  Mensajes       = ["Imprimiendo comandas..."]          // Respuesta.Mensajes
  Data           = List<PrintJobResult>                 // Respuesta.Data (job_id ↔ pedido_ids)
  ImpresionList  = [stickers] o vacío                   // Respuesta.ImpresionList
  Flag           = false                                // Respuesta.Flag (default)

  → El Front sabe que la impresión EMPEZÓ (jobs registrados en cola)
  → El resultado FINAL llega por gRPC: NotifyPrintSuccess / NotifyPrintFailed
```

##### Caso ERROR → "No se pudo iniciar impresión"

```
Respuesta al Front:

  Tipo           = "ERROR"                              // Respuesta.Tipo
  CodigoError    = "PRINTER_SERVICE_UNAVAILABLE"        // Respuesta.CodigoError
  Mensajes       = ["No hay servicio de impresión activo (PrinterServices no responde
                     después de 2 intentos)"]           // Respuesta.Mensajes
  Data           = null                                 // Respuesta.Data
  ImpresionList  = null                                 // Respuesta.ImpresionList

  → El Front muestra el error al usuario
```

### 13.5 NO hay fallback automático

Si PrinterServices está caído, **NO se imprime localmente como fallback**. Razones:
- Simplicidad: evita flujos mixtos difíciles de depurar
- Consistencia: el operador debe saber que PrinterServices no está corriendo
- PrinterServices se instala como Windows Service con arranque automático

---

## 14. Exclusiones — ImprimirStickerJaltech

`ImprimirStickerJaltech()` (PrintUtil.cs) **se excluye de esta migración**:

```csharp
if (preimpresion.Tipoimpresora == Definitions.TIPOIMPRESORA_STICKER)
{
    var resultado = ImprimirStickerJaltech(impresoraObj, preimpresion);
}
```

Los stickers Jaltech usan un flujo diferente con driver propio. En el backend, antes de enviar a PrinterServices, se filtran:

```
foreach Impresion imp in impresionList:
    if (imp.Tipoimpresora == TIPOIMPRESORA_STICKER):
        stickersParaFront.Add(imp)    // incluir en Respuesta.ImpresionList
    else:
        comandasParaServicio.Add(imp)  // enviar a PrinterServices
```

Si hay stickers, la Respuesta INCLUYE un `ImpresionList` parcial (solo stickers) para que el Front los imprima localmente.

---

## 15. Campos NUEVOS en PrinterServices

### 15.1 PrintJob.cs — Campos adicionales

| Campo | Tipo | Descripción |
|---|---|---|
| `ImpresoraTamanio` | `string` | setLetterSize("1","2","3") |
| `ImpresoraTamanioQr` | `string` | Tamaño QR |
| `CashDrawerCode` | `string` | Código de cash drawer |
| `PedidoIds` | `string` | IDs de pedidos asociados "1772,1773" (ver §10) |

> **NOTA**: `BitmapBase64` NO es campo de PrintJob. PrinterServices renderiza el bitmap localmente
> a partir de `ContenidoHtml` usando `HtmlBitmapRenderer` (ver §11).

### 15.2 PrintWorker — Lógica de renderizado (actualizada con §11)

```
PSEUDOCÓDIGO — ProcessJob():
━━━━━━━━━━━━━━━━━━━━━━━━━━━

if (job.ContenidoHtml != null):
    // Renderizado HTML → Bitmap LOCAL en PrinterServices (ver §11.5)
    Bitmap bmp = HtmlBitmapRenderer.RenderSimpleHtmlAsBitmap(job.ContenidoHtml)
    Bitmap resized = BitmapResizer.ResizeIfNeeded(bmp, 576)
    builder.AddBitmapFromImage(resized)
    bmp.Dispose(); resized.Dispose()
else:
    builder.AddText(job.Contenido)

builder.FeedAndCut(2)
transport.Send(builder.Build())
```

---

## 16. Archivos a crear/modificar — Resumen completo

### 16.1 Backend (QuipuNetX) — Archivos NUEVOS

| Archivo | Descripción |
|---|---|
| `Services/Print/ImpresionContext.cs` | Mover desde Front |
| `Services/Print/PrintResultDto.cs` | Mover desde Front |
| `Services/Print/ConfiguracionImpresionAdapter.cs` | Mover desde Front |
| `Services/Print/IPrintStrategy.cs` | Mover desde Front |
| `Services/Print/ImpresionStrategyFactory.cs` | Mover desde Front (adaptar para Server*) |
| `Services/Print/PrinterServiceClient.cs` | NUEVO: cliente HTTP a PrinterServices |
| `Services/Print/PrintJobResult.cs` | NUEVO: DTO `{JobId, PedidoIds[]}` para respuesta de PrinterServices |
| `Services/Print/ServerImpresionService.cs` | NUEVO: orquestador backend |
| `Services/Print/Strategies/ServerComandasStrategy.cs` | Adaptación |
| `Services/Print/Strategies/ServerVentaRapidaStrategy.cs` | Adaptación |
| `Services/Print/Strategies/ServerVentaSalonStrategy.cs` | Adaptación |
| `Services/Print/Strategies/ServerDeliveryStrategy.cs` | Adaptación |
| `Services/Print/Strategies/ServerVentaProveedorStrategy.cs` | Adaptación |
| `Services/Print/Strategies/ServerAnularCorregirStrategy.cs` | Adaptación |
| `Services/Print/Workflows/ServerVentaImpresionWorkflow.cs` | Adaptación |

### 16.2 Backend (QuipuNetX) — Archivos MODIFICADOS

| Archivo | Cambio |
|---|---|
| `controller/PedidoController.cs` | En `addLista()`: if FLAG → ejecutar ServerComandasStrategy |
| `controller/VentaController.cs` | En cobro: if FLAG → ejecutar Server*Strategy |
| `SQLite/FeatureFlagConfigReader.cs` | Agregar `USAR_PRINTER_SERVICE` a flags definidos |

### 16.3 Front (QuipuNet.exe) — Archivos MODIFICADOS

| Archivo | Cambio |
|---|---|
| `ListaPedidosTemporalesPresenter.cs` | `enviarPedidos()`: if FLAG → no esperar ImpresionList |
| `FragmentDetallePago` (y similares) | Si FLAG → no ejecutar ImpresionService |

### 16.4 PrinterServices — Archivos NUEVOS

| Archivo | Descripción |
|---|---|
| `Rendering/HtmlBitmapRenderer.cs` | Copia de QuipuNetX (465 líneas, solo cambio namespace) — ver §11.4 |
| `Rendering/BitmapResizer.cs` | Método `ResizeIfNeeded` — ver §11.4 |

### 16.5 PrinterServices — Archivos MODIFICADOS

| Archivo | Cambio |
|---|---|
| `Queue/PrintJob.cs` | Campos: PedidoIds, CashDrawerCode, etc. (sin BitmapBase64) |
| `Data/Models/PrintJobEntity.cs` | Columnas correspondientes |
| `Api/Controllers/PrintController.cs` | Parsear nuevos campos + respuesta con `job_id ↔ pedido_ids` (ver §10) |
| `Workers/PrintWorker.cs` | Renderizado HTML→Bitmap local (ver §11.5) |
| `Drivers/EscPosCommandBuilder.cs` | Método `AddBitmapFromImage(Bitmap)` → comandos GS v 0 |

---

## 17. Secuencia de implementación

### Fase 7A: Comandas (MVP)

```
1.  [PrinterServices] Copiar HtmlBitmapRenderer.cs → Rendering/ (cambiar namespace)
2.  [PrinterServices] Crear BitmapResizer.cs en Rendering/
3.  [PrinterServices] Agregar campos nuevos a PrintJob, PrintJobEntity, ParsePrintJob
    (PedidoIds, CashDrawerCode — SIN BitmapBase64)
4.  [PrinterServices] Implementar AddBitmapFromImage(Bitmap) en EscPosCommandBuilder
5.  [PrinterServices] Actualizar PrintWorker: ContenidoHtml → HtmlBitmapRenderer → Bitmap
6.  [PrinterServices] Actualizar PrintController: respuesta con job_id ↔ pedido_ids
7.  [PrinterServices] Build y verificar 0 errores

8.  [QuipuNetX] Mover clases base: ImpresionContext, PrintResultDto, IPrintStrategy,
    ConfiguracionImpresionAdapter, ImpresionStrategyFactory
9.  [QuipuNetX] Crear PrinterServiceClient.cs (con retry 2 intentos, mensajes dinámicos)
10. [QuipuNetX] Crear PrintJobResult.cs (DTO)
11. [QuipuNetX] Crear ServerComandasStrategy.cs
12. [QuipuNetX] Crear ServerImpresionService.cs (orquestador)
13. [QuipuNetX] Agregar USAR_PRINTER_SERVICE a FeatureFlagConfigReader
14. [QuipuNetX] Modificar PedidoController.addLista() con feature flag
15. [QuipuNetX] Build y verificar 0 errores

16. [QuipuNet.exe] Modificar ListaPedidosTemporalesPresenter.enviarPedidos()
17. [QuipuNet.exe] Build y verificar 0 errores

18. Testing end-to-end con flag OFF (sin regresión)
19. Testing end-to-end con flag ON (comandas vía PrinterServices)
```

### Fase 7B: Comprobantes

```
20. [QuipuNetX] Crear ServerVentaRapidaStrategy, ServerVentaSalonStrategy,
    ServerDeliveryStrategy, ServerVentaProveedorStrategy, ServerAnularCorregirStrategy
21. [QuipuNetX] Crear ServerVentaImpresionWorkflow
22. [QuipuNetX] Modificar VentaController con feature flag
23. [QuipuNet.exe] Modificar FragmentDetallePago con feature flag
24. Testing comprobantes end-to-end
```

### Fase 7C: Secundarios

```
25. Migrar sorteo, encuesta, motorizado (ya incluidos en estrategias de 7B)
26. Migrar reimpresión
27. Testing completo
```

---

## 18. Verificación y Testing

### 18.1 Feature flag OFF (sin cambios)

```
✅ Flujo actual completo: pedido → ImpresionList → Front → ImpresionService → PrintUtil → TCP:9100
✅ No hay regresión en ningún tipo de impresión
✅ Estrategias del Front siguen funcionando normalmente
```

### 18.2 Feature flag ON + Servidor (con PrinterServices)

```
✅ Pedido → backend ejecuta ServerComandasStrategy → PrinterServiceClient → PrinterServices → TCP:9100
✅ Venta → backend ejecuta Server*Strategy → PrinterServiceClient → PrinterServices → TCP:9100
✅ Front NO intenta imprimir nada
✅ PrinterServices responde con job_id ↔ pedido_ids
✅ gRPC NotifyPrintFailed(job_id) → Front marca pedidos asociados como fallidos
✅ gRPC NotifyPrintSuccess(job_id) → Front confirma impresión de pedidos
✅ Stickers Jaltech siguen imprimiéndose por flujo actual (Front)
```

### 18.3 Feature flag ON + Cliente

```
✅ Máquina cliente ignora el flag — flujo actual siempre
```

### 18.4 PrinterServices caído + flag ON

```
✅ Backend retorna error al Front
✅ Front muestra error al usuario
✅ NO hay fallback local
```

---

## 19. Restricciones y reglas

1. **Solo servidor**: `USAR_PRINTER_SERVICE` solo aplica si `Util.esModoServidor() == true`
2. **No stickers**: `ImprimirStickerJaltech()` se excluye, sigue con flujo actual
3. **Sin fallback**: Si PrinterServices no responde (después de 2 intentos), error (no impresión local)
4. **Backward compatible**: Con flag OFF, todo funciona exactamente igual que antes
5. **Enriquecimiento en backend**: `Impresora.findById()` en QuipuNetX antes de enviar a PrinterServices
6. **Bitmap renderizado LOCAL en PrinterServices**: `HtmlBitmapRenderer` se copia a PrinterServices; NO se envía base64 en JSON
7. **Mismos NuGet**: No se agregan NuGet nuevos a QuipuNetX (HttpClient estándar .NET)
8. **Front intacto**: Las estrategias del Front NO se eliminan ni modifican, solo se dejan de llamar cuando flag ON
9. **Camino A**: Las estrategias viven en el backend — el Front NO orquesta impresión con flag ON
10. **Reintentos**: PrinterServiceClient intenta 2 veces (1 + 1 reintento) con 1s entre intentos antes de declarar servicio no disponible
11. **Mensajes dinámicos**: `Respuesta.Mensajes` dice "Imprimiendo {tipo}..." según la Strategy que lo invoca
12. **SUCCESS ≠ impreso**: `Respuesta.Tipo = SUCCESS` significa "jobs registrados en cola", NO "ya se imprimió". El resultado final llega por gRPC
