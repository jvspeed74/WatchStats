using System;

namespace WatchStats.Core
{
    // Bounded, mergeable histogram for latencies 0..10000 plus overflow bin at index 10001
    public sealed class LatencyHistogram
    {
        private const int MaxMs = 10_000;
        private const int OverflowIndex = MaxMs + 1; // 10001
        private const int BinCount = MaxMs + 2; // 0..10000 plus overflow

        private readonly int[] _bins;
        private long _count;

        public LatencyHistogram()
        {
            _bins = new int[BinCount];
            _count = 0;
        }

        // Expose for tests/inspection (read-only)
        public ReadOnlySpan<int> Bins => _bins;
        public long Count => _count;

        public void Add(int latencyMs)
        {
            int idx;
            if (latencyMs < 0) idx = 0;
            else if (latencyMs <= MaxMs) idx = latencyMs;
            else idx = OverflowIndex;

            _bins[idx]++;
            _count++;
        }

        public void Reset()
        {
            Array.Clear(_bins, 0, _bins.Length);
            _count = 0;
        }

        public void MergeFrom(LatencyHistogram other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));

            for (int i = 0; i < _bins.Length; i++)
            {
                _bins[i] += other._bins[i];
            }
            _count += other._count;
        }

        // p in (0..1] (e.g., 0.5 for p50). Returns null if no samples.
        // Overflow is represented by returning 10001.
        public int? Percentile(double p)
        {
            if (_count == 0) return null;
            if (double.IsNaN(p) || p <= 0) p = 0; // will clamp below
            if (p > 1) p = 1;

            long target = (long)Math.Ceiling(p * _count);
            if (target < 1) target = 1;
            if (target > _count) target = _count;

            long cumulative = 0;
            for (int i = 0; i < _bins.Length; i++)
            {
                cumulative += _bins[i];
                if (cumulative >= target)
                {
                    // return bin index; overflow bin is at OverflowIndex
                    return i;
                }
            }

            // should not reach here, but as a fallback return overflow
            return OverflowIndex;
        }
    }
}

