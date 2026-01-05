using System.Collections.Concurrent;

namespace WatchStats.Core.Processing
{
    /// <summary>
    /// Per-path mutable state used by the tailer and processor.
    /// Callers should synchronize access to mutable fields using the <see cref="Gate"/> object.
    /// </summary>
    public sealed class FileState
    {
        /// <summary>
        /// Lock object used to synchronize concurrent access to this FileState.
        /// Callers should acquire this lock (for example via <c>lock(state.Gate)</c> or <c>Monitor.Enter(state.Gate)</c>)
        /// when reading or modifying fields such as <see cref="Offset"/> and <see cref="Carry"/>.
        /// </summary>
        public readonly object Gate = new object();

        /// <summary>Offset (in bytes) representing how many bytes have been consumed from the file.</summary>
        public long Offset;

        /// <summary>Carry buffer that holds trailing, incomplete line bytes between reads.</summary>
        public PartialLineBuffer Carry;

        private int _dirty;
        private int _deletePending;

        /// <summary>Generation number assigned at creation (epoch + 1).</summary>
        public int Generation;

        /// <summary>Returns true when the state is marked dirty and needs reprocessing.</summary>
        public bool IsDirty => Volatile.Read(ref _dirty) == 1;
        /// <summary>Returns true when a delete has been requested and the state should be finalized.</summary>
        public bool IsDeletePending => Volatile.Read(ref _deletePending) == 1;

        /// <summary>
        /// Marks the state dirty if deletion is not currently pending. This may be called without holding <see cref="Gate"/>.
        /// </summary>
        public void MarkDirtyIfAllowed()
        {
            // If delete pending is set, do not mark dirty
            if (Volatile.Read(ref _deletePending) == 1) return;
            Volatile.Write(ref _dirty, 1);
        }

        /// <summary>
        /// Clears the dirty flag. Typically called while holding <see cref="Gate"/>.
        /// </summary>
        public void ClearDirty()
        {
            Volatile.Write(ref _dirty, 0);
        }

        /// <summary>
        /// Requests deletion of the state (marks delete-pending) and clears the dirty flag.
        /// </summary>
        public void MarkDeletePending()
        {
            Volatile.Write(ref _deletePending, 1);
            // override dirty
            Volatile.Write(ref _dirty, 0);
        }

        /// <summary>
        /// Helper to clear carry memory for GC hygiene. This sets the carry buffer to null and zeroes its length.
        /// Callers typically hold <see cref="Gate"/> when invoking this.
        /// </summary>
        public void ClearCarry()
        {
            // reset buffer and length
            Carry.Buffer = null;
            Carry.Length = 0;
        }
    }

    /// <summary>
    /// Registry of <see cref="FileState"/> objects keyed by file path. Supports concurrent access.
    /// </summary>
    public sealed class FileStateRegistry
    {
        private readonly ConcurrentDictionary<string, FileState> _states = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _epochs = new(StringComparer.Ordinal);

        /// <summary>
        /// Gets an existing <see cref="FileState"/> for <paramref name="path"/> or creates a new one with a generation based on the current epoch.
        /// </summary>
        /// <param name="path">Filesystem path for which to get or create state.</param>
        /// <returns>The <see cref="FileState"/> instance associated with <paramref name="path"/>.</returns>
        /// <exception cref="ArgumentNullException">When <paramref name="path"/> is null.</exception>
        public FileState GetOrCreate(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));

            return _states.GetOrAdd(path, p =>
            {
                int epoch = 0;
                _epochs.TryGetValue(p, out epoch);
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
        /// <exception cref="ArgumentNullException">When <paramref name="path"/> is null.</exception>
        public bool TryGet(string path, out FileState state)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            return _states.TryGetValue(path, out state);
        }

        // Internal helper
        public bool TryRemove(string path, out FileState removed)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            return _states.TryRemove(path, out removed);
        }

        /// <summary>
        /// Finalizes deletion of the state for <paramref name="path"/>, clearing its carry buffer for GC hygiene and bumping the epoch counter.
        /// </summary>
        /// <param name="path">Path whose state should be finalized and removed.</param>
        /// <exception cref="ArgumentNullException">When <paramref name="path"/> is null.</exception>
        public void FinalizeDelete(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));

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
                }
            }

            _epochs.AddOrUpdate(path, 1, (_, old) => old + 1);
        }

        /// <summary>
        /// Returns the current epoch (generation counter) for <paramref name="path"/>.
        /// </summary>
        /// <param name="path">Path to query.</param>
        /// <returns>The current epoch (0 when unknown) for <paramref name="path"/>.</returns>
        /// <exception cref="ArgumentNullException">When <paramref name="path"/> is null.</exception>
        public int GetCurrentEpoch(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            _epochs.TryGetValue(path, out var e);
            return e;
        }
    }
}