using BenchmarkDotNet.Attributes;

using LogWatcher.Core.Backpressure;
using LogWatcher.Core.Ingestion;

namespace LogWatcher.Benchmarks;

[MemoryDiagnoser]
public class BoundedEventBusBenchmarks
{
    // Large enough that the benchmark never fills it during a normal run.
    private BoundedEventBus<FsEvent> _bus = null!;

    // Small bus used to measure publish behavior when the bus is at capacity.
    private BoundedEventBus<FsEvent> _fullBus = null!;

    private FsEvent _event;

    [GlobalSetup]
    public void Setup()
    {
        _event = new FsEvent(
            FsEventKind.Modified,
            "/tmp/app.log",
            null,
            DateTimeOffset.UtcNow,
            true);

        _bus = new BoundedEventBus<FsEvent>(10_000);
        _fullBus = new BoundedEventBus<FsEvent>(1);
    }

    // Ensure _fullBus is at capacity before each Publish_BusFull iteration.
    [IterationSetup(Target = nameof(Publish_BusFull))]
    public void FillBus() => _fullBus.Publish(_event);

    // Drain _fullBus after each Publish_BusFull iteration.
    [IterationCleanup(Target = nameof(Publish_BusFull))]
    public void DrainBus() => _fullBus.TryDequeue(out _, 0);

    // Ensure _bus has room before each Publish_BusHasCapacity iteration.
    [IterationSetup(Target = nameof(Publish_BusHasCapacity))]
    public void EnsureCapacity() => _bus.TryDequeue(out _, 0);

    /// <summary>
    /// Happy path: bus always has room. Measures the cost of a successful enqueue.
    /// </summary>
    [Benchmark]
    public bool Publish_BusHasCapacity() => _bus.Publish(_event);

    /// <summary>
    /// Drop-newest path: bus is at capacity. Publish must return immediately without blocking.
    /// Expected: near-zero latency (INV BP-006).
    /// </summary>
    [Benchmark]
    public bool Publish_BusFull() => _fullBus.Publish(_event);

    /// <summary>
    /// Dequeue from a bus that contains one item.
    /// </summary>
    [Benchmark]
    public bool TryDequeue_WithItem()
    {
        _bus.Publish(_event);
        return _bus.TryDequeue(out _, 0);
    }
}