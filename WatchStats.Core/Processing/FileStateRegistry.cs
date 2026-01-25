using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace WatchStats.Core.Processing;

/// <summary>
/// Registry of <see cref="FileState"/> objects keyed by file path. Supports concurrent access.
/// </summary>
public sealed class FileStateRegistry
{
    private static class Events
    {
        public static readonly EventId FileTruncateDetected = new(3, "file_truncate_detected");
    }

    // TODO: Consider adding a cleanup mechanism for orphaned FileState entries when files are no longer being watched
    private readonly ConcurrentDictionary<string, FileState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _epochs = new(StringComparer.Ordinal);
    private readonly ILogger<FileStateRegistry>? _logger;

    /// <summary>
    /// Creates a new <see cref="FileStateRegistry"/>.
    /// </summary>
    /// <param name="logger">Optional logger for structured logging.</param>
    public FileStateRegistry(ILogger<FileStateRegistry>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets an existing <see cref="FileState"/> for <paramref name="path"/> or creates a new one with a generation based on the current epoch.
    /// </summary>
    /// <param name="path">Filesystem path for which to get or create state.</param>
    /// <returns>The <see cref="FileState"/> instance associated with <paramref name="path"/>.</returns>
    public FileState GetOrCreate(string path)
    {
        return _states.GetOrAdd(path, p =>
        {
            _epochs.TryGetValue(p, out var epoch);
            var fs = new FileState
            {
                Offset = 0,
                Carry = new PartialLineBuffer(),
                Generation = epoch + 1
            };
            return fs;
        });
    }

    /// <summary>
    /// Attempts to look up the <see cref="FileState"/> for <paramref name="path"/>.
    /// </summary>
    /// <param name="path">Filesystem path to lookup.</param>
    /// <param name="state">On success receives the associated <see cref="FileState"/>.</param>
    /// <returns><c>true</c> if a state for <paramref name="path"/> exists; otherwise <c>false</c>.</returns>
    public bool TryGet(string path, out FileState state)
    {
        return _states.TryGetValue(path, out state!);
    }

    /// <summary>
    /// Finalizes deletion of the state for <paramref name="path"/>, clearing its carry buffer for GC hygiene and bumping the epoch counter.
    /// </summary>
    /// <param name="path">Path whose state should be finalized and removed.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="path"/> is null.</exception>
    public void FinalizeDelete(string path)
    {
        if (_states.TryRemove(path, out var removed))
        {
            // clear its carry for GC
            try
            {
                removed.ClearCarry();
            }
            catch
            {
                // swallow any errors from clearing fields
                // TODO: Add structured logging for carry buffer cleanup failures (path, exception details)
            }
        }

        _epochs.AddOrUpdate(path, 1, (_, old) => old + 1);
    }

    /// <summary>
    /// Returns the current epoch (generation counter) for <paramref name="path"/>.
    /// </summary>
    /// <param name="path">Path to query.</param>
    /// <returns>The current epoch (0 when unknown) for <paramref name="path"/>.</returns>
    public int GetCurrentEpoch(string path)
    {
        _epochs.TryGetValue(path, out var e);
        return e;
    }

    /// <summary>
    /// Logs a file truncation detection event.
    /// </summary>
    /// <param name="path">Path of the truncated file.</param>
    /// <param name="previousSize">Previous file size/offset.</param>
    /// <param name="currentSize">Current file size.</param>
    internal void LogTruncation(string path, long previousSize, long currentSize)
    {
        _logger?.LogWarning(Events.FileTruncateDetected,
            "File truncation detected. Path={Path} PreviousSize={PreviousSize} CurrentSize={CurrentSize}",
            path, previousSize, currentSize);
    }
}