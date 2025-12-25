using System;
using System.Collections.Generic;
using System.Threading;

namespace WatchStats.Core
{
    // Bounded event bus with drop-newest semantics (drop the incoming event when full)
    public sealed class BoundedEventBus<T>
    {
        private readonly int _capacity;
        private readonly Queue<T> _queue = new Queue<T>();
        private readonly object _lock = new object();
        private bool _stopped;

        private long _published;
        private long _dropped;

        public BoundedEventBus(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
        }

        // Publish: returns true if enqueued; false if dropped or stopped
        public bool Publish(T item)
        {
            lock (_lock)
            {
                if (_stopped)
                {
                    return false;
                }

                if (_queue.Count >= _capacity)
                {
                    Interlocked.Increment(ref _dropped);
                    return false; // drop newest
                }

                _queue.Enqueue(item);
                Interlocked.Increment(ref _published);
                Monitor.Pulse(_lock);
                return true;
            }
        }

        // Try to dequeue with timeout in milliseconds. Returns true if item dequeued; false on timeout or stop with empty queue.
        public bool TryDequeue(out T item, int timeoutMs)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(Math.Max(0, timeoutMs));

            lock (_lock)
            {
                while (true)
                {
                    if (_queue.Count > 0)
                    {
                        item = _queue.Dequeue();
                        return true;
                    }

                    if (_stopped)
                    {
                        item = default!;
                        return false;
                    }

                    var remaining = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
                    if (remaining == 0)
                    {
                        item = default!;
                        return false;
                    }

                    // Wait might return earlier due to Pulse; loop will re-check conditions
                    Monitor.Wait(_lock, remaining);
                }
            }
        }

        // Stop: unblocks waiting consumers
        public void Stop()
        {
            lock (_lock)
            {
                _stopped = true;
                Monitor.PulseAll(_lock);
            }
        }

        // Metrics (thread-safe reads)
        public long PublishedCount => Interlocked.Read(ref _published);
        public long DroppedCount => Interlocked.Read(ref _dropped);
        public int Depth
        {
            get
            {
                lock (_lock)
                {
                    return _queue.Count;
                }
            }
        }
    }
}

