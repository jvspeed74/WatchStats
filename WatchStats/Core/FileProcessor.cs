using System;
using System.Text;

namespace WatchStats.Core
{
    public interface IFileProcessor
    {
        void ProcessOnce(string path, FileState state, WorkerStatsBuffer stats, int chunkSize = 64 * 1024);
    }

    // FileProcessor: tail -> scan -> parse -> update WorkerStatsBuffer
    public sealed class FileProcessor : IFileProcessor
    {
        private readonly FileTailer _tailer;

        public FileProcessor(FileTailer tailer = null)
        {
            _tailer = tailer ?? new FileTailer();
        }

        // Process whatever is appended right now. Caller must hold state.Gate.
        public void ProcessOnce(string path, FileState state, WorkerStatsBuffer stats, int chunkSize = 64 * 1024)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (stats == null) throw new ArgumentNullException(nameof(stats));

            // Precondition: caller must hold state.Gate. We won't double-check locking here, but document it.
            // Use local offset to avoid advancing state.Offset until processing completes.
            long localOffset = state.Offset;

            int totalBytesRead = 0;
            bool sawTruncation = false;

            TailReadStatus status = _tailer.ReadAppended(path, ref localOffset, chunk =>
            {
                // For each chunk, run the UTF8 scanner using state's carry buffer
                Utf8LineScanner.Scan(chunk, ref state.Carry, line =>
                {
                    // For each complete line
                    stats.LinesProcessed++;

                    if (!LogParser.TryParse(line, out var parsed))
                    {
                        stats.MalformedLines++;
                        return;
                    }

                    // level counts
                    stats.IncrementLevel(parsed.Level);

                    // message key to string
                    string key = parsed.MessageKey.IsEmpty ? string.Empty : Encoding.UTF8.GetString(parsed.MessageKey);
                    if (stats.MessageCounts.TryGetValue(key, out var c)) stats.MessageCounts[key] = c + 1;
                    else stats.MessageCounts[key] = 1;

                    // latency
                    if (parsed.LatencyMs is int v)
                    {
                        stats.Histogram.Add(v);
                    }
                });
            }, out totalBytesRead, chunkSize);

            // handle status counters
            switch (status)
            {
                case TailReadStatus.FileNotFound:
                    stats.FileNotFoundCount++;
                    break;
                case TailReadStatus.AccessDenied:
                    stats.AccessDeniedCount++;
                    break;
                case TailReadStatus.IoError:
                    stats.IoExceptionCount++;
                    break;
                case TailReadStatus.TruncatedReset:
                    stats.TruncationResetCount++;
                    break;
                case TailReadStatus.NoData:
                case TailReadStatus.ReadSome:
                default:
                    break;
            }

            // advance state.Offset only after successful processing
            if (totalBytesRead > 0 || status == TailReadStatus.TruncatedReset)
            {
                state.Offset = localOffset;
            }
        }
    }
}
