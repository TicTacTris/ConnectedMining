using System.Collections.Generic;
using System.Linq;

namespace ConnectedMining
{
    // Server-only reservations. An overlapping proposal never steals the initiating hit
    // or any section from the first accepted chain. Independent chains can run together.
    internal sealed class ChainLedger
    {
        private readonly Dictionary<string, long> claims = new Dictionary<string, long>();
        private readonly Dictionary<long, HashSet<string>> jobs = new Dictionary<long, HashSet<string>>();
        private long next;

        internal bool TryStart(IEnumerable<string> keys, out long job)
        {
            job = 0;
            var unique = new HashSet<string>(keys);
            if (unique.Count == 0 || unique.Any(claims.ContainsKey)) return false;
            job = ++next;
            jobs.Add(job, unique);
            foreach (string key in unique) claims.Add(key, job);
            return true;
        }

        internal bool TryExtend(long job, string key)
        {
            if (!jobs.TryGetValue(job, out var set) || claims.ContainsKey(key)) return false;
            set.Add(key);
            claims.Add(key, job);
            return true;
        }

        internal void Release(long job)
        {
            if (!jobs.TryGetValue(job, out var set)) return;
            foreach (string key in set) claims.Remove(key);
            jobs.Remove(job);
        }
    }
}
