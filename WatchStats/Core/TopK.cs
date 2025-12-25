using System;
using System.Collections.Generic;

namespace WatchStats.Core
{
    public static class TopK
    {
        // Computes top-k entries from counts dictionary.
        public static IReadOnlyList<(string Key, int Count)> ComputeTopK(Dictionary<string, int> counts, int k)
        {
            if (k <= 0) return Array.Empty<(string, int)>();
            if (counts == null || counts.Count == 0) return Array.Empty<(string, int)>();

            var list = new List<(string Key, int Count)>(counts.Count);
            foreach (var kv in counts)
            {
                list.Add((kv.Key, kv.Value));
            }

            list.Sort((a, b) =>
            {
                int c = b.Count.CompareTo(a.Count); // descending
                if (c != 0) return c;
                return StringComparer.Ordinal.Compare(a.Key, b.Key);
            });

            if (list.Count > k)
            {
                return list.GetRange(0, k);
            }

            return list;
        }
    }
}