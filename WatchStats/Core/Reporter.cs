using System;
using System.Diagnostics;
using System.Threading;

namespace WatchStats.Core
{
    /// <summary>
    /// Periodically requests worker stats swaps, merges per-worker buffers into a <see cref="GlobalSnapshot"/>, and prints a report.
    /// The reporter runs on a background thread when <see cref="Start"/> is called and stops after <see cref="Stop"/> is invoked.
    /// </summary>
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

        /// <summary>
        /// Creates a new <see cref="Reporter"/> instance.
        /// </summary>
        /// <param name="workers">Array of per-worker <see cref="WorkerStats"/> instances; used to request swaps and read inactive buffers.</param>
        /// <param name="bus">Event bus whose metrics (published/dropped/depth) are attached to the snapshot.</param>
        /// <param name="topK">Number of top messages to compute in each report; clamped to at least 1.</param>
        /// <param name="intervalSeconds">Report interval in seconds; clamped to at least 1.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="workers"/> or <paramref name="bus"/> is null.</exception>
        public Reporter(WorkerStats[] workers, BoundedEventBus<FsEvent> bus, int topK = 10, int intervalSeconds = 2)
        {
            _workers = workers ?? throw new ArgumentNullException(nameof(workers));
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
            _topK = Math.Max(1, topK);
            _intervalSeconds = Math.Max(1, intervalSeconds);
            _snapshot = new GlobalSnapshot(_topK);
        }

        /// <summary>
        /// Starts the reporter's background thread which will periodically collect and print reports.
        /// Calling <see cref="Start"/> when already started will create a new background thread; callers should ensure it is not started multiple times unintentionally.
        /// </summary>
        public void Start()
        {
            _stopping = false;
            _thread = new Thread(ReporterLoop) { IsBackground = true, Name = "reporter" };
            _thread.Start();
        }

        /// <summary>
        /// Requests the reporter to stop and waits briefly for the background thread to exit.
        /// </summary>
        public void Stop()
        {
            _stopping = true;
            try
            {
                _thread?.Join(2000);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Reporter.Stop join error: {ex}");
            }
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
                    Console.Error.WriteLine("Reporter: swap wait timed out");
                }

                // Merge/Frame build
                var frame = BuildSnapshotAndFrame();

                // Print
                PrintReportFrame(frame, elapsedSeconds);
            }

            // optional final report on stop
            try
            {
                var final = BuildSnapshotAndFrame();
                PrintReportFrame(final, 0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Reporter final report error: {ex}");
            }
        }

        /// <summary>
        /// Performs the merge of inactive worker buffers into the shared <see cref="GlobalSnapshot"/>, attaches bus metrics and finalizes derived outputs.
        /// This method is <c>internal</c> and extracted to allow unit testing of snapshot construction.
        /// </summary>
        /// <returns>The populated <see cref="GlobalSnapshot"/> instance (shared instance reused by the reporter).</returns>
        internal GlobalSnapshot BuildSnapshotAndFrame()
        {
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
            return _snapshot;
        }

        /// <summary>
        /// Formats and writes the provided snapshot to <see cref="Console"/> including allocation and GC stats.
        /// </summary>
        /// <param name="snapshot">Snapshot to print.</param>
        /// <param name="elapsedSeconds">Elapsed interval in seconds used for the printed report line.</param>
        private void PrintReportFrame(GlobalSnapshot snapshot, double elapsedSeconds)
        {
            long allocatedNow = GC.GetTotalAllocatedBytes(false);
            int gen0 = GC.CollectionCount(0);
            int gen1 = GC.CollectionCount(1);
            int gen2 = GC.CollectionCount(2);

            Console.WriteLine(
                $"[REPORT] elapsed={elapsedSeconds:0.00}s lines={snapshot.LinesProcessed} malformed={snapshot.MalformedLines} fs-events={snapshot.FsCreated + snapshot.FsModified + snapshot.FsDeleted + snapshot.FsRenamed} busDropped={snapshot.BusDropped} busPublished={snapshot.BusPublished} busDepth={snapshot.BusDepth} allocated={allocatedNow} gen0={gen0} gen1={gen1} gen2={gen2}");
            if (snapshot.TopKMessages.Count > 0)
            {
                Console.WriteLine("TopK:");
                foreach (var kv in snapshot.TopKMessages)
                {
                    Console.WriteLine($"  {kv.Key}: {kv.Count}");
                }
            }
        }
    }
}