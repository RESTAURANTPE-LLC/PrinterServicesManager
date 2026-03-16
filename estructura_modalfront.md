# Documentación: Modales de Notificación de Impresión

## Índice
1. [Visión General](#1-visión-general)
2. [Arquitectura y Flujo Completo](#2-arquitectura-y-flujo-completo)
3. [CustomModalImprimiendoNewView](#3-custommodalimprimiendonewview)
4. [CustomModalImpresionExitosaNew](#4-custommodalimpresionexitosanew)
5. [CustomModalImpresionFallidaNew](#5-custommodalimpresionfallidanew)
6. [Componentes Compartidos](#6-componentes-compartidos)
7. [Dependencias Backend (QuipuNetX)](#7-dependencias-backend-quipunetx)
8. [Sitios de Uso (Callers)](#8-sitios-de-uso-callers)
9. [Diagrama de Secuencia](#9-diagrama-de-secuencia)
10. [Consideraciones y Problemas Conocidos](#10-consideraciones-y-problemas-conocidos)
11. [Funcionamiento Interno Profundo de los Modales](#11-funcionamiento-interno-profundo-de-los-modales)
12. [Integración Profunda con PrinterQueueManager](#12-integración-profunda-con-printerqueuemanager)
13. [Cadena Completa: Del Pedido al Modal](#13-cadena-completa-del-pedido-al-modal)
14. [Sistema de Animación y Badge de Cabecera](#14-sistema-de-animación-y-badge-de-cabecera)
15. [PrinterCommandViewModel — Reintento Individual](#15-printercommandviewmodel--reintento-individual)

---

## 1. Visión General

Estos tres modales conforman el **sistema de notificaciones visuales de impresión** del POS. Son ventanas WPF flotantes (tipo toast/notification) que aparecen en la **esquina inferior derecha** del escritorio y comunican al usuario el estado del proceso de impresión de documentos (comandas, boletas, etc.).

| Modal | Propósito | Apariencia |
|-------|-----------|------------|
| `CustomModalImprimiendoNewView` | Progreso en tiempo real de impresión | Barra de progreso azul animada |
| `CustomModalImpresionExitosaNew` | Confirmación de impresión exitosa | Badge verde "¡Éxito!" con info del documento |
| `CustomModalImpresionFallidaNew` | Error de impresión con opciones de acción | Alerta roja con botones Cancelar/Reintentar |

**Namespace común:** `QuipuNet.Xaml.NuevoDisenio.OpcionesGenerales.Notificaciones_Impresiones`

**Ubicación física:**
```
QuipuNet/Xaml/NuevoDisenio/OpcionesGenerales/Notificaciones Impresiones/
├── CustomModalImprimiendoNewView.xaml        + .xaml.cs
├── CustomModalImpresionExitosaNew.xaml       + .xaml.cs
└── CustomModalImpresionFallidaNew.xaml       + .xaml.cs
```

### Características comunes de las tres ventanas WPF

```xml
WindowStyle="None"
AllowsTransparency="True"
ResizeMode="NoResize"
ShowInTaskbar="False"
Topmost="True"
Background="Transparent"
```

- **Sin barra de título** (`WindowStyle="None"`) — aspecto de toast notification
- **Transparencia** habilitada para bordes redondeados con `CornerRadius`
- **No redimensionable**, no aparece en taskbar
- **Topmost=True** — siempre visible sobre otras ventanas
- **Fondo transparente** — el contenido visual lo da un `Border` con fondo blanco y bordes redondeados

---

## 2. Arquitectura y Flujo Completo

```
┌─────────────────────────────────────────────────────────────────┐
│                    FLUJO DE IMPRESIÓN                            │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  [Acción de imprimir]                                           │
│        │                                                         │
│        ▼                                                         │
│  PrinterQueueManager.Enqueue(command)                           │
│        │                                                         │
│        ├─► evento OpenProgress ──► MostrarProgresoImpresion()   │
│        │                           ┌─────────────────────────┐  │
│        │                           │ CustomModalImprimiendo   │  │
│        │                           │ NewView                  │  │
│        │                           │ (ProgressService como    │  │
│        │                           │  DataContext)             │  │
│        │                           └─────────────────────────┘  │
│        │                                                         │
│        ▼                                                         │
│  ProcessQueueAsync() — ejecuta cada IPrinterCommand              │
│        │                                                         │
│        ├─► Éxito → ProgressChanged → actualiza progreso         │
│        ├─► Fallo → FailedQueueManager.Add(command)              │
│        │                                                         │
│        ▼                                                         │
│  Al terminar cola:                                               │
│        │                                                         │
│        ├─► evento HideProgress ──► OcultarProgresoImpresion()   │
│        │                                                         │
│        ├─► Si hay errores:                                       │
│        │   ┌─────────────────────────┐                          │
│        │   │ CustomModalImpresion     │                          │
│        │   │ FallidaNew              │                           │
│        │   │ (ModaImpresionFallida    │                          │
│        │   │  ViewModel)              │                          │
│        │   │ [Cancelar] [Reintentar]  │                          │
│        │   └─────────────────────────┘                          │
│        │                                                         │
│        └─► Si NO hay errores:                                    │
│            (actualmente no se muestra                             │
│             CustomModalImpresionExitosaNew                       │
│             desde este flujo automático)                          │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### Relación entre componentes

```
QuipuNetX (Backend)                          QuipuNet (Frontend/WPF)
─────────────────────                        ───────────────────────
PrinterQueueManager (Singleton)       ◄───►  UtilesCabeceraViewModel
  ├── OpenProgress event              ────►    MostrarProgresoImpresion()
  ├── HideProgress event              ────►    OcultarProgresoImpresion()
  └── ProgressChanged event           ────►    ProgressService (polling)

FailedQueueManager (Singleton)        ◄───►  UtilesCabeceraViewModel
  ├── CommandAdded event              ────►    AddFailedPrint()
  └── CommandRemoved event            ────►    RemoveFailedPrint()

IPrinterCommand                       ◄───►  PrinterCommandViewModel
```

---

## 3. CustomModalImprimiendoNewView

### Descripción
Modal de **progreso en tiempo real** que se muestra mientras hay documentos imprimiéndose. Muestra una barra de progreso animada, cantidad de documentos pendientes, fecha/hora de inicio y tiempo transcurrido.

### Archivos
- **XAML:** `QuipuNet/Xaml/NuevoDisenio/OpcionesGenerales/Notificaciones Impresiones/CustomModalImprimiendoNewView.xaml`
- **Code-behind:** `...CustomModalImprimiendoNewView.xaml.cs`
- **ViewModel:** `QuipuNet/MainApp/Services/ProgressService.cs`

### Dimensiones
- `Height="90"`, `Width="380"`

### Estructura visual (XAML)

```
┌──────────────────────────────────────────────────────────┐
│  Imprimiendo {N} documento(s)         📅 Fecha | 🕐 Hora │
│  ┌──────────────────────────────────────────────────────┐│
│  │ ████████████████████░░░░░░░░░░  Imprimiendo... 00:05 ││
│  └──────────────────────────────────────────────────────┘│
│                                      [Cancelar] (oculto) │
└──────────────────────────────────────────────────────────┘
```

**Elementos principales:**
- **Fila 0:** Texto "Imprimiendo N documento(s)" + iconos fecha/hora
- **Fila 1:** Control `controles:ProgressBar` (componente custom)
- **Fila 2:** Botón "Cancelar" (`Visibility="Collapsed"` — actualmente oculto)

### Bindings del XAML

| Elemento | Binding | Propiedad en ProgressService |
|----------|---------|------------------------------|
| Cantidad docs | `{Binding CantidadPendiente}` | `int CantidadPendiente` |
| Fecha inicio | `{Binding FechaInicio}` | `string FechaInicio` (readonly) |
| Hora inicio | `{Binding HoraInicio}` | `string HoraInicio` (readonly) |
| Barra progreso | `{Binding Progreso}` | `double Progreso` (0-100) |
| Tiempo transcurrido | `{Binding TiempoTranscurrido}` | `string TiempoTranscurrido` ("MM:SS") |
| Indicador error | `{Binding HayErroresEnCola}` | `bool HayErroresEnCola` |

### Code-behind (CustomModalImprimiendoNewView.xaml.cs)

```csharp
public partial class CustomModalImprimiendoNewView : Window
{
    private ProgressService _progressService;

    public CustomModalImprimiendoNewView()
    {
        InitializeComponent();
        _progressService = new ProgressService();
        this.DataContext = _progressService;
    }
}
```

**Comportamiento:**
- En el constructor crea una instancia de `ProgressService` y la asigna como `DataContext`
- `ProgressService` inicia automáticamente su timer al construirse
- La vista es completamente pasiva — todo el comportamiento está en el ViewModel

### ViewModel: ProgressService

**Archivo:** `QuipuNet/MainApp/Services/ProgressService.cs`
**Patrón:** `INotifyPropertyChanged` (MVVM ligero sin framework)

**Funcionamiento interno:**

1. **Inicialización:** Al construirse, consulta `PrinterQueueManager.Instance.GetEnCola().Count` para obtener la cantidad inicial de documentos pendientes
2. **Timer:** `DispatcherTimer` con intervalo de **16ms** (~60fps) para actualización fluida de la barra
3. **Cálculo de progreso:** `Progreso = (msTranscurridos / DuracionTotal) * 100` donde `DuracionTotal = 3000ms × CantidadPendiente`
4. **Detección de nuevos docs:** Se suscribe a `PrinterQueueManager.Instance.ProgressChanged` — si la cantidad pendiente aumenta, **reinicia** el timer
5. **Finalización:** Cuando `msTranscurridos >= DuracionTotal` o `cantidadPendiente == 0`:
   - Revisa `FailedQueueManager` para detectar errores
   - Setea `HayErroresEnCola` (cambia color de la barra a rojo vía trigger)
   - Fija `Progreso = 100` y detiene el timer

**Propiedades observables:**

| Propiedad | Tipo | Descripción |
|-----------|------|-------------|
| `FechaInicio` | `string` | Fecha actual formateada (readonly, via `Util.CurrentDateFormato`) |
| `HoraInicio` | `string` | Hora actual sin segundos (readonly, via `Util.CurrentTimeSinSegundos`) |
| `CantidadPendiente` | `int` | Documentos restantes = enCola - completadas |
| `Progreso` | `double` | 0-100, calculo basado en tiempo estimado |
| `TiempoTranscurrido` | `string` | Formato "MM:SS" |
| `HayErroresEnCola` | `bool` | true si `FailedQueueManager` tiene elementos al finalizar |

**Duración estimada por impresión:** 3000ms (constante `DuracionImpresion`)

---

## 4. CustomModalImpresionExitosaNew

### Descripción
Modal de **confirmación de éxito** que muestra información del documento impreso: área de producción, salón/mesa, e impresora utilizada. Incluye un badge verde "¡Éxito!" y un botón de cierre.

### Archivos
- **XAML:** `QuipuNet/Xaml/NuevoDisenio/OpcionesGenerales/Notificaciones Impresiones/CustomModalImpresionExitosaNew.xaml`
- **Code-behind:** `...CustomModalImpresionExitosaNew.xaml.cs`
- **ViewModel:** Ninguno (datos seteados directamente en controles `x:Name`)

### Dimensiones
- `MaxHeight="120"`, `MaxWidth="380"`

### Estructura visual (XAML)

```
┌──────────────────────────────────────────────────────────┐
│  📋 Área de prod.: {nombre}          ┌──────┐  [👁] [✕] │
│  🪑 Salón y mesa: {salon/mesa}       │¡Éxito!│           │
│  🖨 Epson TM-T2011 - COCINA          └──────┘           │
│  ┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄ (sección colapsada)   │
└──────────────────────────────────────────────────────────┘
```

**Elementos principales:**
- **Columna izquierda (info):**
  - Área de producción (`tbAreaProduccion` — `Run`)
  - Salón y mesa (`tbSalonMesa` — `Run`)
  - Impresora (`tbImpresoraAreaProduccion` — `TextBlock`)
- **Columna derecha (acciones):**
  - Badge "¡Éxito!" (verde, `BackgroundPrimary5_New`)
  - Botón "Ver detalle" (`btnViewDetalleImpresion` — `Visibility="Collapsed"`)
  - Botón "Cerrar" (`brCerrar` — con handler `PreviewMouseLeftButtonDown`)
- **Fila inferior:** Panel expandible para modificadores (`Visibility="Collapsed"`)

### Code-behind

```csharp
public partial class CustomModalImpresionExitosaNew : Window
{
    public CustomModalImpresionExitosaNew()
    {
        InitializeComponent();
    }

    private void brCerrar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Vacío — no implementado
    }
}
```

### Estado actual

> **⚠️ IMPORTANTE:** Este modal **NO está siendo utilizado** en ningún flujo activo del sistema. El handler `brCerrar_PreviewMouseLeftButtonDown` está vacío. No tiene ViewModel, y los datos se poblarían manualmente vía `x:Name` references (`tbAreaProduccion`, `tbSalonMesa`, `tbImpresoraAreaProduccion`).
>
> El método `showSuccessPrinterService()` en `ListaPedidosTemporales.cs` lanza `NotImplementedException`.
>
> Parece ser un **componente diseñado pero no integrado aún** al flujo de impresión.

### Controles con x:Name (para setear desde code-behind)

| Nombre | Tipo | Uso esperado |
|--------|------|--------------|
| `tbAreaProduccion` | `Run` | Nombre del área de producción |
| `tbSalonMesa` | `Run` | Info del salón y mesa |
| `tbImpresoraAreaProduccion` | `TextBlock` | Nombre impresora + área (tiene placeholder: "Epson TM-T2011 - COCINA CALIENTES") |
| `btnViewDetalleImpresion` | `Border` | Botón ver detalle (colapsado) |
| `brCerrar` | `StackPanel` | Botón cerrar (handler vacío) |
| `grContainermodificadores` | `StackPanel` | Contenedor expansible (colapsado) |

### Converter utilizado
- `ThicknessMaxConverter` — convierte `Thickness` a un valor máximo para bordes dashed (usado en la sección colapsada)

---

## 5. CustomModalImpresionFallidaNew

### Descripción
Modal de **error de impresión** que muestra cuántas impresiones fallaron, fecha/hora, y ofrece dos acciones: **Cancelar** (cerrar modal) y **Reintentar** (re-ejecutar las impresiones fallidas con barra de progreso).

### Archivos
- **XAML:** `QuipuNet/Xaml/NuevoDisenio/OpcionesGenerales/Notificaciones Impresiones/CustomModalImpresionFallidaNew.xaml`
- **Code-behind:** `...CustomModalImpresionFallidaNew.xaml.cs`
- **ViewModel:** `QuipuNet/MainApp/NuevoDisenioQuipunet/Impresoras/ViewModels/ModaImpresionFallidaViewModel.cs`

### Dimensiones
- `SizeToContent="Height"`, `Width="428"` — altura dinámica según contenido

### Estructura visual (XAML)

```
┌────┬─────────────────────────────────────────────────────┐
│    │  Tienes {N} impresione(s) con error                 │
│ ⚠️ │  📅 Fecha        🕐 Hora                            │
│    │  ┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄  │
│    │  ┌──────────────────────────────────────────────┐   │
│    │  │ ████████ Imprimiendo...              00:03   │   │
│    │  └──────────────────────────────────────────────┘   │
│    │  ┌──────────────┐  ┌──────────────────┐            │
│    │  │   Cancelar   │  │    Reintentar    │            │
│    │  └──────────────┘  └──────────────────┘            │
└────┴─────────────────────────────────────────────────────┘
```

**Layout de dos columnas:**
- **Columna izquierda:** Icono de alerta triangular blanco sobre fondo rojo (`BackgroundDanger_New`) con `CornerRadius="10 0 0 10"`
- **Columna derecha (3 filas):**
  - **Fila 0:** Encabezado con cantidad de errores + fecha/hora, separado con línea dashed
  - **Fila 1:** `controles:ProgressBar` — visible solo durante reintento (`Visibility="{Binding VisibilityProgreso}"`)
  - **Fila 2:** Grid con dos botones: Cancelar y Reintentar

### Bindings del XAML

| Elemento | Binding | Propiedad en ViewModel |
|----------|---------|------------------------|
| Cantidad errores | `{Binding CantidadErrores}` | `int CantidadErrores` |
| Fecha | `{Binding Fecha}` | `string Fecha` (readonly) |
| Hora | `{Binding Hora}` | `string Hora` (readonly) |
| Progreso barra | `{Binding Progreso}` | `double Progreso` |
| Visibility barra | `{Binding VisibilityProgreso}` | `Visibility VisibilityProgreso` |
| Tiempo transcurrido | `{Binding TiempoTranscurrido}` | `string TiempoTranscurrido` |
| Indicador error cola | `{Binding HayErroresEnCola}` | `bool HayErroresEnCola` |
| Habilitar botones | `{Binding MostrarComandos}` | `bool MostrarComandos` |
| Btn Cancelar | `{Binding CancelarCommand}` | `ICommand CancelarCommand` |
| Btn Reintentar | `{Binding ReintentarCommand}` | `ICommand ReintentarCommand` |

### Code-behind

```csharp
public partial class CustomModalImpresionFallidaNew : Window
{
    public ModaImpresionFallidaViewModel ViewModel { get; private set; }

    public CustomModalImpresionFallidaNew(ModaImpresionFallidaViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        ViewModel.CloseAction = new Action(() => cerrarModal());
        this.DataContext = ViewModel;
    }

    private void cerrarModal()
    {
        this.Close();
    }
}
```

**Comportamiento:**
- Recibe el `ViewModel` por **inyección en constructor** (a diferencia de `CustomModalImprimiendoNewView` que lo crea internamente)
- Conecta el `CloseAction` del ViewModel al método `cerrarModal()` → permite que el ViewModel cierre la ventana sin referencia directa a la View
- Patrón MVVM con cierre desacoplado

### ViewModel: ModaImpresionFallidaViewModel

**Archivo:** `QuipuNet/MainApp/NuevoDisenioQuipunet/Impresoras/ViewModels/ModaImpresionFallidaViewModel.cs`
**Patrón:** `INotifyPropertyChanged`

**Estados del modal:**

```
ESTADO INICIAL                     ESTADO REINTENTANDO
┌─────────────────────┐           ┌─────────────────────┐
│ N errores           │  Click    │ N errores           │
│ [barra oculta]      │ ──────►  │ [barra visible]     │
│ [Cancelar][Reintentar]│         │ [deshabilitados]    │
└─────────────────────┘           └─────────────────────┘
                                           │
                                    Timer completa
                                           │
                                           ▼
                                  ┌─────────────────────┐
                                  │ Se cierra automát.   │
                                  │ tras 5s de delay     │
                                  └─────────────────────┘
```

**Propiedades:**

| Propiedad | Tipo | Descripción |
|-----------|------|-------------|
| `CantidadErrores` | `int` | Count de `FailedQueueManager.Instance.GetAll()` |
| `Fecha` | `string` | Fecha actual (readonly via `Util.CurrentDateFormato`) |
| `Hora` | `string` | Hora actual (readonly via `Util.CurrentTimeSinSegundos`) |
| `Progreso` | `double` | 0-100 (private set) |
| `TiempoTranscurrido` | `string` | "MM:SS" |
| `HayErroresEnCola` | `bool` | true si quedan errores tras reintento |
| `MostrarProgreso` | `bool` | Controla visibilidad de la barra |
| `VisibilityProgreso` | `Visibility` | Computed: `MostrarProgreso ? Visible : Collapsed` |
| `MostrarComandos` | `bool` | Habilita/deshabilita botones durante reintento |
| `CloseAction` | `Action` | Callback inyectado por la View para cerrarse |

**Commands:**

| Command | Acción |
|---------|--------|
| `ReintentarCommand` | 1. Muestra barra de progreso<br>2. Llama `FailedQueueManager.Instance.RetryAllFailedAsync()`<br>3. Inicia timer de progreso |
| `CancelarCommand` | Invoca `CloseAction` → cierra el modal |

**Timer de reintento (`iniciaTimer`):**
- `DispatcherTimer` a 16ms (~60fps)
- `DuracionTotal = 3000ms × CantidadErrores`
- Al completar: verifica si quedan errores, espera 5 segundos (`Task.Delay(5000, _cts.Token)`), y luego auto-cierra vía `CloseAction`
- `CancellationTokenSource` para cancelar el delay si el usuario reintenta de nuevo antes de los 5s

### Estilos XAML custom definidos en Resources

El XAML define 3 estilos locales para un `Expander` personalizado (actualmente no usado visiblemente):
- `ExpanderDownHeaderStyle` — `ToggleButton` con flecha chevron
- `ExpanderUpHeaderStyle` — `ToggleButton` con flecha invertida (180°)
- `ExpanderStyle1` — Expander completo con background `BackgroundInfo_5`

Estos estilos están preparados para una sección expandible de detalle que actualmente no se renderiza.

---

## 6. Componentes Compartidos

### 6.1 Control `ProgressBar` (Custom UserControl)

**Archivos:**
- `QuipuNet/Xaml/NuevoDisenio/Componentes/ProgressBar.xaml`
- `QuipuNet/Xaml/NuevoDisenio/Componentes/ProgressBar.xaml.cs`

**Namespace:** `QuipuNet.Xaml.NuevoDisenio.Componentes`

**Usado por:** `CustomModalImprimiendoNewView` y `CustomModalImpresionFallidaNew`

**Dependency Properties:**

| Propiedad | Tipo | Default | Descripción |
|-----------|------|---------|-------------|
| `Value` | `double` | 0.0 | Progreso actual (0-100) |
| `TiempoTranscurrido` | `string` | "00:00" | Tiempo en formato MM:SS |
| `TieneError` | `bool` | false | Indicador de error (legacy) |
| `EsErrorProgress` | `bool` | false | Indicador de error (usado en triggers) |
| `TipoAccion` | `TipoAccionProgreso` | `Actualizacion` | Enum: `Actualizacion` o `Impresion` |

**Comportamiento visual (Triggers):**

| Condición | Color barra | Texto |
|-----------|-------------|-------|
| En progreso (< 100%) | `#76A8F9` (azul) | "Imprimiendo..." / "Actualizando..." |
| Completado (100%) sin error | `#9AD791` (verde) | "¡Envío exitoso!" / "¡Actualización exitosa!" |
| Error | `#F45B69` (rojo) | "¡Envío fallido!" / "¡Actualización fallida!" |

**Converters internos:**
- `ProgressWidthConverter` (`IMultiValueConverter`) — calcula ancho del indicador: `trackWidth × (value / maximum)`
- `TextoProgresoConverter` (`IMultiValueConverter`) — genera texto según `TipoAccion`, `EsErrorProgress` y `Value`

### 6.2 Enum `TipoAccionProgreso`

```csharp
public enum TipoAccionProgreso
{
    Actualizacion,  // Para modal de actualización del sistema
    Impresion       // Para modales de impresión
}
```

Los modales de impresión siempre usan `TipoAccion="Impresion"` en el XAML.

### 6.3 ThicknessMaxConverter

**Archivo:** `QuipuNet/Converters/ThicknessMaxConverter.cs`

Converter usado por `CustomModalImpresionExitosaNew` y `CustomModalImpresionFallidaNew` para generar bordes dashed (líneas punteadas) vía `VisualBrush` con `Rectangle.StrokeDashArray`.

---

## 7. Dependencias Backend (QuipuNetX)

### 7.1 PrinterQueueManager (Singleton)

**Archivo:** `QuipuNetX/PrinterManager/PrinterQueueManager.cs`

Cola de impresión principal. Gestiona el ciclo de vida de los `IPrinterCommand`.

**Eventos que disparan los modales:**

| Evento | Cuándo se dispara | Modal afectado |
|--------|-------------------|----------------|
| `OpenProgress` | Al iniciar `ProcessQueueAsync()` (hay docs en cola) | Abre `CustomModalImprimiendoNewView` |
| `ProgressChanged` | Después de cada impresión completada | Actualiza `ProgressService` (cuenta pendientes) |
| `HideProgress` | Al terminar toda la cola + 1s delay | Cierra `CustomModalImprimiendoNewView`, abre `CustomModalImpresionFallidaNew` si hay errores |

**Flujo interno de `ProcessQueueAsync()`:**
1. Dispara `OpenProgress`
2. Loop: dequeue → `command.ExecutePrintAsync()` → si exitosa, agrega a completadas; si falla, agrega a `FailedQueueManager`
3. Dispara `ProgressChanged` tras cada comando
4. Al vaciar cola: `await Task.Delay(1000)` → `LimpiarProgreso()` → `HideProgress`

### 7.2 FailedQueueManager (Singleton)

**Archivo:** `QuipuNetX/PrinterManager/FailedQueueManager.cs`

Gestiona comandos de impresión fallidos con capacidad de reintento.

**Eventos:**

| Evento | Cuándo | Efecto en UI |
|--------|--------|--------------|
| `CommandAdded` | Cuando una impresión falla | Agrega a la lista visual en cabecera + inicia animación |
| `CommandRemoved` | Cuando un reintento es exitoso | Remueve de la lista visual |

**Método clave: `RetryAllFailedAsync()`**
- Llamado por `ModaImpresionFallidaViewModel.ReintentarCommand`
- Itera sobre todos los comandos fallidos y ejecuta `RetrySingleFailedAsync(command)`
- Si el reintento es exitoso (`Estado == EXITOSA`), lo remueve de la lista

### 7.3 IPrinterCommand

**Archivo:** `QuipuNetX/PrinterManager/Interfaces/IPrinterCommand.cs`

```csharp
public interface IPrinterCommand : INotifyPropertyChanged
{
    Impresion GetImpresion { get; }
    Definitions.ESTADO_IMPRESION Estado { get; set; }
    Task ExecutePrintAsync();
}
```

---

## 8. Sitios de Uso (Callers)

### 8.1 UtilesCabeceraViewModel (Flujo principal)

**Archivo:** `QuipuNet/ViewModels/General/UtilesCabeceraViewModel.cs`

Este es el **flujo principal y automático** de los modales de impresión. Se suscribe a los eventos de `PrinterQueueManager` y `FailedQueueManager` en el método `SubscribeToQueueEvents()`.

```csharp
private void SubscribeToQueueEvents()
{
    FailedQueueManager.Instance.CommandAdded += cmd => AddFailedPrint(cmd);
    FailedQueueManager.Instance.CommandRemoved += cmd => RemoveFailedPrint(cmd);
    PrinterQueueManager.Instance.HideProgress += OcultarProgresoImpresion;
    PrinterQueueManager.Instance.OpenProgress += MostrarProgresoImpresion;
}
```

**`MostrarProgresoImpresion()`** (línea ~614):
- Ejecuta en UI thread vía `DispatcherHelper.ExecuteOnDispatcher`
- Usa `lock(_modalLock)` para prevenir múltiples modales simultáneos
- Crea `CustomModalImprimiendoNewView`, la posiciona en esquina inferior derecha
- Guarda referencia en campo `modal` para posterior cierre

**`OcultarProgresoImpresion()`** (línea ~554):
- Cierra el modal de progreso con manejo seguro (try/catch + null check)
- Espera 1 segundo (`Task.Delay(1000)`)
- Si `FailedQueueManager.Instance.GetAll().Count > 0`:
  - Crea `ModaImpresionFallidaViewModel` + `CustomModalImpresionFallidaNew`
  - Posiciona en esquina inferior derecha
  - Auto-cierra tras 5 segundos

**Posicionamiento (esquina inferior derecha):**
```csharp
var desktopWorkingArea = SystemParameters.WorkArea;
modal.Left = desktopWorkingArea.Right - modal.Width - 40;
modal.Top = desktopWorkingArea.Bottom - modal.Height - 25;
```

### 8.2 ListaPedidosTemporales (Flujo manual)

**Archivo:** `QuipuNet/MainApp/venta/Pedidos/ListaPedidosTemporales.cs`

Expone métodos públicos para mostrar modales de impresión de forma manual/directa:

```csharp
// Muestra modal de error y auto-ejecuta reintento
public void showFailedPrinterService()
{
    ModaImpresionFallidaViewModel viewModel = new ModaImpresionFallidaViewModel();
    CustomModalImpresionFallidaNew modalImpresionFallidaNew = new CustomModalImpresionFallidaNew(viewModel);
    modalImpresionFallidaNew.Show();
    viewModel.ReintentarCommand.Execute(null); // Auto-reintenta inmediatamente
    // ... posicionamiento ...
}

// Muestra modal de progreso
public void showImprimiendoPrinterService()
{
    CustomModalImprimiendoNewView modalImprimiendo = new CustomModalImprimiendoNewView();
    modalImprimiendo.Show();
    // ... posicionamiento ...
}

// NO implementado
public void showSuccessPrinterService()
{
    throw new NotImplementedException();
}
```

### 8.3 ListaPedidos (Flujo manual)

**Archivo:** `QuipuNet/MainApp/venta/Pedidos/ListaPedidos.cs`

```csharp
public async Task showModalErroresImpresiones()
{
    var viewModel = new ModaImpresionFallidaViewModel();
    var modalImpresionFallidaNew = new CustomModalImpresionFallidaNew(viewModel);
    modalImpresionFallidaNew.Show();
    viewModel.ReintentarCommand.Execute(null); // Auto-reintenta
    // ... posicionamiento ...
}
```

### Resumen de callers

| Caller | Modal Imprimiendo | Modal Exitosa | Modal Fallida |
|--------|:-:|:-:|:-:|
| `UtilesCabeceraViewModel` | ✅ (automático) | ❌ | ✅ (automático) |
| `ListaPedidosTemporales` | ✅ (manual) | ❌ (`NotImplementedException`) | ✅ (manual + auto-retry) |
| `ListaPedidos` | ❌ | ❌ | ✅ (manual + auto-retry) |

---

## 9. Diagrama de Secuencia

```
    Usuario          PrinterQueueManager       UtilesCabeceraVM        ProgressService         FailedQueueManager       ModalFallida
      │                      │                       │                       │                       │                       │
      │  [Imprimir]          │                       │                       │                       │                       │
      │─────────────────────►│                       │                       │                       │                       │
      │                      │  Enqueue(cmd)         │                       │                       │                       │
      │                      │  ──► ProcessQueueAsync│                       │                       │                       │
      │                      │                       │                       │                       │                       │
      │                      │  OpenProgress ────────►│                       │                       │                       │
      │                      │                       │  new ModalImprimiendo │                       │                       │
      │                      │                       │──────────────────────►│  new ProgressService  │                       │
      │                      │                       │                       │  Timer.Start()        │                       │
      │                      │                       │                       │  ◄── polling queue ──►│                       │
      │                      │                       │                       │                       │                       │
      │                      │  ExecutePrint (OK)    │                       │                       │                       │
      │                      │  ProgressChanged─────►│                       │                       │                       │
      │                      │                       │                       │  actualiza progreso   │                       │
      │                      │                       │                       │                       │                       │
      │                      │  ExecutePrint (FAIL)  │                       │                       │                       │
      │                      │  ──────────────────────────────────────────────────────────────────────►│  Add(cmd)             │
      │                      │                       │                       │                       │  CommandAdded ──────►  │
      │                      │                       │                       │                       │  (animación cabecera) │
      │                      │                       │                       │                       │                       │
      │                      │  HideProgress ────────►│                       │                       │                       │
      │                      │                       │  modal.Close()        │                       │                       │
      │                      │                       │  await 1000ms         │                       │                       │
      │                      │                       │  ¿hay errores?        │                       │                       │
      │                      │                       │  SÍ ──────────────────────────────────────────────────────────────────►│
      │                      │                       │                       │                       │  new ModalFallida     │
      │                      │                       │                       │                       │  Show()               │
      │                      │                       │                       │                       │                       │
      │  [Click Reintentar]  │                       │                       │                       │                       │
      │──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────►│
      │                      │                       │                       │                       │  RetryAllFailedAsync()│
      │                      │                       │                       │                       │◄─────────────────────│
      │                      │                       │                       │                       │  retry cada cmd      │
      │                      │                       │                       │                       │                       │
      │                      │                       │                       │                       │  Timer completa      │
      │                      │                       │                       │                       │  await 5000ms        │
      │                      │                       │                       │                       │  CloseAction()───────►│
      │                      │                       │                       │                       │                 Close()│
```

---

## 10. Consideraciones y Problemas Conocidos

### 10.1 CustomModalImpresionExitosaNew no integrada
El modal de éxito existe como XAML completo pero **no está conectado a ningún flujo**. El handler de cierre está vacío, no tiene ViewModel, y el método `showSuccessPrinterService()` lanza `NotImplementedException`. Si se desea integrar, se necesita:
1. Crear un ViewModel o poblar los controles `x:Name` directamente
2. Implementar el handler de cierre
3. Conectar al flujo de `HideProgress` cuando no hay errores

### 10.2 Falta de limpieza de event handlers en ProgressService
`ProgressService` se suscribe a `PrinterQueueManager.Instance.ProgressChanged` en su constructor pero **nunca se desuscribe**. Dado que `PrinterQueueManager` es Singleton, cada vez que se crea un nuevo `CustomModalImprimiendoNewView` (y por tanto un nuevo `ProgressService`), se acumula un handler. Esto puede causar **memory leaks** y ejecución de callbacks en instancias obsoletas.

**Recomendación:** Implementar `IDisposable` en `ProgressService` y desuscribirse, o usar `WeakEventManager` (per AGENTS.md).

### 10.3 Timer a 16ms en UI thread
Ambos `ProgressService` y `ModaImpresionFallidaViewModel` usan `DispatcherTimer` con intervalo de 16ms, lo cual es muy frecuente (~60 ticks/seg). Para una barra de progreso de impresión, un intervalo de 50-100ms sería suficiente y reduciría carga en el UI thread.

### 10.4 Concurrencia del lock en UtilesCabeceraViewModel
`MostrarProgresoImpresion()` usa `lock(_modalLock)` dentro de un `DispatcherHelper.ExecuteOnDispatcher(async () => ...)`. El lock protege contra múltiples aperturas, pero al estar en el UI thread (Dispatcher), la contención real es mínima. Sin embargo, `OcultarProgresoImpresion()` también usa el lock y realiza operaciones async después, lo cual está bien porque el lock se libera antes del `await`.

### 10.5 Auto-cierre del modal fallida puede competir con el cierre externo
En `UtilesCabeceraViewModel.OcultarProgresoImpresion()`, el modal fallida se cierra externamente tras 5s (`await Task.Delay(5000); modalImpresionFallidaNew.Close()`). Pero el propio `ModaImpresionFallidaViewModel` también puede auto-cerrarse vía `CloseAction` tras completar el reintento. Esto puede causar doble `Close()` — actualmente mitigado con try/catch pero podría limpiarse.

### 10.6 Posicionamiento hardcoded
Todas las instancias usan la misma fórmula de posicionamiento:
```csharp
modal.Left = desktopWorkingArea.Right - modal.Width - 40;
modal.Top = desktopWorkingArea.Bottom - modal.Height - 25;
```
No hay manejo de múltiples monitores ni apilamiento de notificaciones.

---

## 11. Funcionamiento Interno Profundo de los Modales

### 11.1 Máquina de Estados del Modal "Imprimiendo"

El modal `CustomModalImprimiendoNewView` no tiene estados explícitos, pero su `ProgressService` implementa una máquina de estados implícita controlada por el timer y los eventos del `PrinterQueueManager`:

```
                         new ProgressService()
                                │
                                ▼
                    ┌───────────────────────┐
                    │   ESTADO: ANIMANDO    │◄──────────────────┐
                    │                       │                    │
                    │ Timer 16ms activo     │   ProgressChanged  │
                    │ Progreso 0→100%       │   (nuevos docs)    │
                    │ CantidadPendiente > 0 │   Reiniciar()      │
                    └───────┬───────────────┘                    │
                            │                                    │
              msTranscurridos >= DuracionTotal                   │
              OR cantidadPendiente == 0                          │
                            │                                    │
                            ▼                                    │
                    ┌───────────────────────┐                    │
                    │   ESTADO: COMPLETADO  │                    │
                    │                       │                    │
                    │ Progreso = 100        │                    │
                    │ Timer.Stop()          │                    │
                    │ HayErroresEnCola?     │────── SI ─────────►│ (barra se pone ROJA)
                    │                       │                    │
                    └───────────────────────┘                    │
                                                                 │
                    (El cierre del modal lo hace                 │
                     UtilesCabeceraViewModel vía                 │
                     HideProgress event, NO el                   │
                     ProgressService)                            │
```

**Puntos clave:**
- `ProgressService` **NO cierra** el modal. Solo anima la barra y detecta errores.
- El cierre lo controla `UtilesCabeceraViewModel.OcultarProgresoImpresion()` que escucha el evento `HideProgress`.
- Si llegan **nuevos documentos** mientras se anima (vía `ProgressChanged`), el timer se reinicia desde cero con nueva duración.

### 11.2 Cálculo de Progreso en ProgressService (Timer Tick detallado)

Cada tick (16ms), el `ProgressService` ejecuta esta lógica:

```csharp
// 1. Calcular tiempo transcurrido desde inicio
var tiempo = DateTime.Now - _inicio;
var msTranscurridos = tiempo.TotalMilliseconds;

// 2. Consultar estado actual de la cola (LECTURAS DIRECTAS al singleton)
var enCola = PrinterQueueManager.Instance.GetEnCola().Count;
var completadas = PrinterQueueManager.Instance.GetCompletadas().Count;
var cantidadPendiente = Math.Max(enCola - completadas, 0);

// 3. Actualizar UI bindings
CantidadPendiente = cantidadPendiente;
TiempoTranscurrido = $"{(int)tiempo.TotalMinutes:00}:{tiempo.Seconds:00}";

// 4. ¿Terminó?
if (msTranscurridos >= DuracionTotal || cantidadPendiente == 0)
{
    // Revisar errores en cola de fallidos
    HayErroresEnCola = FailedQueueManager.Instance.GetAll().Count > 0;
    Progreso = 100;
    _timer.Stop();
}
else
{
    // Progreso lineal basado en tiempo estimado
    Progreso = (msTranscurridos / DuracionTotal) * 100;
}
```

**Observación importante:** El progreso es **basado en tiempo estimado** (3s por documento), NO en la finalización real de cada impresión. Esto significa que la barra puede llegar al 100% antes o después de que realmente terminen las impresiones. El `PrinterQueueManager` es quien sabe cuándo realmente terminó.

### 11.3 Mecanismo de Reinicio por Nuevos Documentos

```csharp
// Suscripción en constructor de ProgressService
PrinterQueueManager.Instance.ProgressChanged += () =>
{
    var cantidadPendiente = Math.Max(enCola - completadas, 0);

    // Si la cantidad pendiente AUMENTÓ respecto a la anterior
    if (cantidadPendiente > _cantidadPendienteAnterior)
    {
        Reiniciar(); // Stop timer + recalcular DuracionTotal + Start timer
    }

    CantidadPendiente = cantidadPendiente;
};
```

Esto permite que si el usuario envía más comandas mientras se imprime, el modal **no se cierra prematuramente** sino que extiende su duración.

### 11.4 Máquina de Estados del Modal "Fallida"

```
     new ModaImpresionFallidaViewModel()
                    │
                    ▼
     ┌──────────────────────────────┐
     │   ESTADO: MOSTRANDO_ERRORES  │
     │                              │
     │ CantidadErrores = N          │
     │ MostrarProgreso = false      │
     │ Barra OCULTA                 │
     │ Botones HABILITADOS          │
     └──────┬──────────┬────────────┘
            │          │
   [Cancelar]     [Reintentar]
            │          │
            ▼          ▼
     ┌─────────┐  ┌──────────────────────────────┐
     │ CERRADO │  │   ESTADO: REINTENTANDO        │
     │         │  │                                │
     │ Close() │  │ MostrarProgreso = true         │
     └─────────┘  │ FailedQueueManager             │
                  │   .RetryAllFailedAsync()        │
                  │ Timer 16ms activo               │
                  │ Botones deshabilitados           │
                  └──────────┬─────────────────────┘
                             │
                   Timer completa (100%)
                             │
                             ▼
                  ┌──────────────────────────────┐
                  │   ESTADO: ESPERANDO_CIERRE    │
                  │                                │
                  │ HayErroresEnCola? (recalcula)  │
                  │ await Task.Delay(5000, _cts)   │
                  │                                │
                  │ Si se cancela el token:         │
                  │   → return (no cierra)          │
                  │ Si completa 5s:                 │
                  │   → CloseAction() → Close()     │
                  └────────────────────────────────┘
```

**El `CancellationTokenSource` (`_cts`) es clave:**
- Si el usuario clickea "Reintentar" por segunda vez ANTES de que pasen los 5s, se cancela el delay anterior y se inicia un nuevo ciclo de reintento.
- Sin esto, el `CloseAction` del primer reintento podría cerrar el modal durante el segundo reintento.

### 11.5 Ciclo de Vida del Modal Fallida — Timer de Reintento detallado

```csharp
private async void OnTimerTick(object sender, EventArgs e)
{
    // 1. Calcular tiempo transcurrido
    var tiempo = DateTime.Now - _inicio;
    var msTranscurridos = tiempo.TotalMilliseconds;

    // 2. Consultar FailedQueueManager (no PrinterQueueManager)
    CantidadPendiente = FailedQueueManager.Instance.GetEnCola().Count
                      - FailedQueueManager.Instance.GetCompletadas().Count;

    TiempoTranscurrido = $"{(int)tiempo.TotalMinutes:00}:{tiempo.Seconds:00}";

    // 3. ¿Terminó el reintento?
    if (msTranscurridos >= DuracionTotal || CantidadPendiente == 0)
    {
        // Recalcular errores remanentes
        CantidadErrores = FailedQueueManager.Instance.GetAll().Count;
        HayErroresEnCola = CantidadErrores > 0;

        Progreso = 100;
        _timer.Stop();

        // 4. Esperar 5 segundos y auto-cerrar
        try
        {
            await Task.Delay(5000, _cts.Token);
        }
        catch (TaskCanceledException)
        {
            return; // Fue cancelado por un nuevo reintento
        }
        CloseAction?.Invoke(); // Cerrar modal
    }
    else
    {
        Progreso = (msTranscurridos / DuracionTotal) * 100;
    }
}
```

**Diferencia clave con ProgressService:** El modal fallida consulta `FailedQueueManager` (no `PrinterQueueManager`) porque está reintentando comandos que ya fallaron.

---

## 12. Integración Profunda con PrinterQueueManager

### 12.1 Arquitectura Interna del PrinterQueueManager

```
PrinterQueueManager (Singleton)
├── _queue          : Queue<IPrinterCommand>     ← Cola principal FIFO
├── _processing     : List<IPrinterCommand>      ← En ejecución actual
├── _completed      : List<IPrinterCommand>      ← Completados en este ciclo
├── _cola           : List<IPrinterCommand>      ← Todos los encolados en este ciclo
├── _isProcessing   : bool                       ← Flag de procesamiento
├── _lock           : object                     ← Lock para thread-safety
│
├── Eventos:
│   ├── OpenProgress  : Action       ← Disparo: inicio de ProcessQueueAsync
│   ├── HideProgress  : Action       ← Disparo: fin de ProcessQueueAsync
│   └── ProgressChanged : Action     ← Disparo: después de cada ExecutePrintAsync
│
└── Métodos:
    ├── Enqueue(command)             ← Punto de entrada público
    ├── ProcessQueueAsync()          ← Loop principal (background)
    ├── LimpiarProgreso()            ← Reset listas internas
    ├── GetEnCola() → List           ← Snapshot de _cola
    ├── GetCompletadas() → List      ← Snapshot de _completed
    └── GetEnProceso() → List        ← Snapshot de _processing
```

### 12.2 Flujo Interno de Enqueue → Eventos → Modales

```
Thread llamante (UI o Background)
│
▼
Enqueue(command)
│
├── lock(_lock)
│   ├── _queue.Enqueue(command)      ← Agrega a cola FIFO
│   ├── _cola.Add(command)           ← Agrega a lista total del ciclo
│   └── if (!_isProcessing)
│       └── _isProcessing = true
│           shouldStartProcessing = true
│
├── if (shouldStartProcessing)
│   └── Task.Run(ProcessQueueAsync)  ← INICIA PROCESSING EN THREAD POOL
│
│   (Si ya estaba procesando, el nuevo command
│    será consumido por el loop existente)
│
▼
ProcessQueueAsync() — Thread Pool
│
├── ══════ OpenProgress?.Invoke() ══════════════════════════════════
│   │                                                              │
│   └──► UtilesCabeceraViewModel.MostrarProgresoImpresion()        │
│        │                                                          │
│        ├── DispatcherHelper.ExecuteOnDispatcher()                 │
│        │   (marshalling a UI thread)                              │
│        │                                                          │
│        ├── lock(_modalLock)                                       │
│        │   ├── if modal ya visible → return                       │
│        │   ├── modal = new CustomModalImprimiendoNewView()        │
│        │   │   └── constructor crea ProgressService               │
│        │   │       └── ProgressService.constructor:               │
│        │   │           ├── lee PrinterQueueManager.GetEnCola()    │
│        │   │           ├── suscribe a ProgressChanged             │
│        │   │           └── Iniciar() → timer.Start()              │
│        │   ├── modal.Show()                                       │
│        │   └── posicionar esquina inferior derecha                │
│        └──────────────────────────────────────────────────────────
│
├── LOOP while (_queue.Any()):
│   │
│   ├── lock: command = _queue.Dequeue()
│   │         _processing.Add(command)
│   │
│   ├── await command.ExecutePrintAsync()
│   │   │
│   │   │   (Dentro del command:)
│   │   │   Estado = IMPRIMIENDO
│   │   │   var respuesta = await PrintUtil.imprimirXxxAsync(_impresion)
│   │   │   Estado = respuesta.Tipo == SUCCESS ? EXITOSA : FALLIDA
│   │   │
│   │   └── _completed.Add(command)
│   │
│   ├── ══════ ProgressChanged?.Invoke() ══════════════════════════
│   │   │                                                          │
│   │   └──► ProgressService (handler suscrito):                   │
│   │        ├── Recalcula cantidadPendiente                       │
│   │        ├── Si aumentó → Reiniciar() timer                    │
│   │        └── Actualiza CantidadPendiente binding               │
│   │   ══════════════════════════════════════════════════════════  │
│   │
│   └── lock: Clasificar resultado
│       ├── Si Estado == EXITOSA:
│       │   └── Si estaba en FailedQueueManager → RemoveFailed(command)
│       └── Si Estado != EXITOSA:
│           └── Si NO está en FailedQueueManager → Add(command)
│               └── FailedQueueManager.CommandAdded event
│                   └── UtilesCabeceraViewModel.AddFailedPrint(cmd)
│                       ├── ImpresionesFallidas.Add(vm)
│                       ├── ContImpresiones = count
│                       └── iniciarAnimacion() → campana
│
├── await Task.Delay(1000)   ← Pausa de 1s tras vaciar cola
│
├── LimpiarProgreso()
│   └── _processing.Clear(), _completed.Clear(), _cola.Clear()
│       └── ProgressChanged?.Invoke()
│
└── ══════ HideProgress?.Invoke() ══════════════════════════════════
    │                                                              │
    └──► UtilesCabeceraViewModel.OcultarProgresoImpresion()        │
         │                                                          │
         ├── DispatcherHelper.ExecuteOnDispatcher(async () =>       │
         │                                                          │
         ├── lock(_modalLock):                                      │
         │   └── modalToClose = modal; modal = null                 │
         │                                                          │
         ├── if (modalToClose.IsVisible) → modalToClose.Close()     │
         │   (cierra CustomModalImprimiendoNewView)                 │
         │                                                          │
         ├── await Task.Delay(1000)                                 │
         │   (pausa entre cierre de modal imprimiendo y apertura    │
         │    de modal fallida)                                     │
         │                                                          │
         ├── if (FailedQueueManager.Instance.GetAll().Count > 0):   │
         │   ├── iniciarAnimacion() → campana en cabecera           │
         │   ├── vm = new ModaImpresionFallidaViewModel()           │
         │   ├── modal = new CustomModalImpresionFallidaNew(vm)     │
         │   ├── modal.Show() + posicionar                          │
         │   ├── await Task.Delay(5000) ← auto-cierre externo      │
         │   └── modal.Close()                                      │
         │                                                          │
         └── else: No hace nada (todo exitoso)                      │
         ════════════════════════════════════════════════════════════
```

### 12.3 Las 4 Listas Internas del PrinterQueueManager

Es crucial entender qué lista usa cada componente:

| Lista | Contenido | Quien la lee | Para qué |
|-------|-----------|--------------|----------|
| `_queue` | FIFO de comandos pendientes | Solo `ProcessQueueAsync` (dequeue) | Ejecutar secuencialmente |
| `_cola` | **Todos** los encolados en este ciclo | `ProgressService` vía `GetEnCola()` | Total de documentos para cálculo de progreso |
| `_completed` | Completados (éxito O fallo) | `ProgressService` vía `GetCompletadas()` | `pendientes = cola - completadas` |
| `_processing` | En ejecución actual | `ProgressService` vía `GetEnProceso()` | Info visual (no usado directamente en modales) |

**Ciclo de vida de un command en las listas:**
```
Enqueue()       → _queue ✅, _cola ✅
Dequeue()       → _queue ❌, _cola ✅, _processing ✅
ExecutePrint()  → _cola ✅, _processing ✅, _completed ✅
LimpiarProgreso → todas se vacían
```

### 12.4 Diferencia entre PrinterQueueManager y FailedQueueManager en relación a los modales

| Aspecto | PrinterQueueManager | FailedQueueManager |
|---------|--------------------|--------------------|
| **Tipo de cola** | `Queue<T>` (FIFO, un ciclo) | `ObservableCollection<T>` (persistente) |
| **Se limpia** | Sí, vía `LimpiarProgreso()` al terminar ciclo | Solo cuando se reintenta con éxito o se cancela manualmente |
| **Modal que lo consulta** | `CustomModalImprimiendoNewView` (vía `ProgressService`) | `CustomModalImpresionFallidaNew` (vía `ModaImpresionFallidaViewModel`) |
| **Eventos hacia UI** | `OpenProgress`, `HideProgress`, `ProgressChanged` | `CommandAdded`, `CommandRemoved` |
| **Thread de procesamiento** | `Task.Run(ProcessQueueAsync)` — Thread Pool | `RetryAllFailedAsync` — llamado desde UI, ejecuta en background |

---

## 13. Cadena Completa: Del Pedido al Modal

### 13.1 Punto de Origen: ¿Dónde se crean los IPrinterCommand?

Los modales no se crean "solos" — se activan cuando alguien encola un `IPrinterCommand` en el `PrinterQueueManager`. Estos son los **5 tipos de comandos** y **quién los crea**:

| Command | Archivo | Método de impresión | Origen |
|---------|---------|---------------------|--------|
| `PrintComandaCommand` | `PrinterManager/Commands/PrintComandaCommand.cs` | `PrintUtil.imprimirComandaAsync()` | Envío de pedidos a cocina/barra |
| `PrintComprobanteCommand` | `PrinterManager/Commands/PrintComprobanteCommand.cs` | `PrintUtil.ImprimirVentaAsync()` | Impresión de boletas/facturas |
| `PrintPrecuentaCommand` | `PrinterManager/Commands/PrintPrecuentaCommand.cs` | `PrintUtil.imprimirPrecuentaAsync()` | Impresión de pre-cuenta |
| `PrintEncuestaCommand` | `PrinterManager/Commands/PrintEncuestaCommand.cs` | `PrintUtil.ImprimirEncuestaAsync()` | Impresión de encuesta de satisfacción |
| `PrintPromocionCommand` | `PrinterManager/Commands/PrintPromocionCommand.cs` | `PrintUtil.ImprimirPromocionAsync()` | Impresión de cupones/promociones |

### 13.2 Todos los Commands tienen el mismo patrón

```csharp
public class PrintXxxCommand : IPrinterCommand
{
    private readonly Impresion _impresion;
    private Definitions.ESTADO_IMPRESION _estado;

    public PrintXxxCommand(Impresion impresion)
    {
        _impresion = impresion;
        Estado = ESTADO_IMPRESION.PENDIENTE;  // Estado inicial
    }

    public async Task ExecutePrintAsync()
    {
        Estado = ESTADO_IMPRESION.IMPRIMIENDO;   // Transición 1

        var respuesta = await PrintUtil.imprimirXxxAsync(_impresion);

        if (respuesta.Tipo == Util.SUCCESS)
            Estado = ESTADO_IMPRESION.EXITOSA;   // Transición 2a
        else
            Estado = ESTADO_IMPRESION.FALLIDA;   // Transición 2b
    }
}
```

### 13.3 Enum ESTADO_IMPRESION — Máquina de estados del comando

```csharp
public enum ESTADO_IMPRESION
{
    PENDIENTE   = 0,   // Recién creado, en cola
    IMPRIMIENDO = 1,   // ExecutePrintAsync en ejecución
    EXITOSA     = 2,   // PrintUtil retornó SUCCESS
    FALLIDA     = 3,   // PrintUtil retornó error
    CANCELADA   = 4    // Cancelado (no usado actualmente)
}
```

```
PENDIENTE ──► IMPRIMIENDO ──┬──► EXITOSA
                             │
                             └──► FALLIDA ──(reintento)──► IMPRIMIENDO ──┬──► EXITOSA
                                                                         └──► FALLIDA
```

### 13.4 La entidad `Impresion` — Datos que porta cada comando

**Archivo:** `QuipuNetX/entity/extras/Impresion.cs`

`Impresion` es un objeto pesado (~100 propiedades) que contiene **todo** lo necesario para imprimir un documento:

| Categoría | Propiedades clave |
|-----------|-------------------|
| **Impresora destino** | `Impresora` (nombre), `Impresora_ip`, `Impresora_modoimpresion`, `Impresora_macdeimpresora`, `Impresora_id`, `Tipoimpresora`, `Printermodel` |
| **Contenido** | `Cadena` (texto raw), `CadenaHTML`, `Cuerpo1`-`Cuerpo4` (secciones), `lineasimprimir` (List\<Linea\>) |
| **Contexto del pedido** | `Area` (producción), `Sala`, `Mesa`, `Mozo`, `Cliente`, `Cantpax`, `Hora` |
| **Tipo de documento** | `Tipo`, `TipoComanda`, `Formato`, `Tipoimpresion` |
| **Configuración corte/gaveta** | `Codigocorte`, `Cashdrawer`, `Abregaveta`, `Numcaracteres` |
| **Facturación electrónica** | `FacturacionElectronica`, `QrData`, `Numeroresolucionsunat`, `Codigobarras` |
| **Identificadores** | `Uniqueid`, `NumeroTicket`, `NumeroPuntos`, `Pedido_id`, `Venta_checksum` |
| **Impresión móvil** | `Areaproduccion_ipimpresion`, `Modoimpresion`, `ImpresionDirectaRed`, `ImpresoraIP` |

### 13.5 Quiénes llaman a Enqueue (Callers concretos)

**A. Desde `ComandasPrintStrategy` (flujo con cola):**
```csharp
// QuipuNet/Services/Print/Strategies/ComandasPrintStrategy.cs
if (adapter.UsarColaImpresion())  // ← ConfiguracionImpresionAdapter
{
    foreach (var impresion in context.ImpresionComandas)
    {
        PrinterQueueManager.Instance.Enqueue(new PrintComandaCommand(impresion));
    }
}
```

**B. Desde `VentaRapidaPrintStrategy` (venta rápida):**
```csharp
// QuipuNet/Services/Print/Strategies/VentaRapidaPrintStrategy.cs
foreach (Impresion impresion in context.ImpresionComandas)
{
    PrinterQueueManager.Instance.Enqueue(new PrintComandaCommand(impresion));
}
```

**C. Desde `FragmentDetallePedidoPresenter` (flujo legacy directo, 5 puntos):**
```csharp
// QuipuNet/MainApp/venta/Pedidos/FragmentDetallePedidoPresenter.cs
foreach (Impresion impresion in respuesta.ImpresionList)
{
    PrinterQueueManager.Instance.Enqueue(new PrintComandaCommand(impresion));
}
```

**D. Desde `OrdenpedidoController` (flujo backend/ordenpedido):**
```csharp
// QuipuNetX/controller/OrdenpedidoController.cs
PrinterQueueManager.Instance.Enqueue(new PrintComandaCommand(impresion));
```

### 13.6 Decisión: Cola vs Directo — `ConfiguracionImpresionAdapter`

**Archivo:** `QuipuNet/Services/Print/ConfiguracionImpresionAdapter.cs`

```csharp
public bool UsarColaImpresion()
{
    return Util.estaConfiguracionActivadaPOS3(
        Definitions.CONFIGURACIONPOS_UTILIZAR_COLA_IMPRESIONES);
}
```

- Si está **activada** → se usa `PrinterQueueManager.Enqueue()` → se muestran los modales
- Si está **desactivada** → se imprime directamente vía `PrintUtil.imprimirComandasEthernet()` → **NO se muestran modales** (resultado via callback)

**Esto significa que los modales SOLO aparecen cuando la configuración POS `UTILIZAR_COLA_IMPRESIONES` está activada.**

---

## 14. Sistema de Animación y Badge de Cabecera

### 14.1 Vista general

Cuando hay impresiones fallidas, además de los modales toast, la cabecera del POS muestra una **animación visual** (campana/badge) para alertar al usuario. Este sistema funciona en paralelo a los modales.

### 14.2 Interfaz IAnimationController

**Archivo:** `QuipuNet/MainApp/NuevoDisenioQuipunet/ManagerImpresiones/IAnimationController.cs`

```csharp
public interface IAnimationController
{
    void IniciarAnimacionImpresion();
    void DetenerAnimacionImpresion();
}
```

Las vistas que implementan esta interfaz (pantallas que tienen un icono de campana/badge) se registran en `UtilesCabeceraViewModel` para recibir notificaciones de inicio/fin de animación.

### 14.3 Patrón Observer para animaciones

```csharp
// UtilesCabeceraViewModel
private readonly List<IAnimationController> _animationControllers = new List<IAnimationController>();
private bool _animacionIniciada;

// Registro (lo hace cada vista al cargarse)
public void RegisterAnimationController(IAnimationController controller)
{
    _animationControllers.Add(controller);
    if (_animacionIniciada)
        controller.IniciarAnimacionImpresion(); // Sincronizar si ya hay errores
}

// Desregistro (lo hace cada vista al descargarse)
public void UnregisterAnimationController(IAnimationController controller)
{
    _animationControllers.Remove(controller);
}
```

### 14.4 Cuándo se inicia y detiene la animación

```
iniciarAnimacion() se llama cuando:
├── AddFailedPrint(cmd)              ← FailedQueueManager.CommandAdded
│   (si ImpresionesFallidas.Count > 0)
│
└── OcultarProgresoImpresion()       ← PrinterQueueManager.HideProgress
    (si FailedQueueManager tiene errores)

detenerAnimacion() se llama cuando:
└── RemoveFailedPrint(cmd)           ← FailedQueueManager.CommandRemoved
    (si ImpresionesFallidas.Count <= 0)
```

### 14.5 La colección `ImpresionesFallidas`

```csharp
// UtilesCabeceraViewModel
private ObservableCollection<PrinterCommandViewModel> _impresionesFallidas;
public ObservableCollection<PrinterCommandViewModel> ImpresionesFallidas { get; set; }
public int ContImpresiones { get; set; }  // Badge count
public bool HasFailedPrints => ImpresionesFallidas?.Count > 0;
public bool ShowEmptyImpresiones => !HasFailedPrints;
```

Esta colección mantiene un **espejo** del `FailedQueueManager` en forma de `PrinterCommandViewModel`, permitiendo:
- Mostrar la lista en un panel lateral/dropdown de la cabecera
- Cada item con su propio botón Reintentar/Cancelar individual
- Badge numérico con `ContImpresiones`

### 14.6 Relación entre animación y modales

```
FailedQueueManager.Add(cmd)
       │
       ├──► CommandAdded event
       │    │
       │    └──► UtilesCabeceraViewModel.AddFailedPrint()
       │         ├── ImpresionesFallidas.Add(vm)  ← Lista en cabecera
       │         ├── ContImpresiones++             ← Badge número
       │         └── iniciarAnimacion()            ← Campana animada
       │
       └──► (Más tarde) HideProgress event
            └──► OcultarProgresoImpresion()
                 ├── Cierra modal "Imprimiendo"
                 └── Abre modal "Fallida" (si hay errores)
```

**Los dos sistemas (animación + modal) se activan por las mismas fuentes pero son independientes:**
- La animación/badge persiste hasta que se resuelvan TODOS los errores
- El modal fallida se cierra automáticamente tras 5 segundos
- El usuario puede reabrir el modal desde los callers manuales (`showFailedPrinterService`, `showModalErroresImpresiones`)

---

## 15. PrinterCommandViewModel — Reintento Individual

### 15.1 Descripción

**Archivo:** `QuipuNet/MainApp/NuevoDisenioQuipunet/ManagerImpresiones/PrinterCommandViewModel.cs`

Wrapper ViewModel que envuelve un `IPrinterCommand` para mostrar en la lista de impresiones fallidas de la cabecera. Permite **reintento individual** (a diferencia del modal fallida que reintenta todos).

### 15.2 Propiedades expuestas

| Propiedad | Tipo | Fuente |
|-----------|------|--------|
| `AreaProduccion` | `string` | `Command.GetImpresion.Area` |
| `Salon` | `string` | `Command.GetImpresion.Salon` |
| `Mesa` | `string` | `Command.GetImpresion.Mesa` |
| `ImpresoraNombre` | `string` | `Command.GetImpresion.Impresora` |
| `Fecha` | `string` | `Command.GetImpresion.FechaImpresion` |
| `Hora` | `string` | `Command.GetImpresion.HoraImpresion` |
| `Estado` | `ESTADO_IMPRESION` | `Command.Estado` (reactivo) |
| `EstadoTexto` | `string` | "Pendiente" / "En proceso" / "Completado" / "Error" |
| `MostrarComandos` | `bool` | `true` si no está IMPRIMIENDO |
| `MostrarProgreso` | `Visibility` | `Visible` si IMPRIMIENDO, `Collapsed` si no |
| `Progreso` | `double` | 0-100 (timer local de 2s) |
| `TiempoTranscurrido` | `string` | "MM:SS" |
| `StyleEstadoTexto` | `Style` | WPF style dinámico por estado |
| `BackgroundEstadoTexto` | `SolidColorBrush` | Color de fondo badge por estado |
| `EstadoItem` | `SolidColorBrush` | Borde del item por estado |

### 15.3 Commands de reintento individual

```csharp
ReintentarCommand = new RelayCommand(() =>
{
    IniciarProgressBar();                               // Barra local de 2s
    OnPropertyChanged("MostrarProgreso");               // Hace visible la barra
    FailedQueueManager.Instance.RetrySingleFailedAsync(command);  // Reintenta SOLO este
});

CancelarCommand = new RelayCommand(() =>
{
    FailedQueueManager.Instance.RemoveFailed(command);  // Lo quita de la lista
});
```

### 15.4 Propagación reactiva de Estado

```csharp
// En el constructor
if (Command is INotifyPropertyChanged npc)
{
    npc.PropertyChanged += (s, e) =>
    {
        if (e.PropertyName == nameof(IPrinterCommand.Estado))
        {
            OnPropertyChanged("Estado");
            OnPropertyChanged("EstadoTexto");
            OnPropertyChanged("StyleEstadoTexto");
            OnPropertyChanged("BackgroundEstadoTexto");
            OnPropertyChanged("EstadoItem");
            OnPropertyChanged("MostrarComandos");
            OnPropertyChanged("MostrarProgreso");
        }
    };
}
```

Cuando `FailedQueueManager.RetrySingleFailedAsync(command)` ejecuta `command.ExecutePrintAsync()`:
1. Command setea `Estado = IMPRIMIENDO` → PropertyChanged
2. ViewModel actualiza todas las propiedades visuales (badge azul, "En proceso", barra visible)
3. Command setea `Estado = EXITOSA` o `FALLIDA` → PropertyChanged
4. ViewModel actualiza (badge verde/rojo, "Completado"/"Error", barra se oculta)
5. Si EXITOSA → `FailedQueueManager.RemoveFailed(command)` → `CommandRemoved` event → se quita de la lista

### 15.5 Timer de ProgressBar individual

A diferencia del `ProgressService` (3s por doc) y `ModaImpresionFallidaViewModel` (3s por error), el `PrinterCommandViewModel` usa **2 segundos** de duración:

```csharp
private int duracionImpresion = 2000; // 2s por reintento individual
private bool _progresoEnCurso = false; // Protección contra doble-click

private void IniciarProgressBar()
{
    if (_progresoEnCurso) return; // Ya hay una en curso
    _progresoEnCurso = true;

    // ... timer 16ms ...

    // Completar cuando: tiempo >= 2s Y estado ya no es IMPRIMIENDO
    if (msTranscurridos >= duracionImpresion &&
        (Command.Estado == EXITOSA || Command.Estado == FALLIDA))
    {
        Progreso = 100;
        _timer.Stop();
        _progresoEnCurso = false;
    }
}
```

**Diferencia clave:** La barra del reintento individual espera **tanto** el tiempo estimado **como** la finalización real del comando (`Estado != IMPRIMIENDO`). Esto evita que la barra llegue al 100% mientras aún se imprime.
