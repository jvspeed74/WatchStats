using System.Text;
using Microsoft.Extensions.Logging;
using WatchStats.Core.IO;
using WatchStats.Core.Metrics;

namespace WatchStats.Core.Processing
{
    /// <summary>
    /// Abstraction for a component that processes appended data in a single file once.
    /// Implementations should read newly appended bytes, extract complete lines, parse them
    /// and update the provided <see cref="WorkerStatsBuffer"/>.
    /// </summary>
    public interface IFileProcessor
    {
        /// <summary>
        /// Process whatever was appended to <paramref name="path"/> since <paramref name="state"/>.Offset.
        /// The caller is responsible for acquiring any necessary synchronization (e.g. <c>state.Gate</c>);
        /// this method will not attempt to acquire it.
        /// </summary>
        /// <param name="path">Filesystem path to the file to process.</param>
        /// <param name="state">File-specific state object (offset/carry buffer) that will be read and updated.</param>
        /// <param name="stats">Worker-local statistics buffer that will be updated from parsed lines.</param>
        /// <param name="chunkSize">Optional read chunk size passed to the file tailer. Defaults to 64KiB.</param>
        /// <param name="workerId">Optional worker identifier for logging. Defaults to -1 (not logged).</param>
        void ProcessOnce(string path, FileState state, WorkerStatsBuffer stats, int chunkSize = 64 * 1024, int workerId = -1);
    }

    // FileProcessor: tail -> scan -> parse -> update WorkerStatsBuffer
    /// <summary>
    /// Implementation of <see cref="IFileProcessor"/> that uses <see cref="FileTailer"/>,
    /// <see cref="Utf8LineScanner"/> and <see cref="LogParser"/> to read appended data,
    /// parse newline-delimited UTF-8 log lines and update a <see cref="WorkerStatsBuffer"/>.
    /// </summary>
    public sealed class FileProcessor : IFileProcessor
    {
        private static class Events
        {
            public static readonly EventId WorkerBatchProcessed = new(5, "worker_batch_processed");
        }

        private readonly FileTailer _tailer;
        private readonly FileStateRegistry? _registry;
        private readonly ILogger<FileProcessor>? _logger;

        /// <summary>
        /// Creates a new <see cref="FileProcessor"/>. An optional <see cref="FileTailer"/> may be supplied
        /// (useful for tests); when <c>null</c> a default <see cref="FileTailer"/> is created.
        /// </summary>
        /// <param name="tailer">Optional tailer used to read appended bytes from files.</param>
        /// <param name="registry">Optional registry for logging truncation events.</param>
        /// <param name="logger">Optional logger for structured logging.</param>
        public FileProcessor(FileTailer? tailer = null, FileStateRegistry? registry = null, ILogger<FileProcessor>? logger = null)
        {
            _tailer = tailer ?? new FileTailer();
            _registry = registry;
            _logger = logger;
        }

        /// <summary>
        /// Process whatever is appended right now. Caller must hold <c>state.Gate</c>.
        /// This method advances <c>state.Offset</c> only after processing completes successfully.
        /// For each complete UTF-8 line it increments counters in <paramref name="stats"/>,
        /// updates message counts, and adds parsed latency values to the histogram.
        /// </summary>
        /// <param name="path">Path to the file to process. Must not be <c>null</c>.</param>
        /// <param name="state">File state object that contains offset and carry buffer. Must not be <c>null</c>.</param>
        /// <param name="stats">Worker-local statistics buffer to update. Must not be <c>null</c>.</param>
        /// <param name="chunkSize">Read buffer size in bytes for tailer. Defaults to 64KiB.</param>
        /// <param name="workerId">Worker identifier for logging. Defaults to -1 (not logged).</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="path"/>, <paramref name="state"/>, or <paramref name="stats"/> is <c>null</c>.</exception>
        public void ProcessOnce(string path, FileState state, WorkerStatsBuffer stats, int chunkSize = 64 * 1024, int workerId = -1)
        {
            // Precondition: caller must hold state.Gate. We won't double-check locking here, but document it.
            // Use local offset to avoid advancing state.Offset until processing completes.
            long localOffset = state.Offset;
            var processingStartTime = DateTime.UtcNow;
            long startLinesProcessed = stats.LinesProcessed;
            long startMalformed = stats.MalformedLines;

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
                    if (parsed.LatencyMs is { } v)
                    {
                        stats.Histogram.Add(v);
                    }
                });
            }, out var totalBytesRead, out var previousSize, out var currentSize, chunkSize);

            // Log worker batch processed (Debug level)
            if (totalBytesRead > 0 || status == TailReadStatus.TruncatedReset)
            {
                var duration = (DateTime.UtcNow - processingStartTime).TotalMilliseconds;
                var linesProcessedInBatch = stats.LinesProcessed - startLinesProcessed;
                var malformedInBatch = stats.MalformedLines - startMalformed;
                
                if (workerId >= 0)
                {
                    _logger?.LogDebug(Events.WorkerBatchProcessed,
                        "Worker batch processed. WorkerId={WorkerId} LinesProcessed={LinesProcessed} MalformedLines={MalformedLines} DurationMs={DurationMs}",
                        workerId, linesProcessedInBatch, malformedInBatch, (int)duration);
                }
                else
                {
                    _logger?.LogDebug(Events.WorkerBatchProcessed,
                        "Worker batch processed. LinesProcessed={LinesProcessed} MalformedLines={MalformedLines} DurationMs={DurationMs}",
                        linesProcessedInBatch, malformedInBatch, (int)duration);
                }
            }

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
                    // Log truncation event via FileStateRegistry if available
                    _registry?.LogTruncation(path, previousSize, currentSize);
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