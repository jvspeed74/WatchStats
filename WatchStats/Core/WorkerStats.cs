using System;
using System.Threading;

namespace WatchStats.Core
{
    // Per-worker double-buffered stats with swap/ack semantics (S2a/R2)
    public sealed class WorkerStats
    {
        private readonly WorkerStatsBuffer _a;
        private readonly WorkerStatsBuffer _b;

        private WorkerStatsBuffer _active;
        private WorkerStatsBuffer _inactive;

        private int _swapRequested;
        private readonly ManualResetEventSlim _swapAck;

        public WorkerStats(int messageInitialCapacity = 256)
        {
            _a = new WorkerStatsBuffer(messageInitialCapacity);
            _b = new WorkerStatsBuffer(messageInitialCapacity);

            _active = _a;
            _inactive = _b;

            _swapRequested = 0;
            _swapAck = new ManualResetEventSlim(true); // initially acknowledged
        }

        // Worker-visible active buffer
        public WorkerStatsBuffer Active => _active;

        // Reporter-visible inactive buffer (reporter should only call this after WaitForSwapAck)
        public WorkerStatsBuffer Inactive => _inactive;

        // Reporter requests a swap; workers will perform swap at their next safe point
        public void RequestSwap()
        {
            _swapAck.Reset();
            Volatile.Write(ref _swapRequested, 1);
        }

        // Reporter waits for this worker to ack the swap. Throws on cancellation.
        public void WaitForSwapAck(CancellationToken ct)
        {
            _swapAck.Wait(ct);
        }

        // Worker calls this at end of each event handling to acknowledge swap if requested.
        public void AcknowledgeSwapIfRequested()
        {
            if (Volatile.Read(ref _swapRequested) == 0)
                return;

            // perform swap: swap references
            var prevActive = _active;
            _active = _inactive;
            _inactive = prevActive;

            // reset new active buffer so workers start fresh
            _active.Reset();

            // clear request
            Volatile.Write(ref _swapRequested, 0);

            // set ack so reporter can proceed
            _swapAck.Set();
        }

        // Helper for reporter to get the inactive buffer for merging (should be called after WaitForSwapAck)
        public WorkerStatsBuffer GetInactiveBufferForMerge()
        {
            return _inactive;
        }
    }
}