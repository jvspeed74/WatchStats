using System;
using System.Collections.Concurrent;
using System.Threading;

namespace WatchStats.Core
{
    // Per-path mutable state used by tailer/processor
    public sealed class FileState
    {
        public readonly object Gate = new object();

        // mutated under Gate
        public long Offset;
        public PartialLineBuffer Carry;

        private int _dirty;
        private int _deletePending;

        // Generation = epoch + 1 at creation
        public int Generation;

        public bool IsDirty => Volatile.Read(ref _dirty) == 1;
        public bool IsDeletePending => Volatile.Read(ref _deletePending) == 1;

        public void MarkDirtyIfAllowed()
        {
            // If delete pending is set, do not mark dirty
            if (Volatile.Read(ref _deletePending) == 1) return;
            Volatile.Write(ref _dirty, 1);
        }

        // Clear dirty under Gate in practice
        public void ClearDirty()
        {
            Volatile.Write(ref _dirty, 0);
        }

        public void MarkDeletePending()
        {
            Volatile.Write(ref _deletePending, 1);
            // override dirty
            Volatile.Write(ref _dirty, 0);
        }

        // Helper to clear carry memory for GC hygiene
        public void ClearCarry()
        {
            // reset buffer and length
            Carry.Buffer = null;
            Carry.Length = 0;
        }
    }

    public sealed class FileStateRegistry
    {
        private readonly ConcurrentDictionary<string, FileState> _states = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _epochs = new(StringComparer.Ordinal);

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

        public int GetCurrentEpoch(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            _epochs.TryGetValue(path, out var e);
            return e;
        }
    }
}

