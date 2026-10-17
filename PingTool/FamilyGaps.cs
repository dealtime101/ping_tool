using System.Globalization;

namespace PingTool
{
    // The stretches of time when only ONE IP family of a dual-stack service answered: the targets of one name probed once with #family=4 and
    // once with #family=6 (same kind and port), an outage on one of them while the other had none. ServiceDiagnosis says it live, for the
    // moment; this is what the report keeps of the whole period, from the outages of the incident log.
    internal static class FamilyGaps
    {
        public const int MaxLines = 10;
        private static readonly TimeSpan Shortest = TimeSpan.FromSeconds(1);

        public static List<string> For(IEnumerable<string> addresses, IReadOnlyList<Incident> incidents, DateTimeOffset end)
        {
            // The services that are probed on both families: the key is the name (case does not matter), the kind and the port.
            var services = new Dictionary<(string Host, ProbeKind Kind, int Port), (string? V4, string? V6)>();
            foreach (string address in addresses)
            {
                if (!ProbeTarget.TryParse(address, out var t, out _) || t.Family == 0) continue;
                var key = (t.Host.ToLowerInvariant(), t.Kind, t.Port);
                services.TryGetValue(key, out var pair);
                services[key] = t.Family == 4 ? (address, pair.V6) : (pair.V4, address);
            }

            var lines = new List<string>();
            foreach (var ((host, kind, port), (v4, v6)) in services.OrderBy(s => s.Key.Host, StringComparer.Ordinal))
            {
                if (v4 is null || v6 is null) continue;
                var down4 = Merge(OutagesOf(v4, incidents, end));
                var down6 = Merge(OutagesOf(v6, incidents, end));
                string name = kind == ProbeKind.Tcp ? $"{host}:{port.ToString(CultureInfo.InvariantCulture)}" : host;
                foreach (var gap in Subtract(down6, down4))
                    lines.Add(Line(name, "IPv6", "IPv4", gap));
                foreach (var gap in Subtract(down4, down6))
                    lines.Add(Line(name, "IPv4", "IPv6", gap));
            }

            if (lines.Count > MaxLines)
            {
                int more = lines.Count - MaxLines;
                lines = lines.Take(MaxLines).ToList();
                lines.Add($"(+{more.ToString(CultureInfo.InvariantCulture)} more)");
            }

            return lines;
        }

        private static string Line(string name, string failed, string answered, (DateTimeOffset From, DateTimeOffset To) gap) =>
            $"{name}: only {answered} answered from {gap.From.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} to "
            + $"{gap.To.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} ({IncidentLog.FormatDuration(gap.To - gap.From)}): {failed} was down.";

        private static List<(DateTimeOffset From, DateTimeOffset To)> OutagesOf(string address, IReadOnlyList<Incident> incidents, DateTimeOffset end) =>
            incidents.Where(i => i.Kind == IncidentKind.Outage && TargetKey.Same(i.Host, address))
                .Select(i => (From: i.Start, To: i.End ?? end)).Where(s => s.To > s.From).ToList();

        // Overlapping or touching stretches as one.
        internal static List<(DateTimeOffset From, DateTimeOffset To)> Merge(List<(DateTimeOffset From, DateTimeOffset To)> spans)
        {
            var merged = new List<(DateTimeOffset From, DateTimeOffset To)>();
            foreach (var s in spans.OrderBy(s => s.From))
            {
                if (merged.Count > 0 && s.From <= merged[^1].To) merged[^1] = (merged[^1].From, s.To > merged[^1].To ? s.To : merged[^1].To);
                else merged.Add(s);
            }

            return merged;
        }

        // What is in `a` and not in `b` (both merged), without the stretches shorter than a second.
        internal static List<(DateTimeOffset From, DateTimeOffset To)> Subtract(List<(DateTimeOffset From, DateTimeOffset To)> a, List<(DateTimeOffset From, DateTimeOffset To)> b)
        {
            var result = new List<(DateTimeOffset From, DateTimeOffset To)>();
            foreach (var (from, to) in a)
            {
                var cursor = from;
                foreach (var cut in b)
                {
                    if (cut.To <= cursor) continue;
                    if (cut.From >= to) break;
                    if (cut.From > cursor) result.Add((cursor, cut.From));
                    cursor = cut.To > cursor ? cut.To : cursor;
                }

                if (cursor < to) result.Add((cursor, to));
            }

            return result.Where(r => r.To - r.From >= Shortest).ToList();
        }
    }
}
