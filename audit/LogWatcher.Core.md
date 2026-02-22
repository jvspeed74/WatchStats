# Rule Audit — LogWatcher.Core

Rules evaluated against: `.github/instructions/dotnet.instructions.md`

---

## Violations

### BCL — Hand-rolled span byte search instead of `span.IndexOf(value)`

**File:** `Processing/Scanning/Utf8LineScanner.cs`

The same pattern appears twice — manual `for` loops to find `'\n'`:

```csharp
for (int i = 0; i < chunk.Length; i++)
{
    if (chunk[i] == (byte)'\n') { nlIndex = i; break; }
}
// and again:
for (int i = start; i < chunk.Length; i++)
{
    if (chunk[i] == (byte)'\n') { j = i; break; }
}
```

Should be replaced with `chunk.IndexOf((byte)'\n')`.

Rule (BCL table): *"Span byte search | hand-rolled `for` loop | `span.IndexOf(value)`"*  
Prohibited by Default table: *"Hand-rolled span/byte search | Manual loops for BCL operations | `span.IndexOf`, `MemoryExtensions`"*

---

### Pipeline — Nested callbacks 3+ levels deep

**File:** `Processing/FileProcessor.cs`

```csharp
TailReadStatus status = _tailer.ReadAppended(path, ref localOffset, chunk =>
{
    Utf8LineScanner.Scan(chunk, ref state.Carry, line =>
    {
        stats.LinesProcessed++;
        if (!LogParser.TryParse(line, out var parsed)) { ... }
        // level counts, message key, latency — all buried here
    });
}, out var totalBytesRead, chunkSize);
```

Three levels of anonymous callbacks. The inner logic is untestable without the full outer stack. The TODO comments in the file itself acknowledge this.

Rule: *"NEVER nest processing logic 3+ levels deep — control flow becomes untraceable, inner logic untestable. Extract one named private method per level."*  
Prohibited by Default table: *"Nested lambda chains 3+ levels | Callbacks beyond two levels | Named private method per level"*

---

### Pipeline — Properties on hot-path mutable struct

**File:** `FileManagement/PartialLineBuffer.cs`

```csharp
public struct PartialLineBuffer
{
    public byte[]? Buffer { get; private set; }
    public int Length { get; private set; }
```

`PartialLineBuffer` is a mutable struct used on the hot path (invoked per-chunk in every file scan). Its own XML doc says "simple data shape (public fields) for performance", yet the implementation uses properties with private setters, adding indirection and emitting a false encapsulation signal for a type that is explicitly mutable by design.

Rule: *"DO NOT reflexively convert fields to properties on mutable structs and hot-path state objects — adds indirection, emits false encapsulation signal on types explicitly mutable by design."*  
Prohibited by Default table: *"Properties on hot-path mutable structs | Wrapping directly-mutated fields | Fields where direct mutation is the design"*

---

### BCL — Periodic background work using `Thread` + `Thread.Sleep` loop instead of `PeriodicTimer`

**File:** `Reporting/Reporter.cs`

```csharp
private void ReporterLoop()
{
    while (!_stopping)
    {
        Thread.Sleep(TimeSpan.FromSeconds(_intervalSeconds));
        // ... swap, merge, print
    }
}
```

Rule (BCL table): *"Periodic background work | `Thread` + `Thread.Sleep` loop | `PeriodicTimer`"*

---

### Concurrency — `Task.Run` to parallelize waits in a background loop

**File:** `Reporting/Reporter.cs`

```csharp
var tasks = _workers.Select((w, idx) => Task.Run(() =>
{
    try { w.WaitForSwapAck(cts.Token); return idx; }
    catch (OperationCanceledException) { return -1; }
})).ToArray();
Task.WaitAll(tasks, _ackTimeout);
```

This creates one `Task` per worker per reporter interval. The prohibition is specifically against using `Task.Run` to parallelize waits inside a background loop.

Rule: *"DO NOT use `Task.Run` to parallelize waits in a background loop — allocates a `Task` per iteration. Use sequential waits; `Parallel.ForEach` only when parallelism is justified."*  
Prohibited by Default table: *"`Task.Run` in background loops | Task allocation in tight loops | Sequential waits; `Parallel.ForEach` only when justified"*

---

### Pipeline — LINQ enumeration in per-interval path (Allocation Coherence)

**File:** `Reporting/Reporter.cs`

```csharp
var tasks = _workers.Select((w, idx) => Task.Run(...)).ToArray();          // LINQ in loop
var ackedIndices = tasks.Where(t => t.IsCompleted && t.Result >= 0)
                        .Select(t => t.Result).ToArray();                   // LINQ in loop
```

Both expressions execute once per reporting interval and allocate enumerators, closures, and arrays on each iteration.

Rule (Allocation Coherence): *"In per-chunk, per-line, or per-interval hot paths, NEVER: LINQ enumeration"*

---

### Concurrency — `volatile` keyword instead of `Volatile.Read` / `Volatile.Write`

**Files:** `Processing/ProcessingCoordinator.cs`, `Reporting/Reporter.cs`, `Backpressure/BoundedEventBus.cs`

```csharp
private volatile bool _stopping;   // ProcessingCoordinator
private volatile bool _stopping;   // Reporter
private volatile bool _stopped;    // BoundedEventBus
```

The concurrency primitives table prescribes `Volatile.Read` / `Volatile.Write` for non-blocking flag visibility across threads. The `volatile` keyword predates this API and is not listed as the preferred approach. `FileState.cs` in the same project already uses `Volatile.Read`/`Volatile.Write` consistently — these three classes are inconsistent with the rest of the codebase.

Rule (Concurrency table): *"Non-blocking flag visibility across threads | `Volatile.Read` / `Volatile.Write`"*
