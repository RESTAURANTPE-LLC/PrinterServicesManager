# Vibe Engineering — Post-Print Wait (cruce de tickets con mucho negro)

## 1. Síntoma reportado

Al imprimir tickets con bitmaps con **mucho negro** (ej: cuadrado negro relleno de ~3 cm de alto) en impresoras térmicas ESC/POS:

- La primera impresión **no termina de imprimir** y queda a la mitad.
- Se **cruza con la segunda** impresión, que sale completa debajo de la primera truncada, en el **mismo papel**.
- Ocurre especialmente con `BitmapEmulacion = escasterisc` (ESC \* por bandas).

---

## 2. Causa raíz

`PrintWorker` marcaba el job como `DONE` en el instante en que `TcpTransport.SendAsync()` / `UsbTransport.SendAsync()` devolvía — pero ese momento **solo confirma que los bytes salieron del NIC**, NO que la impresora terminó de imprimir físicamente.

| Evento | Tiempo típico (3 cm negro) |
|---|---|
| `SendAsync` (TCP) devuelve | 50–200 ms |
| Impresora termina de imprimir físicamente | 500–1500 ms+ (throttling térmico por densidad de puntos) |

Entre esos dos instantes, el worker tomaba el siguiente job del queue, abría una nueva conexión TCP, y enviaba `ESC @` (Init). Muchos modelos **resetean** ante `ESC @` → corte del ticket en curso + inicio del nuevo encima. También había pérdida de buffer al cerrar el socket con datos pendientes.

### Por qué `escasterisc` lo empeoraba

`AddBitmapEscAsterisk` (ESC \* modo 33) envía **3 bytes por columna en bandas de 24 px** → ~3× más bytes que `GS v 0`, procesados banda por banda. La ventana entre "TCP terminó" y "papel terminó" se agranda → más probabilidad de pisar.

---

## 3. Fix implementado: Opción 6 — Delay post-send proporcional al negro

No se hace polling DLE EOT (opción 1) ni lock por impresora (opción 2), sino una **espera heurística barata** tras cada `SendAsync` exitoso, calculada desde el propio bitmap antes de convertirlo a bytes ESC/POS.

### Fórmula

```
estimatedMs = height * BaseMsPerRow
            + Σ(darkRatio_por_fila) * ExtraMsPerBlackRow
```

Clampada a `[PostPrintWaitMinMs, PostPrintWaitMaxMs]`.

- `darkRatio_por_fila` = `pixels_negros / width` donde "negro" = luma `< 128`.
- Paths sin bitmap (modo `LINEAS`, `CADENA`) → `estimatedMs = 0`.

### Ejemplo con tu caso

- Cuadrado negro de 3 cm a 8 dots/mm = **240 filas** al 100% negro.
- `240 * 2 ms + 240 * 1.0 * 10 ms = 2880 ms` → clampado a **4000 ms** si el ticket total supera el techo.

---

## 4. Configuración nueva (`ConfigManager`)

| Key | Default | Descripción |
|---|---|---|
| `PostPrintWaitEnabled` | `1` | Master switch |
| `PostPrintWaitBaseMsPerRow` | `2` | ms por fila clara (base para ~150 mm/s a 8 dots/mm) |
| `PostPrintWaitExtraMsPerBlackRow` | `10` | ms extra por fila 100% negra (compensa throttling térmico) |
| `PostPrintWaitMinMs` | `100` | Piso para bitmaps chicos |
| `PostPrintWaitMaxMs` | `4000` | **Techo de seguridad** |

Todos tocables en caliente vía `POST /api/config/sync` o SQL console del dashboard.

---

## 5. Archivos tocados

| # | Archivo | Cambio |
|---|---|---|
| 1 | `Rendering/PrintDurationEstimator.cs` *(nuevo)* | Helper estático: `EstimateMs(Bitmap)`. Recorre filas con `LockBits` (fast path 24/32 bpp) o `GetPixel` (fallback). Suma `darkRatio` por fila. Lee configs + clamp. |
| 2 | `Config/ConfigManager.cs` | +5 defaults categoría `timing` |
| 3 | `Workers/PrintWorker.cs` | `BuildPayload` devuelve `BuiltPayload { Data, EstimatedWaitMs }`; calcula el estimate dentro del `using` del bitmap (antes de disposar); pasa a `SendWithRetryInstrumented` / `SendUsbWithRetry`; nuevo helper `WaitForPhysicalPrintAsync` aplica `Task.Delay` tras cada send+disconnect exitoso (entre copias y al final). |
| 4 | `PrinterServices.csproj` | +`Compile Include Rendering\PrintDurationEstimator.cs` |

---

## 6. Dónde se aplica la espera

Dentro de `SendWithRetryInstrumented` (TCP) y `SendUsbWithRetry` (USB), **después** de `transport.SendAsync() + transport.Disconnect()` y **antes** de `return true`:

```csharp
await transport.SendAsync(payload, ct);
transport.Disconnect();
await WaitForPhysicalPrintAsync(job, estimatedWaitMs, "TCP", ct);
return true;
```

- Aplica **entre copias** (cada `Copias > 1` espera entre envíos).
- Aplica al **final del job** → el worker no llama `DequeueAsync()` del siguiente hasta que venció la espera.
- Respeta `CancellationToken` → shutdown no queda colgado.

---

## 7. Calibración en producción

| Síntoma | Ajuste |
|---|---|
| Siguen cruzándose tickets muy negros | Subir `PostPrintWaitExtraMsPerBlackRow` a `12`–`15` |
| Latencia innecesaria entre jobs sin bitmap pesado | Bajar `PostPrintWaitBaseMsPerRow` a `1` |
| Tickets muy largos con mucho negro se truncan aun con la espera | Subir `PostPrintWaitMaxMs` (techo de seguridad) |
| Modelo específico se cruza pero los otros no | Dejar el default y evaluar opción 1 (polling DLE EOT post-send) en un segundo paso |

---

## 8. Limitaciones conocidas (no arreglado aquí)

- **No hay lock por impresora**: si eventualmente se agregan múltiples workers en paralelo, se pueden mandar dos jobs simultáneos a la misma IP. Opción 2 quedó pendiente.
- **La espera es una heurística**, no una confirmación real de la impresora. En modelos con buffer muy chico o térmica muy lenta puede subestimar. Para eso está la opción 1 (DLE EOT post-send) como siguiente iteración si el síntoma persiste.
- **`BitmapEmulacion = escpos` sigue siendo preferible** a `escasterisc` cuando el modelo lo soporta (3× menos bytes, menos throttling). Recomendado evaluar en paralelo al deploy de este fix.

---

## 9. Tradeoff explícito

- **Ventaja**: cero dependencia del modelo de impresora, sin polling, sin locks, fórmula 100% configurable.
- **Desventaja**: si la fórmula se calibra mal puede agregar latencia entre jobs (sobreestima) o no alcanzar a prevenir cruces (subestima). Mitigado con los 5 parámetros tocables en caliente.
