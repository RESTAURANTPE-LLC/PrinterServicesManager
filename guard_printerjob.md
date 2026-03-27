# Guard PrinterJob — Sistema Anti-Duplicación de Impresión

## Problema Crítico

Race condition entre `StatusMonitor` y `PrintWorker` causaba **reimpresiín fatal de comandas de cocina**:

1. `PrintWorker` imprime un job y lo marca `DONE` en BD
2. `StatusMonitor` lee BD (SELECT) **antes** de que el UPDATE de `PrintWorker` se persista
3. `StatusMonitor` ve el job como `WAITING` y lo re-encola → **se reimprime**

### Ventana de vulnerabilidad adicional: Crash post-impresión

Si el servicio crashea **después** de enviar bytes a la impresora pero **antes** de marcar `DONE` en BD, al reiniciar el servicio recupera el job como `PENDING` y lo reimprime.

---

## Solución: 2 Capas de Protección

| Capa | Protege contra | Mecanismo |
|------|---------------|-----------|
| **RAM** (`_jobStates`) | Race condition en runtime | `TryChangeState()` atómico con lock por job |
| **Hash** (`printed_jobs_hash`) | Crash post-impresión | SHA256 registrado ANTES de enviar bytes |

---

## Archivos Creados

### `PrinterServices/Data/Models/PrintedJobHashEntity.cs` (NUEVO)

Modelo de tabla `printed_jobs_hash` para registrar hash SHA256 de jobs **realmente impresos físicamente**.

```csharp
[Table("printed_jobs_hash")]
public class PrintedJobHashEntity
{
    [PrimaryKey]
    [Column("job_id")]
    public string JobId { get; set; }

    [NotNull]
    [Column("impresora_id")]
    public string ImpresoraId { get; set; }

    [NotNull]
    [Column("contenido_hash")]
    public string ContenidoHash { get; set; }  // SHA256 del contenido completo

    [NotNull]
    [Column("fecha_impresion")]
    public string FechaImpresion { get; set; }  // ISO 8601

    [Column("impresora_nombre")]
    public string ImpresoraNombre { get; set; }

    [Column("area_impresion")]
    public string AreaImpresion { get; set; }

    [Column("tipo_impresion")]
    public string TipoImpresion { get; set; }

    [Column("pedido_ids")]
    public string PedidoIds { get; set; }

    [Column("comanda_id")]
    public string ComandaId { get; set; }
}
```

**Razón**: Se registra ANTES de enviar bytes a la impresora. Si el servicio crashea después de imprimir, el hash persiste y `RecoverPending()` detecta que el job ya fue impreso.

---

## Archivos Modificados

### 1. `PrinterServices/Data/PrinterServiceDb.cs`

**Cambios:**
- Agregado `CreateTable<Models.PrintedJobHashEntity>()` en `CreateTables()`
- Agregados índices:
  - `idx_printed_hash` → búsqueda rápida por hash de contenido
  - `idx_printed_impresora` → búsqueda por impresora + fecha

```csharp
// Anti-Duplicación: Hash de trabajos REALMENTE impresos físicamente
CreateTable<Models.PrintedJobHashEntity>();
```

```sql
CREATE INDEX IF NOT EXISTS idx_printed_hash ON printed_jobs_hash(contenido_hash);
CREATE INDEX IF NOT EXISTS idx_printed_impresora ON printed_jobs_hash(impresora_id, fecha_impresion);
```

**Compatibilidad**: `CreateTable<>()` crea la tabla si no existe. Servicios existentes obtienen la tabla automáticamente al actualizar.

---

### 2. `PrinterServices/Queue/PrintJobManager.cs`

#### Nuevos campos (estado en RAM como fuente de verdad)

```csharp
// Estado en RAM — fuente de verdad (thread-safe)
private readonly ConcurrentDictionary<string, PrintJobStatus> _jobStates;

// Lock por job para operaciones atómicas
private readonly ConcurrentDictionary<string, object> _jobLocks;
```

#### Nuevo método: `TryChangeState()` — Transición atómica

```csharp
public bool TryChangeState(string jobId, PrintJobStatus? expectedState, PrintJobStatus newState)
```

- Obtiene lock por job (granularidad fina)
- Verifica que el job exista en RAM
- **BARRERA CRÍTICA**: Estados terminales (DONE, FAILED, EXPIRED) NUNCA cambian
- Valida estado esperado si se especificó
- Actualiza RAM atómicamente

#### Nuevo método: `GetStateInMemory()`

```csharp
public PrintJobStatus? GetStateInMemory(string jobId)
```

Consulta directa al diccionario RAM. Usado por `StatusMonitor` y `ExpirationLoop` para verificar estado real antes de operar sobre datos de BD (potencialmente stale).

#### Nuevo método: `IsTerminalState()`

```csharp
public static bool IsTerminalState(PrintJobStatus status)
// true para: Done, Failed, Expired
```

#### Nuevo método: `MarkPrinting()`

```csharp
public bool MarkPrinting(PrintJob job)
// Transición: PENDING → PRINTING (atómica)
```

Se usa en `WorkLoop` al desencolar un job. Si el job ya no está PENDING (fue expirado, cancelado, etc.), la transición es rechazada y el job se omite.

#### Nuevo método: `CalculateContentHash()`

```csharp
public static string CalculateContentHash(PrintJob job)
```

SHA256 de: `Contenido + ContenidoHtml + ImpresoraId + ComandaId + PedidoIds + LineasImprimirJson + DocumentoJson`

#### Nuevo método: `RegisterPrintHash()`

```csharp
public void RegisterPrintHash(PrintJob job, string hash)
```

`INSERT OR REPLACE` en `printed_jobs_hash`. Se ejecuta **ANTES** de enviar bytes a la impresora.

#### Nuevo método: `IsAlreadyPrinted()`

```csharp
public bool IsAlreadyPrinted(string jobId)
// SELECT COUNT(*) FROM printed_jobs_hash WHERE job_id = ?
```

#### Nuevo método: `RetryManual()`

```csharp
public void RetryManual(PrintJob job)
```

Retry desde API/dashboard. Diferencias con `Retry()`:
- **Permite** salir de estados terminales (FAILED, EXPIRED, WAITING)
- **DONE nunca se reintenta** (ya se imprimió exitosamente)
- **Elimina hash previo** (`DELETE FROM printed_jobs_hash WHERE job_id = ?`) para permitir reimpresión
- Fuerza transición en RAM directamente

#### Método auxiliar: `LogRetry()` (refactor)

Extraído como método privado compartido entre `Retry()` y `RetryManual()`.

#### Métodos actualizados con validación atómica

| Método | Transición permitida | Retorno |
|--------|---------------------|---------|
| `MarkDone(job)` | `PRINTING → DONE` | `bool` |
| `MarkFailed(job, error)` | `* → FAILED` (no-terminal) | `bool` |
| `MarkWaiting(job, reason)` | `* → WAITING` (no-terminal) | `bool` |
| `MarkExpired(job, reason)` | `WAITING → EXPIRED` | `bool` |
| `ReEnqueue(job)` | `WAITING → PENDING` | `bool` |
| `Retry(job)` | `* → PENDING` (no-terminal) | `void` |

Todos usan `TryChangeState()` internamente. Si la transición es rechazada, logean WARN/ERROR y retornan `false`.

#### `Enqueue()` actualizado

```csharp
// Registrar estado en RAM (fuente de verdad)
_jobStates[job.JobId] = job.Estado;
```

#### `RecoverPending()` actualizado

```
1. Recupera jobs PENDING + WAITING + PRINTING (no solo PENDING/WAITING)
2. Para cada job:
   a. Verifica IsAlreadyPrinted(jobId) → si hash existe:
      - Marca DONE en BD sin reimprimir
      - Registra en RAM como DONE
   b. Si estaba en PRINTING → vuelve a PENDING (no sabemos si se imprimió)
   c. Registra en _jobStates y encola
```

---

### 3. `PrinterServices/Workers/PrintWorker.cs`

#### `WorkLoop` — Transición atómica PENDING→PRINTING

```csharp
// ANTES:
job.Estado = PrintJobStatus.Printing;

// DESPUÉS:
if (!_jobManager.MarkPrinting(job))
{
    Log.WarnFormat("[WORKER] Job {0} omitido — no está PENDING", job.JobId);
    continue;  // Skip: otro thread ya cambió el estado
}
```

#### `ProcessJobAsync()` — Hash check al inicio

```csharp
// NUEVO: Al inicio del método, antes de cualquier envío físico
if (_jobManager.IsAlreadyPrinted(job.JobId))
{
    _jobManager.MarkDone(job);
    LogPrint(job, "DONE", "Ya impreso (hash anti-duplicación)");
    return;
}
```

#### Flujo RED — Hash registrado ANTES de enviar bytes

```csharp
// NUEVO: Después de BuildPayload, ANTES del loop de copias
string contentHash = PrintJobManager.CalculateContentHash(job);
_jobManager.RegisterPrintHash(job, contentHash);

// Luego se envían las copias normalmente...
for (int copia = 0; copia < job.Copias; copia++) { ... }
```

#### Flujo USB — Hash registrado ANTES de enviar bytes

```csharp
// NUEVO: Idéntico al flujo RED
string contentHash = PrintJobManager.CalculateContentHash(job);
_jobManager.RegisterPrintHash(job, contentHash);
```

#### `ExpirationLoop` — Verificación de estado RAM

```csharp
// NUEVO: Antes de re-encolar o expirar un job WAITING
var ramState = _jobManager.GetStateInMemory(job.JobId);
if (ramState.HasValue && PrintJobManager.IsTerminalState(ramState.Value))
{
    continue;  // Ya es DONE/FAILED en RAM, BD está stale
}

// ReEnqueue ahora retorna bool:
if (_jobManager.ReEnqueue(job))
{
    LogPrint(job, "RE-ENQUEUED", "Impresora volvió online");
    requeuedCount++;
}
```

---

### 4. `PrinterServices/Monitoring/StatusMonitor.cs`

#### `RequeueWaitingJobs()` — Verificación de estado RAM

```csharp
// NUEVO: Antes de operar sobre cada job WAITING
var ramState = _jobManager.GetStateInMemory(job.JobId);
if (ramState.HasValue && PrintJobManager.IsTerminalState(ramState.Value))
{
    skippedCount++;
    continue;  // Job ya es DONE en RAM, BD retornó dato stale
}

// ReEnqueue ahora valida atómicamente:
if (_jobManager.ReEnqueue(job))
{
    requeuedCount++;  // Solo cuenta si realmente se re-encoló
}
```

Agregado contador `skippedCount` para visibilidad de jobs omitidos por estado terminal en RAM.

---

### 5. `PrinterServices/Api/Controllers/JobController.cs`

```csharp
// ANTES:
_jobManager.Retry(job);

// DESPUÉS:
_jobManager.RetryManual(job);
```

`RetryManual` permite salir de estados terminales (FAILED, EXPIRED, WAITING) y elimina el hash previo para permitir reimpresión manual desde dashboard.

---

## Diagrama de Flujo de Protección

```
Job llega → Enqueue() → _jobStates[jobId] = PENDING
                              ↓
WorkLoop dequeue → MarkPrinting() → _jobStates[jobId] = PRINTING (atómico)
                              ↓
ProcessJobAsync() → IsAlreadyPrinted()? → SÍ → MarkDone() → FIN (no reimprime)
                              ↓ NO
                    CalculateContentHash()
                    RegisterPrintHash()    ← PERSISTE HASH EN BD
                              ↓
                    Enviar bytes a impresora física
                              ↓
                    MarkDone() → _jobStates[jobId] = DONE (atómico, terminal)
                              ↓
        ┌─────────────────────┴──────────────────────────┐
        │ StatusMonitor intenta ReEnqueue                │
        │ → GetStateInMemory() = DONE → SKIP            │
        │ → TryChangeState(WAITING→PENDING) = RECHAZADO  │
        └────────────────────────────────────────────────┘
```

## Escenario de Crash

```
1. Job en PRINTING
2. RegisterPrintHash() → hash persistido en BD ✓
3. Bytes enviados a impresora ✓
4. MarkDone() → *** CRASH AQUÍ ***
5. BD tiene: estado=PRINTING, printed_jobs_hash tiene: hash del job

Al reiniciar:
6. RecoverPending() → SELECT WHERE estado IN ('PENDING','WAITING','PRINTING')
7. Para cada job: IsAlreadyPrinted(jobId)?
8. Hash encontrado → marca DONE sin reimprimir ✓
```

---

## Tabla de Transiciones de Estado Válidas

| Estado Actual | Transición Permitida | Método |
|---------------|---------------------|--------|
| PENDING | → PRINTING | `MarkPrinting()` |
| PRINTING | → DONE | `MarkDone()` |
| PRINTING | → FAILED | `MarkFailed()` |
| PRINTING | → WAITING | `MarkWaiting()` |
| PRINTING | → PENDING | `Retry()` (auto) |
| WAITING | → PENDING | `ReEnqueue()` |
| WAITING | → EXPIRED | `MarkExpired()` |
| WAITING | → PENDING | `RetryManual()` |
| **DONE** | **→ NINGUNO** | **Estado terminal inmutable** |
| **FAILED** | → PENDING | **Solo `RetryManual()`** |
| **EXPIRED** | → PENDING | **Solo `RetryManual()`** |
