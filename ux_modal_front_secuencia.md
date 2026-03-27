# Secuencia UX de Modales de Impresión — PrinterServices

> **Versión:** 1.0
> **Aplica a:** Cualquier flujo del Front que delegue impresión a PrinterServices
> **Referencia técnica:** `estructura_modalfront.md`, `vibe_qn_client_mode.md`

---

## 1. Diagrama de Secuencia

```
Usuario ejecuta acción (enviar pedido, liberar mesa, anular, etc.)
    │
    ▼
Backend delega a PrinterServices (flag USAR_PRINTER_SERVICE ON)
    │
    ▼
Respuesta llega al Front con PrintJobResults
    │
    ├─ PrintJobResults tiene jobs? ──── NO ──→ [Modal Error] ──→ Navegar
    │
    └─ SÍ
       │
       ▼
┌─────────────────────────────────────────────────┐
│  MODAL PROGRESO (CustomModalImprimiendoNewView) │
│                                                  │
│  "Enviando X de Y documento(s)"                 │
│  [═══════════════░░░░░░░] 60%                   │
│                                                  │
│  Mínimo visible: 1800ms (MIN_DISPLAY_MS)        │
│  PSProgressService escucha callbacks reales      │
│  de PrinterServices via PrintJobStatusManager    │
└─────────────────────────────────────────────────┘
    │
    ▼
Completado(huboErrores) se dispara
    │
    ├─ Cerrar modal progreso
    ├─ Esperar 500ms (transición visual)
    │
    ├─── huboErrores = false ────────────────────────────────┐
    │                                                         │
    │    ┌──────────────────────────────────────────┐         │
    │    │  MODAL ÉXITO (CustomModalImpresionExitosa)│        │
    │    │                                           │        │
    │    │  ✓ ¡Impresión exitosa!                   │        │
    │    │                                           │        │
    │    │  Auto-cierre: 3 segundos                 │        │
    │    │  Posición: esquina inferior derecha       │        │
    │    └──────────────────────────────────────────┘         │
    │         │                                               │
    │         ▼ (después de 3s)                               │
    │    Navegar (showSuccessRegister / showSuccessLiberarMesa)│
    │                                                         │
    └─── huboErrores = true ─────────────────────────────────┐
         │                                                    │
         │    ┌──────────────────────────────────────────┐    │
         │    │  MODAL ERROR (CustomModalImpresionFallida)│   │
         │    │                                           │   │
         │    │  ⚠ Error de impresión                    │   │
         │    │  X documento(s) fallido(s)               │   │
         │    │                                           │   │
         │    │  [Reintentar]  [Cancelar]                │   │
         │    │                                           │   │
         │    │  Reactivo: PSModaImpresionFallidaViewModel│   │
         │    │  Escucha PrintJobStatusManager en tiempo  │   │
         │    │  real para actualizar conteos             │   │
         │    └──────────────────────────────────────────┘    │
         │         │                                          │
         │         ▼                                          │
         │    Navegar (la operación fue exitosa,              │
         │    solo la impresión falló — el modal              │
         │    queda flotando para retry)                      │
         │                                                    │
         └────────────────────────────────────────────────────┘
```

---

## 2. Tiempos y Garantías

| Fase | Duración | Garantía |
|---|---|---|
| Modal progreso | ≥ 1800ms | `PSProgressService.MIN_DISPLAY_MS` asegura visibilidad mínima aunque PS responda en <5ms |
| Transición progreso → resultado | 500ms | `DispatcherTimer` entre cerrar progreso y abrir éxito/error |
| Modal éxito (toast verde) | 3000ms | Auto-cierre con `DispatcherTimer`. La navegación ocurre DESPUÉS del cierre |
| Modal error | Indefinido | El usuario decide: Reintentar o Cancelar. Se auto-cierra 5s después de retry exitoso |

### Timeline mínimo percibido por el usuario

```
t=0.0s   Modal progreso aparece
t=1.8s   Modal progreso se cierra (mínimo, puede ser más si PS tarda)
t=2.3s   Modal éxito/error aparece (500ms transición)
t=5.3s   Modal éxito se cierra (3s) → navegación
         Total mínimo: ~5.3 segundos de feedback visual
```

---

## 3. Reglas Críticas

### 3.1 Navegación SIEMPRE después del modal

```
❌ INCORRECTO (navegación mata los modales):
    onSuccess: () => {
        showSuccessLiberarMesa();     // Navega inmediatamente → destruye vista
        showSuccessPrinterService();  // Nunca se muestra
    }

✅ CORRECTO (navegación después del toast):
    onSuccess: () => {
        showSuccessPrinterServiceConNavegacion(() => {
            showSuccessLiberarMesa();  // Se ejecuta al cerrar el toast (3s después)
        });
    }
```

### 3.2 Dispatcher obligatorio

Todos los métodos que crean ventanas WPF **deben** ejecutar en UI thread:

```csharp
// ⚠️ El Presenter llama desde hilo de fondo (callback REST)
Application.Current.Dispatcher.BeginInvoke(new Action(() =>
{
    // Crear y mostrar modal aquí
}));
```

### 3.3 cantidadJobs obligatorio

```csharp
// ⚠️ Sin cantidadJobs, PSProgressService lee PrintJobStatusManager.Count
// que puede ser 0 si los callbacks DONE llegaron antes de crear el servicio
var psProgress = new PSProgressService(cantidadJobs);  // ← SIEMPRE pasar
```

### 3.4 Posicionamiento consistente

Todos los modales de impresión se posicionan en **esquina inferior derecha**:

```csharp
var desktopWorkingArea = SystemParameters.WorkArea;
modal.Left = desktopWorkingArea.Right - modal.Width - 40;
modal.Top = desktopWorkingArea.Bottom - modal.Height - 25;
```

---

## 4. Patrón de Implementación en el Presenter

```csharp
private void verificarPrintJobs_[Flujo](Respuesta respuesta)
{
    try
    {
        bool tieneJobs = respuesta.PrintJobResults != null
                      && respuesta.PrintJobResults.Count > 0;

        if (tieneJobs)
        {
            view.showImprimiendoPrinterService(
                onSuccess: () =>
                {
                    if (view == null) return;
                    // Toast verde → navegar DESPUÉS de que se cierre
                    view.showSuccessPrinterServiceConNavegacion(() =>
                    {
                        if (view != null) view.navegarAlExito();
                    });
                },
                onFail: () =>
                {
                    if (view == null) return;
                    view.showFailedPrinterService();  // Modal error (flotante)
                    view.navegarAlExito();             // Navegar (operación sí fue exitosa)
                },
                cantidadJobs: respuesta.PrintJobResults.Count
            );
        }
        else
        {
            // PS no respondió — error + navegar
            view.showFailedPrinterService();
            view.navegarAlExito();
        }
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Error verificando PrintJobResults");
        if (view != null) view.mostrarError("PrinterService no está activo");
    }
}
```

---

## 5. Patrón de Implementación en la View

### 5.1 showImprimiendoPrinterService

```csharp
public void showImprimiendoPrinterService(Action onSuccess, Action onFail, int cantidadJobs = 0)
{
    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
    {
        var psProgress = new PSProgressService(cantidadJobs);
        var modal = new CustomModalImprimiendoNewView(psProgress);

        psProgress.Completado += (huboErrores) =>
        {
            try
            {
                if (modal != null && modal.IsLoaded) modal.Close();

                // 500ms transición antes de mostrar resultado
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    if (huboErrores) onFail?.Invoke();
                    else onSuccess?.Invoke();
                };
                timer.Start();
            }
            catch (Exception ex) { Log.Error(ex); }
        };

        modal.Show();
        // Posicionar bottom-right
        var wa = SystemParameters.WorkArea;
        modal.Left = wa.Right - modal.Width - 40;
        modal.Top = wa.Bottom - modal.Height - 25;
    }));
}
```

### 5.2 showSuccessPrinterServiceConNavegacion

```csharp
public void showSuccessPrinterServiceConNavegacion(Action onClosed)
{
    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
    {
        var modal = new CustomModalImpresionExitosaNew();
        modal.Show();
        // Posicionar bottom-right
        var wa = SystemParameters.WorkArea;
        modal.Left = wa.Right - modal.Width - 40;
        modal.Top = wa.Bottom - modal.Height - 25;

        // Auto-cerrar en 3 segundos → LUEGO navegar
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            try { if (modal.IsLoaded) modal.Close(); } catch { }
            onClosed?.Invoke();  // Navegar aquí, no antes
        };
        timer.Start();
    }));
}
```

### 5.3 showSuccessPrinterService (sin navegación)

```csharp
public void showSuccessPrinterService()
{
    showSuccessPrinterServiceConNavegacion(null);  // Toast sin callback
}
```

### 5.4 showFailedPrinterService

```csharp
public void showFailedPrinterService()
{
    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
    {
        if (!CustomModalImpresionFallidaNew.PuedeAbrir()) return;  // Guard: no duplicar
        var viewModel = new PSModaImpresionFallidaViewModel();
        var modal = new CustomModalImpresionFallidaNew(viewModel);
        modal.Show();
        // Posicionar bottom-right
        var wa = SystemParameters.WorkArea;
        modal.Left = wa.Right - modal.Width - 40;
        modal.Top = wa.Bottom - modal.Height - 25;
    }));
}
```

---

## 6. Decisión en el Presenter (antes de los modales)

```csharp
if (respuesta.ImpresionList != null && respuesta.ImpresionList.Count > 0)
{
    // Impresión LOCAL (flag OFF o fallback)
    imprimirComandas(respuesta.ImpresionList);
    navegarAlExito();
}
else if (FeatureFlagManager.Instance.IsEnabled("USAR_PRINTER_SERVICE")
    || (Util.esModoCliente() && UtilFront.FEATURE_FLAG_FROM_SERVER_PRINTER_SERVICES))
{
    // PrinterServices delegó — verificar jobs y mostrar modales
    verificarPrintJobs(respuesta);
}
else
{
    // Sin impresiones, sin flag — nada que imprimir
    navegarAlExito();
}
```

---

## 7. Flujos Implementados

| Flujo | Archivo Presenter | Archivo View | Estado |
|---|---|---|---|
| Enviar pedido (addLista) | `ListaPedidosTemporalesPresenter.cs` | `ListaPedidosTemporales.cs` | ✅ Referencia |
| Modificar cantidad | `FragmentDetallePedidoPresenter.cs` | `FragmentDetallePedido.cs` | ✅ |
| Liberar mesa | `FragmentDetalleMesaPresenter.cs` | `FragmentDetalleMesa.cs` | ✅ |
| Anular pedido delivery | `DeliveryController.cs` (backend directo) | — | ✅ Backend |
| Reimprimir comanda | `FragmentDetallePedidoPresenter.cs` | `FragmentDetallePedido.cs` | ✅ |

---

## 8. Anti-patrones

| Anti-patrón | Problema | Solución |
|---|---|---|
| Navegar antes del toast | El toast nunca se muestra | Usar `showSuccessPrinterServiceConNavegacion(callback)` |
| No pasar `cantidadJobs` | Race condition: PSProgressService lee 0 jobs | Siempre pasar `respuesta.PrintJobResults.Count` |
| Crear modal sin Dispatcher | `InvalidOperationException` en WPF | Siempre envolver en `Dispatcher.BeginInvoke` |
| No verificar `view == null` en callbacks | `NullReferenceException` si la vista se destruyó | Guard `if (view == null) return;` en cada callback |
| Llamar `showSuccessRegister` antes de `showSuccessPrinterService` | Navegación destruye el contexto | Éxito primero, navegar después |
| No usar `PuedeAbrir()` en modal error | Modales duplicados superpuestos | `if (!CustomModalImpresionFallidaNew.PuedeAbrir()) return;` |
