using System;
using System.Threading;
using System.Collections.Generic;

namespace WatchStats.Core
{
    public sealed class ProcessingCoordinator
    {
        private readonly BoundedEventBus<FsEvent> _bus;
        private readonly FileStateRegistry _registry;
        private readonly IFileProcessor _processor;
        private readonly WorkerStats[] _workerStats;
        private readonly Thread[] _threads;
        private readonly int _dequeueTimeoutMs;

        private volatile bool _stopping;

        public ProcessingCoordinator(BoundedEventBus<FsEvent> bus, FileStateRegistry registry, IFileProcessor processor,
            WorkerStats[] workerStats, int workerCount = 4, int dequeueTimeoutMs = 200)
        {
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _processor = processor ?? throw new ArgumentNullException(nameof(processor));
            _workerStats = workerStats ?? throw new ArgumentNullException(nameof(workerStats));

            int wc = Math.Max(1, workerCount);
            _dequeueTimeoutMs = Math.Max(10, dequeueTimeoutMs);

            _threads = new Thread[wc];
            for (int i = 0; i < wc; i++)
            {
                int idx = i;
                _threads[i] = new Thread(() => WorkerLoop(idx)) { IsBackground = true, Name = $"ws-{i}" };
            }
        }

        public void Start()
        {
            _stopping = false;
            foreach (var t in _threads) t.Start();
        }

        public void Stop()
        {
            _stopping = true;
            _bus.Stop();
            try
            {
                foreach (var t in _threads)
                {
                    if (!t.Join(2000)) t.Interrupt();
                }
            }
            catch
            {
            }
        }

        private void WorkerLoop(int workerIndex)
        {
            var stats = _workerStats[workerIndex];
            while (!_stopping)
            {
                if (!_bus.TryDequeue(out var ev, _dequeueTimeoutMs))
                {
                    // nothing dequeued; continue loop
                    continue;
                }

                // Increment filesystem event counter in active stats
                stats.Active.IncrementFsEvent(ev.Kind);

                // Route event
                switch (ev.Kind)
                {
                    case FsEventKind.Created:
                    case FsEventKind.Modified:
                        if (ev.Processable)
                            HandleCreateOrModify(ev.Path, stats.Active);
                        break;
                    case FsEventKind.Deleted:
                        HandleDelete(ev.Path, stats.Active);
                        break;
                    case FsEventKind.Renamed:
                        if (!string.IsNullOrEmpty(ev.OldPath))
                            HandleDelete(ev.OldPath, stats.Active);
                        if (ev.Processable)
                            HandleCreateOrModify(ev.Path, stats.Active);
                        break;
                }

                // Acknowledge swap after fully handling this event
                stats.AcknowledgeSwapIfRequested();
            }
        }

        private void HandleCreateOrModify(string path, WorkerStatsBuffer stats)
        {
            var state = _registry.GetOrCreate(path);
            // try acquire gate
            if (!Monitor.TryEnter(state.Gate))
            {
                state.MarkDirtyIfAllowed();
                stats.CoalescedDueToBusyGate++;
                return;
            }

            try
            {
                if (state.IsDeletePending)
                {
                    stats.SkippedDueToDeletePending++;
                    _registry.FinalizeDelete(path);
                    stats.FileStateRemovedCount++;
                    return;
                }

                // catch-up loop
                while (true)
                {
                    if (state.IsDeletePending)
                    {
                        _registry.FinalizeDelete(path);
                        stats.FileStateRemovedCount++;
                        return;
                    }

                    // process once
                    _processor.ProcessOnce(path, state, stats);

                    if (state.IsDeletePending)
                    {
                        _registry.FinalizeDelete(path);
                        stats.FileStateRemovedCount++;
                        return;
                    }

                    if (state.IsDirty)
                    {
                        state.ClearDirty();
                        continue; // immediate re-read
                    }

                    break;
                }
            }
            finally
            {
                Monitor.Exit(state.Gate);
            }
        }

        private void HandleDelete(string path, WorkerStatsBuffer stats)
        {
            if (!_registry.TryGet(path, out var state))
            {
                return;
            }

            // try to acquire gate
            if (!Monitor.TryEnter(state.Gate))
            {
                state.MarkDeletePending();
                stats.DeletePendingSetCount++;
                return;
            }

            try
            {
                state.MarkDeletePending();
                _registry.FinalizeDelete(path);
                stats.FileStateRemovedCount++;
            }
            finally
            {
                Monitor.Exit(state.Gate);
            }
        }
    }
}