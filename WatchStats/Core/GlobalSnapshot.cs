using System;
using System.Collections.Generic;

namespace WatchStats.Core
{
    public sealed class GlobalSnapshot
    {
        // Scalars
        public long FsCreated;
        public long FsModified;
        public long FsDeleted;
        public long FsRenamed;

        public long LinesProcessed;
        public long MalformedLines;

        public long CoalescedDueToBusyGate;
        public long DeletePendingSetCount;
        public long SkippedDueToDeletePending;
        public long FileStateRemovedCount;

        public long FileNotFoundCount;
        public long AccessDeniedCount;
        public long IoExceptionCount;
        public long TruncationResetCount;

        // Bus metrics (attached after merge)
        public long BusPublished;
        public long BusDropped;
        public int BusDepth;

        public long[] LevelCounts;
        public Dictionary<string, int> MessageCounts;
        public LatencyHistogram Histogram;

        // Derived outputs
        public List<(string Key, int Count)> TopKMessages;
        public int? P50;
        public int? P95;
        public int? P99;

        public GlobalSnapshot(int topK)
        {
            LevelCounts = new long[Enum.GetNames(typeof(LogLevel)).Length];
            MessageCounts = new Dictionary<string, int>(256);
            Histogram = new LatencyHistogram();
            TopKMessages = new List<(string, int)>(topK);

            ResetForNextMerge(topK);
        }

        // Reset for next merge: clears counters and collections, preserves capacity where reasonable
        public void ResetForNextMerge(int topK)
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

            BusPublished = 0;
            BusDropped = 0;
            BusDepth = 0;

            if (LevelCounts == null || LevelCounts.Length != Enum.GetNames(typeof(LogLevel)).Length)
                LevelCounts = new long[Enum.GetNames(typeof(LogLevel)).Length];
            else
                Array.Clear(LevelCounts, 0, LevelCounts.Length);

            MessageCounts.Clear();
            Histogram.Reset();

            TopKMessages.Clear();
            P50 = P95 = P99 = null;
        }

        // Merge a worker buffer into this snapshot
        public void MergeFrom(WorkerStatsBuffer buf)
        {
            if (buf == null) throw new ArgumentNullException(nameof(buf));

            FsCreated += buf.FsCreated;
            FsModified += buf.FsModified;
            FsDeleted += buf.FsDeleted;
            FsRenamed += buf.FsRenamed;

            LinesProcessed += buf.LinesProcessed;
            MalformedLines += buf.MalformedLines;

            CoalescedDueToBusyGate += buf.CoalescedDueToBusyGate;
            DeletePendingSetCount += buf.DeletePendingSetCount;
            SkippedDueToDeletePending += buf.SkippedDueToDeletePending;
            FileStateRemovedCount += buf.FileStateRemovedCount;

            FileNotFoundCount += buf.FileNotFoundCount;
            AccessDeniedCount += buf.AccessDeniedCount;
            IoExceptionCount += buf.IoExceptionCount;
            TruncationResetCount += buf.TruncationResetCount;

            // Level counts
            int min = Math.Min(LevelCounts.Length, buf.LevelCounts.Length);
            for (int i = 0; i < min; i++)
            {
                LevelCounts[i] += buf.LevelCounts[i];
            }

            // Message counts
            foreach (var kv in buf.MessageCounts)
            {
                if (MessageCounts.TryGetValue(kv.Key, out var existing)) MessageCounts[kv.Key] = existing + kv.Value;
                else MessageCounts[kv.Key] = kv.Value;
            }

            // Histogram
            Histogram.MergeFrom(buf.Histogram);
        }

        // Finalize derived values: top-K and percentiles
        public void FinalizeSnapshot(int topK)
        {
            TopKMessages.Clear();
            if (MessageCounts.Count > 0)
            {
                var top = TopK.ComputeTopK(MessageCounts, topK);
                TopKMessages.AddRange(top);
            }

            P50 = Histogram.Percentile(0.50);
            P95 = Histogram.Percentile(0.95);
            P99 = Histogram.Percentile(0.99);
        }
    }
}

