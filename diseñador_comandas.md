# Diseñador Visual de Comandas — Documentacion Tecnica

## Problema Original

El sistema tiene dos servicios de impresion que producen tickets con formato visual diferente:

- **Servicio antiguo (PrinterITDAST)**: Recibe campos individuales (mesa, mozo, area, productos) y construye el ticket internamente con `CreaTicket`. Renderiza con fuentes GDI (Arial Bold, Lucida Console) via `PrintDocument`. Resultado: tipografia grafica, tamaños variables, estilo profesional.

- **Servicio nuevo (PrinterServices LCA)**: Recibe una cadena pre-armada desde QuipuNetX y la envia como texto plano ESC/POS. La impresora usa su fuente interna monoespaciada. Resultado: texto plano uniforme, estilo consola.

Los clientes que migraron del servicio antiguo al nuevo notan la diferencia visual y quieren mantener el formato anterior.

---

## Solucion: Feature Flag + ITipoDocumento + Diseñador Visual

En lugar de hard-codear un formato fijo, se implemento un sistema completo que permite:

1. **Activar el formato antiguo** con un feature flag (sin romper el flujo existente)
2. **Personalizar el formato** con un diseñador visual tipo Crystal Reports
3. **Extender a otros tipos de documento** (ventas, precuentas) sin modificar PrintJob ni PrintWorker

---

## Arquitectura

```
┌─────────────────────────────────────────────────────────────────┐
│  FRONT (QuipuNet WPF)                                           │
│                                                                  │
│  FeatureFlagsWindow → Pestaña Impresion                          │
│    ├─ Toggle: "Usar Formato Antiguo" (FORMATO_ANTIGUO_SERVICIO)  │
│    └─ Boton: "Abrir Diseñador" → ComandaDesignerWindow           │
│                                                                  │
│  ComandaDesignerWindow (3 paneles)                               │
│    ├─ Toolbox: campos disponibles (doble clic para agregar)      │
│    ├─ Papel: preview con fuentes reales, reordenar, eliminar     │
│    └─ Propiedades: fuente, tamaño, bold, italic, alineacion      │
│    [Guardar] → template_comanda.txt (JSON)                       │
└──────────────────────┬──────────────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────────────┐
│  template_comanda.txt (%LOCALAPPDATA%\QuipuNet\)                │
│  JSON: { paperWidth, fields: [{fieldName, label, fontFamily,     │
│          fontSize, fontBold, fontItalic, alignment, visible}] }   │
└──────────────────────┬──────────────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────────────┐
│  QUIPU (QuipuNetX Backend)                                       │
│                                                                  │
│  PrinterServiceClient.EnviarComandasAsync()                      │
│    ├─ json["formato_antiguo_servicio"] = FeatureFlag activo?     │
│    ├─ json["cadenaHTML"] = HTML de ImpresionController            │
│    └─ json["mesa"], json["mozo"], json["area"], etc.             │
│       (campos individuales para ComandaDocument)                  │
└──────────────────────┬──────────────────────────────────────────┘
                       │ POST /api/print/comandas
                       ▼
┌─────────────────────────────────────────────────────────────────┐
│  PRINTERSERVICES                                                 │
│                                                                  │
│  PrintController.ParsePrintJob()                                 │
│    └─ Si FormatoAntiguoServicio = true:                          │
│       TipoDocumentoFactory.Create(json, "comanda")               │
│       → ComandaDocument con campos individuales + ProductosHtml   │
│       → job.Documento = ComandaDocument                           │
│                                                                  │
│  PrintWorker.BuildPayload()                                      │
│    └─ Si job.Documento != null:                                  │
│       html = job.Documento.GenerarHtml()                         │
│         ├─ ¿Existe template? → genera HTML segun template        │
│         └─ ¿No existe?       → genera HTML hard-coded (default)  │
│       ComandaBitmapRenderer.RenderAsBitmap(html)                 │
│       → Bitmap → ESC/POS raster → Impresora                      │
└─────────────────────────────────────────────────────────────────┘
```

---

## Por que un Feature Flag

El formato antiguo NO es para todos los clientes. Muchos prefieren el texto plano ESC/POS porque es mas rapido de imprimir (no hay rendering de bitmap). El feature flag `FORMATO_ANTIGUO_SERVICIO` permite:

- **Activar/desactivar** desde la UI sin recompilar
- **No interferir** con el flujo existente (si esta OFF, todo funciona igual que antes)
- **Persistir** en `config_fla.cfg` (sobrevive reinicios)
- **Propagarse** automaticamente al JSON de impresion via `FeatureFlagConfigReader`

---

## Por que ITipoDocumento (Patron Factory + Strategy)

### El problema de meter campos en PrintJob

PrintJob es un documento de impresion **generico** — puede ser comanda, venta, precuenta, egreso, etc. Si agregamos campos de comanda (Mesa, Mozo, Salon) directamente a PrintJob, rompemos el Principio de Responsabilidad Unica (SRP): PrintJob sabria sobre comandas, ventas, egresos, todo al mismo tiempo.

### La solucion

```
ITipoDocumento (interfaz)
  └─ GenerarHtml(): string    ← unico metodo, minimo necesario (ISP)

ComandaDocument : ITipoDocumento
  ├─ Operacion, Serie, Mesa, Mozo, Area, Hora, Salon, Cliente
  ├─ ProductosHtml
  └─ GenerarHtml() → HTML con estructura CreaTicket

VentaDocument : ITipoDocumento  (futuro)
  ├─ RazonSocial, RUC, Direccion, Productos, Totales
  └─ GenerarHtml() → HTML con estructura de boleta/factura

PrintJob
  └─ Documento: ITipoDocumento  ← propiedad opcional, no se persiste en BD
```

**Principios SOLID aplicados:**

| Principio | Como se aplica |
|---|---|
| **SRP** | ComandaDocument solo sabe armar HTML de comanda. PrintJob no sabe nada de comandas. |
| **OCP** | Para agregar VentaDocument, solo se crea la clase + un case en TipoDocumentoFactory. No se modifica PrintJob ni PrintWorker. |
| **LSP** | Cualquier ITipoDocumento es intercambiable — PrintWorker solo llama GenerarHtml(). |
| **ISP** | ITipoDocumento tiene un solo metodo — el minimo que PrintWorker necesita. |
| **DIP** | PrintWorker depende de la abstraccion ITipoDocumento, no de ComandaDocument. |

### TipoDocumentoFactory

```csharp
// PrintController.ParsePrintJob():
if (job.FormatoAntiguoServicio)
{
    job.Documento = TipoDocumentoFactory.Create(json, tipo);
}

// TipoDocumentoFactory.Create():
switch (tipoImpresion)
{
    case "comanda":  return CreateComandaDocument(json);
    // case "venta": return CreateVentaDocument(json);  ← futuro
}
```

El Factory extrae los campos individuales del JSON y crea la implementacion correcta. Los campos viajan en el JSON pero solo los usa el Factory — PrintJob nunca los ve.

---

## Por que un Diseñador Visual

### El problema del hard-code

El orden y estilo de la cabecera estaba hard-coded en `ComandaDocument.GenerarHtml()`. Si un cliente quiere la Operacion despues de la Mesa, o el Mozo en negrita, hay que:
1. Modificar el codigo
2. Recompilar
3. Reinstalar en TODOS los locales

### La solucion: template externo

`ComandaDocument.GenerarHtml()` busca un archivo `template_comanda.txt` en `%LOCALAPPDATA%\QuipuNet\`. Si existe, usa ese template para determinar:
- **Orden** de los campos (el usuario los arrastra para reordenar)
- **Label/prefijo** de cada campo ("MESA:", "Mesa:", "TABLE:", etc.)
- **Fuente** (Arial, Lucida Console, Consolas, Courier New)
- **Tamaño** (9, 11, 13, 15, 17, 19 pt)
- **Estilo** (bold, italic)
- **Alineacion** (left, center, right)
- **Visibilidad** (incluir u omitir un campo)

Si el template **no existe**, usa el formato hard-coded por defecto (compatible con CreaTicket).

### Formato del template (JSON)

```json
{
  "paperWidth": 80,
  "fields": [
    {
      "fieldName": "Operacion",
      "label": "Operación:",
      "fontFamily": "Arial",
      "fontSize": 13,
      "fontBold": true,
      "fontItalic": false,
      "alignment": "Left",
      "visible": true
    },
    {
      "fieldName": "Serie",
      "label": "N°:",
      "fontFamily": "Arial",
      "fontSize": 13,
      "fontBold": true,
      "fontItalic": false,
      "alignment": "Left",
      "visible": true
    },
    {
      "fieldName": "Mesa",
      "label": "MESA:",
      "fontFamily": "Lucida Console",
      "fontSize": 11,
      "fontBold": false,
      "fontItalic": false,
      "alignment": "Left",
      "visible": true
    },
    ...
    {
      "fieldName": "EmptyLine",
      "label": "",
      "fontFamily": "Arial",
      "fontSize": 11,
      "fontBold": false,
      "fontItalic": false,
      "alignment": "Left",
      "visible": true
    },
    {
      "fieldName": "ProductosHtml",
      "label": "",
      "fontFamily": "Arial",
      "fontSize": 13,
      "fontBold": true,
      "fontItalic": false,
      "alignment": "Left",
      "visible": true
    }
  ]
}
```

### Campos disponibles

| FieldName | Descripcion | Fuente default | Tamaño default |
|---|---|---|---|
| `Operacion` | Numero de trazabilidad de la operacion | Arial Bold | 13pt |
| `Serie` | Numero de serie de la comanda | Arial Bold | 13pt |
| `TipoImpresion` | ** AGREGADO ** / ** RE-IMPRESION ** / ** ANULACION ** | Arial Bold | 13pt |
| `Modalidad` | DELIVERY, VENTA RAPIDA, etc. | Arial Bold | 17pt |
| `Mesa` | Mesa + cantidad de personas (Pax) | Lucida Console | 11pt |
| `CantPax` | Cantidad de personas (separado) | Lucida Console | 11pt |
| `Area` | Area de produccion (COCINA, BARRA, etc.) | Lucida Console | 11pt |
| `Hora` | Fecha y hora de la comanda | Lucida Console | 11pt |
| `Mozo` | Nombre del mozo/mesero | Lucida Console | 11pt |
| `Salon` | Sala o salon | Lucida Console | 11pt |
| `Cliente` | Nombre del cliente | Lucida Console | 11pt |
| `ProductosHtml` | Detalle de productos (viene del HTML de ImpresionController) | Arial Bold | 13pt |
| `Separator` | Linea separadora (────────) | Arial Bold | 11pt |
| `EmptyLine` | Linea vacia (espacio) | — | 11pt |

### Campos especiales

- **ProductosHtml**: No se genera desde una propiedad — se inyecta directamente el HTML de productos que viene de ImpresionController. Incluye productos, notas, sub-productos de combo, secciones "PARA LLEVAR", etc.
- **Separator**: Genera una linea de separacion. El texto se configura en `label`.
- **EmptyLine**: Genera un `<br>` (espacio vertical).
- **TipoImpresion**: Se resuelve automaticamente a "** AGREGADO **", "** RE-IMPRESION **" o "** ANULACION **" segun el valor que envie QuipuNetX.

---

## Como se mapean fuentes del template a HTML

ComandaBitmapRenderer parsea tags HTML para decidir la fuente. ComandaDocument genera esos tags segun la config del template:

| Config del template | Tags HTML generados | Fuente que usa ComandaBitmapRenderer |
|---|---|---|
| FontSize >= 15 | `<h2>` | Arial {size}pt |
| FontSize >= 12 | `<h3>` | Arial {size}pt |
| FontSize < 12 | `<h4>` | Lucida Console {size}pt |
| FontFamily = "Lucida Console" | `<h4>` (forzado) | Lucida Console {size}pt |
| FontBold = true | `<b>` | Bold |
| FontItalic = true | `<i>` | Italic |

Esto permite que el diseñador configure visualmente las fuentes y el resultado impreso sea consistente.

---

## Diseñador Visual (ComandaDesignerWindow)

### Layout de 3 paneles

```
┌──────────────┐ ┌─────────────────────┐ ┌──────────────────┐
│  TOOLBOX     │ │  PAPEL PREVIEW      │ │  PROPIEDADES     │
│              │ │                     │ │                  │
│ [Operacion]  │ │ ┌─────────────────┐ │ │ Campo: Mesa      │
│ [Serie]      │ │ │ Operación: [v]  │ │ │                  │
│ [Mesa]       │ │ │ N°: [valor]     │ │ │ Etiqueta: MESA:  │
│ [Area]       │ │ │ MESA: [valor]   │ │ │ Fuente: [Lucida] │
│ [Hora]       │ │ │ AREA: [valor]   │ │ │ Tamaño: [11]     │
│ [Mozo]       │ │ │ MOZO: [valor]   │ │ │ [x] Negrita      │
│ [Salon]      │ │ │ SALA: [valor]   │ │ │ [ ] Cursiva      │
│ [Cliente]    │ │ │                 │ │ │ Alineacion:[Left]│
│ [Productos]  │ │ │ (Productos)     │ │ │ [x] Visible      │
│ [Separador]  │ │ └─────────────────┘ │ │                  │
│ [Linea Vacia]│ │                     │ │ [▲ Subir]        │
│              │ │ Papel: (●)80 (○)58  │ │ [▼ Bajar]        │
│ Doble clic   │ │ [Guardar] [Reset]   │ │                  │
│ para agregar │ │                     │ │                  │
└──────────────┘ └─────────────────────┘ └──────────────────┘
```

### Interacciones

| Accion | Como |
|---|---|
| Agregar campo al papel | Doble clic en el toolbox |
| Reordenar campos | Drag-and-drop en el papel, o botones ▲▼ en propiedades |
| Eliminar campo | Boton ✕ en el campo, o tecla Delete |
| Editar propiedades | Clic en un campo del papel → panel derecho |
| Cambiar ancho de papel | RadioButton 80mm / 58mm |
| Guardar template | Boton "Guardar" → template_comanda.txt |
| Restablecer default | Boton "Restablecer" → formato CreaTicket original |

### Preview en tiempo real

El papel muestra cada campo con la fuente, tamaño y estilo configurados en tiempo real. Si un campo tiene `visible = false`, se muestra con opacidad reducida (40%).

---

## Flujo completo: Desde el diseño hasta la impresion

```
1. DISEÑO (una sola vez por local)
   Usuario abre Feature Flags → Impresion → Abrir Diseñador
   Arrastra campos, configura fuentes/orden
   [Guardar] → template_comanda.txt en %LOCALAPPDATA%\QuipuNet\

2. ACTIVACION (una sola vez)
   Toggle "Usar Formato Antiguo" → ON
   → FORMATO_ANTIGUO_SERVICIO=true en config_fla.cfg

3. IMPRESION (cada vez que se agrega un pedido)
   Front: addPedido → QuipuNetX genera impresionList
   QuipuNetX: PrinterServiceClient envia JSON con:
     - formato_antiguo_servicio: true
     - cadenaHTML (productos)
     - campos individuales (mesa, mozo, area, etc.)

4. PROCESAMIENTO (PrinterServices)
   PrintController: ParsePrintJob → TipoDocumentoFactory.Create("comanda")
   → ComandaDocument con campos individuales + ProductosHtml

5. GENERACION HTML
   PrintWorker: job.Documento.GenerarHtml()
   ComandaDocument: ¿Existe template_comanda.txt?
     SI → Lee template, genera HTML segun orden/estilo configurado
     NO → Genera HTML con formato CreaTicket hard-coded (default)

6. RENDERING
   ComandaBitmapRenderer.RenderAsBitmap(html)
   → Parsea tags HTML → aplica fuentes GDI
   → Genera Bitmap 576px ancho (80mm a 203dpi)

7. ENVIO
   EscPosCommandBuilder.AddBitmapFromImage(bitmap)
   → ESC/POS raster → TCP:9100 o USB → Impresora
```

---

## Archivos del modulo

### Proyecto Front (QuipuNet)

| Archivo | Responsabilidad |
|---|---|
| `ComandaDesigner/Models/ComandaFieldModel.cs` | Modelo de campo con INotifyPropertyChanged para binding WPF |
| `ComandaDesigner/Models/ComandaTemplateModel.cs` | Modelo raiz del template (paperWidth + fields) |
| `ComandaDesigner/Services/ComandaTemplateService.cs` | Save/Load/CreateDefault del template JSON |
| `ComandaDesigner/ComandaDesignerWindow.xaml` | UI del diseñador (3 paneles, toolbox, papel, propiedades) |
| `ComandaDesigner/ComandaDesignerWindow.xaml.cs` | Code-behind: drag-reorder, properties binding, save/load |

### Proyecto PrinterServices

| Archivo | Responsabilidad |
|---|---|
| `Queue/Documents/ITipoDocumento.cs` | Interfaz con metodo GenerarHtml() |
| `Queue/Documents/ComandaDocument.cs` | Genera HTML de comanda (con template o default) |
| `Queue/Documents/TipoDocumentoFactory.cs` | Factory: crea ITipoDocumento desde JSON |
| `Queue/Documents/ComandaTemplateModel.cs` | DTOs para deserializar template_comanda.txt |
| `Rendering/ComandaBitmapRenderer.cs` | Renderiza HTML como bitmap con fuentes GDI estilo CreaTicket |

### Archivos modificados

| Archivo | Cambio |
|---|---|
| `front/.../FeatureFlagsWindow.xaml` | +boton "Abrir Diseñador" + toggle "Usar Formato Antiguo" en pestaña Impresion |
| `front/.../FeatureFlagsWindow.xaml.cs` | +handlers para ambos controles |
| `front/.../FeatureFlagManager.cs` | +flag FORMATO_ANTIGUO_SERVICIO (default: false) |
| `front/QuipuNet.csproj` | +referencias a archivos del diseñador |
| `quipu/.../PrinterServiceClient.cs` | +envio de formato_antiguo_servicio y campos individuales en JSON |
| `printerservices/.../PrintController.cs` | +parseo del flag + creacion de ITipoDocumento via Factory |
| `printerservices/.../PrintJob.cs` | +propiedad Documento (ITipoDocumento, no se persiste) |
| `printerservices/.../PrintJobEntity.cs` | +columna formato_antiguo_servicio |
| `printerservices/.../PrintWorker.cs` | +uso de Documento.GenerarHtml() en BuildPayload |
| `printerservices/PrinterServices.csproj` | +referencias a archivos nuevos |

---

## Decisiones de diseño

### Por que posicionamiento vertical (lista) y no x/y (canvas libre)

Las impresoras termicas imprimen **linea por linea**, secuencialmente de arriba hacia abajo. No pueden saltar a posiciones arbitrarias. El unico control horizontal posible es la alineacion (izquierda, centro, derecha). Un canvas con posicionamiento libre seria engañoso porque el resultado impreso no coincidiria con lo diseñado.

### Por que JSON en un .txt

El requerimiento especifica `template_comanda.txt`. JSON es legible, editable manualmente si es necesario, y `Newtonsoft.Json` ya esta disponible en ambos proyectos.

### Por que duplicar modelos (DTOs) en PrinterServices

El front (QuipuNet) y el backend (PrinterServices) son soluciones separadas. Agregar un proyecto compartido requeriria reestructurar el build. Los DTOs son dos clases de menos de 20 lineas cada una — triviales de mantener sincronizadas. Comparten el formato JSON, no codigo.

### Por que %LOCALAPPDATA% y no junto al exe

QuipuNet usa ClickOnce para deployment. El directorio del exe se sobreescribe en cada actualizacion. `%LOCALAPPDATA%\QuipuNet\` es persistente entre actualizaciones (ahi ya se guarda `config_fla.cfg`).

### Por que el Documento no se persiste en BD

`ITipoDocumento` es un objeto transitorio — se crea al parsear el JSON del request y se usa para generar HTML. No necesita persistirse porque:
- Los campos originales (cadena, cadenaHTML) YA se persisten en PrintJobEntity
- El template esta en disco (template_comanda.txt)
- Si PrinterServices se reinicia y retoma un job pendiente, puede regenerar el Documento desde los campos persistidos

### Por que fallback a hard-coded si no hay template

El sistema debe funcionar sin template. Si el usuario activa el flag pero nunca abre el diseñador, `ComandaDocument.GenerarHtml()` usa el formato por defecto que replica CreaTicket. El template es una personalizacion opcional, no un requisito.
