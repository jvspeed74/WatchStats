using System;
using System.Diagnostics;
using System.Threading;

namespace WatchStats.Core
{
    // Simple Reporter that follows the documented behavior: requests swap, waits for acks, merges into GlobalSnapshot, and prints a simple report.
    public sealed class Reporter
    {
        private readonly WorkerStats[] _workers;
        private readonly BoundedEventBus<FsEvent> _bus;
        private readonly int _topK;
        private readonly int _intervalSeconds;
        private Thread? _thread;
        private volatile bool _stopping;

        // snapshot reused across reports
        private readonly GlobalSnapshot _snapshot;

        public Reporter(WorkerStats[] workers, BoundedEventBus<FsEvent> bus, int topK = 10, int intervalSeconds = 2)
        {
            _workers = workers ?? throw new ArgumentNullException(nameof(workers));
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
            _topK = Math.Max(1, topK);
            _intervalSeconds = Math.Max(1, intervalSeconds);
            _snapshot = new GlobalSnapshot(_topK);
        }

        public void Start()
        {
            _stopping = false;
            _thread = new Thread(ReporterLoop) { IsBackground = true, Name = "reporter" };
            _thread.Start();
        }

        public void Stop()
        {
            _stopping = true;
            try
            {
                _thread?.Join(2000);
            }
            catch { }
        }

        private void ReporterLoop()
        {
            var sw = Stopwatch.StartNew();
            long lastTicks = sw.ElapsedTicks;

            while (!_stopping)
            {
                Thread.Sleep(TimeSpan.FromSeconds(_intervalSeconds));

                var nowTicks = sw.ElapsedTicks;
                double elapsedSeconds = (nowTicks - lastTicks) / (double)Stopwatch.Frequency;
                lastTicks = nowTicks;

                // Swap phase
                foreach (var w in _workers) w.RequestSwap();
                // wait for acks with cancellation support
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, _intervalSeconds)));
                try
                {
                    foreach (var w in _workers)
                    {
                        w.WaitForSwapAck(cts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // timeout or cancelled; proceed with what we have
                }

                // Merge phase
                _snapshot.ResetForNextMerge(_topK);
                foreach (var w in _workers)
                {
                    var buf = w.GetInactiveBufferForMerge();
                    _snapshot.MergeFrom(buf);
                }

                // attach bus metrics
                _snapshot.BusPublished = _bus.PublishedCount;
                _snapshot.BusDropped = _bus.DroppedCount;
                _snapshot.BusDepth = _bus.Depth;

                _snapshot.FinalizeSnapshot(_topK);

                // GC deltas simple capture
                long allocatedNow = GC.GetTotalAllocatedBytes(false);
                int gen0 = GC.CollectionCount(0);
                int gen1 = GC.CollectionCount(1);
                int gen2 = GC.CollectionCount(2);

                // Print a compact report line
                PrintReport(elapsedSeconds, allocatedNow, gen0, gen1, gen2);
            }

            // optional final report on stop
            try
            {
                _snapshot.ResetForNextMerge(_topK);
                foreach (var w in _workers)
                {
                    var buf = w.GetInactiveBufferForMerge();
                    _snapshot.MergeFrom(buf);
                }
                _snapshot.BusPublished = _bus.PublishedCount;
                _snapshot.BusDropped = _bus.DroppedCount;
                _snapshot.BusDepth = _bus.Depth;
                _snapshot.FinalizeSnapshot(_topK);
                PrintReport(0, GC.GetTotalAllocatedBytes(false), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
            }
            catch { }
        }

        private void PrintReport(double elapsedSeconds, long allocatedNow, int gen0, int gen1, int gen2)
        {
            // Print a single-line summary and top-K on subsequent lines
            Console.WriteLine($"[REPORT] elapsed={elapsedSeconds:0.00}s lines={_snapshot.LinesProcessed} malformed={_snapshot.MalformedLines} fs-events={_snapshot.FsCreated + _snapshot.FsModified + _snapshot.FsDeleted + _snapshot.FsRenamed} busDropped={_snapshot.BusDropped} busPublished={_snapshot.BusPublished} busDepth={_snapshot.BusDepth} allocated={allocatedNow} gen0={gen0} gen1={gen1} gen2={gen2}");
            if (_snapshot.TopKMessages != null && _snapshot.TopKMessages.Count > 0)
            {
                Console.WriteLine("TopK:");
                foreach (var kv in _snapshot.TopKMessages)
                {
                    Console.WriteLine($"  {kv.Key}: {kv.Count}");
                }
            }
        }
    }
}

