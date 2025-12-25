using System;
using System.IO;

namespace WatchStats
{
    public sealed class AppConfig
    {
        public string WatchPath { get; }
        public int Workers { get; }
        public int QueueCapacity { get; }
        public int ReportIntervalSeconds { get; }
        public int TopK { get; }

        public AppConfig(string watchPath, int workers, int queueCapacity, int reportIntervalSeconds, int topK)
        {
            if (string.IsNullOrWhiteSpace(watchPath)) throw new ArgumentException("watchPath is required", nameof(watchPath));
            if (!Directory.Exists(watchPath)) throw new ArgumentException($"watchPath does not exist: {watchPath}", nameof(watchPath));
            if (workers < 1) throw new ArgumentOutOfRangeException(nameof(workers), "workers must be >= 1");
            if (queueCapacity < 1) throw new ArgumentOutOfRangeException(nameof(queueCapacity), "queueCapacity must be >= 1");
            if (reportIntervalSeconds < 1) throw new ArgumentOutOfRangeException(nameof(reportIntervalSeconds), "reportIntervalSeconds must be >= 1");
            if (topK < 1) throw new ArgumentOutOfRangeException(nameof(topK), "topK must be >= 1");

            WatchPath = Path.GetFullPath(watchPath);
            Workers = workers;
            QueueCapacity = queueCapacity;
            ReportIntervalSeconds = reportIntervalSeconds;
            TopK = topK;
        }

        public override string ToString()
        {
            return string.Format("WatchPath={0}; Workers={1}; QueueCapacity={2}; ReportIntervalSeconds={3}; TopK={4}",
                WatchPath, Workers, QueueCapacity, ReportIntervalSeconds, TopK);
        }
    }
}

