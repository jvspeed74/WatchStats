using System;
using System.Collections.Generic;

namespace WatchStats.Core
{
    // Filesystem event kinds (per technical spec)
    public enum FsEventKind { Created, Modified, Deleted, Renamed }

    // Per-worker stats buffer for a single reporting interval.
    public sealed class WorkerStatsBuffer
    {
        // Fs event counters
        public long FsCreated;
        public long FsModified;
        public long FsDeleted;
        public long FsRenamed;

        // Other scalar counters
        public long LinesProcessed;
        public long MalformedLines;
        public long CoalescedDueToBusyGate;
        public long DeletePendingSetCount;
        public long SkippedDueToDeletePending;
        public long FileStateRemovedCount;

        // IO counters
        public long FileNotFoundCount;
        public long AccessDeniedCount;
        public long IoExceptionCount;
        public long TruncationResetCount;

        // Level counts sized to LogLevel enum
        public long[] LevelCounts;

        // Message counts (string keys)
        public Dictionary<string, int> MessageCounts;

        // Latency histogram
        public LatencyHistogram Histogram;

        private const int DefaultMessageCapacity = 256;

        public WorkerStatsBuffer(int messageInitialCapacity = DefaultMessageCapacity)
        {
            // Initialize fields
            LevelCounts = new long[Enum.GetNames(typeof(LogLevel)).Length];
            MessageCounts = new Dictionary<string, int>(messageInitialCapacity);
            Histogram = new LatencyHistogram();
        }

        // Reset contract: clear scalars, array, collections, and histogram
        public void Reset()
        {
            FsCreated = FsModified = FsDeleted = FsRenamed = 0;
            LinesProcessed = 0;
            MalformedLines = 0;
            CoalescedDueToBusyGate = 0;
            DeletePendingSetCount = 0;
            SkippedDueToDeletePending = 0;
            FileStateRemovedCount = 0;

            FileNotFoundCount = 0;
            AccessDeniedCount = 0;
            IoExceptionCount = 0;
            TruncationResetCount = 0;

            Array.Clear(LevelCounts);
            MessageCounts.Clear();
            Histogram.Reset();
        }

        // Convenience helpers
        public void IncrementFsEvent(FsEventKind kind)
        {
            switch (kind)
            {
                case FsEventKind.Created: FsCreated++; break;
                case FsEventKind.Modified: FsModified++; break;
                case FsEventKind.Deleted: FsDeleted++; break;
                case FsEventKind.Renamed: FsRenamed++; break;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        public void IncrementLevel(LogLevel level)
        {
            var idx = (int)level;
            if (idx < 0 || idx >= LevelCounts.Length) return;
            LevelCounts[idx]++;
        }

        public void IncrementMessage(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (MessageCounts.TryGetValue(key, out var v)) MessageCounts[key] = v + 1;
            else MessageCounts[key] = 1;
        }

        public void RecordLatency(int latencyMs)
        {
            Histogram.Add(latencyMs);
        }
    }
}

