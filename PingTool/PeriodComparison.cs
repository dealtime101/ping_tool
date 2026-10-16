using System.Globalization;
using System.Net;
using System.Text;

namespace PingTool
{
    // What one target did over one period, measured from its log: the figures a comparison needs.
    internal sealed record HostFigures(string Host, int Pings, double LossPercent, double? Avg, double? P50, double? P95, double? P99,
        AvailabilityRow Availability, TimeSpan Measured, TimeSpan Gaps);

    internal sealed record PeriodFigures(string Name, DateTimeOffset From, DateTimeOffset To, int Pings, Dictionary<string, HostFigures> Hosts, HashSet<int> Hours);

    // "Did the new router (the cable, the provider) help?": two periods of logs, side by side per target, with the gain or the loss in
    // words and the reasons why the comparison may mislead. Targets are paired by address; the logs do not record the probe settings or the
    // limits used, so both periods are read with the default ones.
    internal static class PeriodComparison
    {
        public const int FewPings = 30;
        public static readonly TimeSpan ShortPeriod = TimeSpan.FromHours(1);

        public static PeriodFigures Measure(string name, ReplayResult replay, IReadOnlyCollection<LogEntry> entries)
        {
            var ordered = entries.Distinct().OrderBy(e => e.Time).ToList();
            var gapOf = LogReplay.GapLimits(ordered);
            var hosts = new Dictionary<string, HostFigures>(TargetKey.Comparer);
            var hours = new HashSet<int>(ordered.Select(e => e.Time.ToLocalTime().Hour));

            foreach (var group in ordered.GroupBy(e => e.Host, StringComparer.Ordinal))
            {
                var list = group.ToList();
                var history = list.Select(e => e.Status == "OK" && e.RttMs is not null ? e.RttMs.Value : -1L).ToList();
                var stats = RecentStats.From(history, int.MaxValue);
                var gaps = TimeSpan.Zero;
                for (int i = 1; i < list.Count; i++)
                    if (list[i].Time - list[i - 1].Time > gapOf[group.Key]) gaps += list[i].Time - list[i - 1].Time;

                var first = list[0].Time;
                var last = list[^1].Time;
                hosts[group.Key] = new HostFigures(group.Key, list.Count, 100.0 * stats.Lost / Math.Max(1, stats.Samples), stats.Mean, stats.P50, stats.P95, stats.P99,
                    Availability.For(group.Key, replay.Incidents.Incidents, first, last), last - first - gaps, gaps);
            }

            return new PeriodFigures(name, replay.From, replay.To, ordered.Count, hosts, hours);
        }

        // The reasons to read the comparison with care, one sentence each. Empty only when nothing at all is doubtful (it never is: the
        // last one is always there).
        public static List<string> Cautions(PeriodFigures before, PeriodFigures after)
        {
            var said = new List<string>();
            var c = CultureInfo.CurrentCulture;
            var lenB = before.To - before.From;
            var lenA = after.To - after.From;
            string Len(TimeSpan t) => IncidentLog.FormatDuration(t);

            if (lenB < ShortPeriod || lenA < ShortPeriod)
                said.Add($"A period is short (before: {Len(lenB)}, after: {Len(lenA)}): under an hour, one incident or one quiet moment decides the figures.");
            else if (Math.Max(lenB.Ticks, lenA.Ticks) > 2 * Math.Min(lenB.Ticks, lenA.Ticks))
                said.Add($"The periods are of very different length (before: {Len(lenB)}, after: {Len(lenA)}): the longer one has had more time to meet an incident.");

            int shared = before.Hours.Intersect(after.Hours).Count();
            int union = before.Hours.Union(after.Hours).Count();
            if (union > 0 && shared < union / 2.0)
                said.Add("The two periods were measured at different hours of the day (for instance a night and a working day): a connection that is busier at some hours is not comparable with itself at others.");

            foreach (var host in before.Hosts.Keys.Except(after.Hosts.Keys, TargetKey.Comparer))
                said.Add($"{host} is in the \"before\" log only: it is not compared.");
            foreach (var host in after.Hosts.Keys.Except(before.Hosts.Keys, TargetKey.Comparer))
                said.Add($"{host} is in the \"after\" log only: it is not compared.");

            foreach (var (host, b) in before.Hosts)
            {
                if (!after.Hosts.TryGetValue(host, out var a)) continue;
                if (b.Pings < FewPings || a.Pings < FewPings)
                    said.Add($"{host}: few pings (before: {b.Pings.ToString(c)}, after: {a.Pings.ToString(c)}); the percentiles and the loss mean little.");
                if (b.Gaps > TimeSpan.Zero || a.Gaps > TimeSpan.Zero)
                    said.Add($"{host}: time with no measure (PingTool closed, the PC asleep) - before: {Len(b.Gaps)}, after: {Len(a.Gaps)}; an outage in those holes is not counted.");
            }

            said.Add("The logs do not record the probe settings (interval, timeout, packet size) or the limits used, nor what was changed between the periods: "
                + "both are read with the default limits, and a different interval or packet size alone can change the figures. Write down what you changed.");
            return said;
        }

        // "better", "worse" or "about the same" for a figure where LOWER is better (latency, loss, outages, time down). Null when one side has no figure.
        public static string? Verdict(double? before, double? after, double absoluteSame, double relativeSame = 0.10)
        {
            if (before is not double b || after is not double a) return null;
            double change = Math.Abs(a - b);
            if (change <= absoluteSame || change <= relativeSame * Math.Abs(b)) return "about the same";
            return a < b ? "better" : "worse";
        }

        public static string BuildHtml(PeriodFigures before, PeriodFigures after, DateTimeOffset generated)
        {
            var c = CultureInfo.CurrentCulture;
            string E(string s) => WebUtility.HtmlEncode(s);
            string N(double? v, string unit = "") => v is null ? "-" : v.Value.ToString("0.#", c) + unit;
            // Cut, never rounded up, to two decimals: 99.997 % with a real outage must not read "100 %" (as in the report).
            string Pct(double? v) => v is null ? "-" : (Math.Floor(v.Value * 100 + 1e-9) / 100).ToString("0.##", c) + " %";
            string Span(PeriodFigures p) => $"{p.From.ToLocalTime():yyyy-MM-dd HH:mm} to {p.To.ToLocalTime():yyyy-MM-dd HH:mm} ({IncidentLog.FormatDuration(p.To - p.From)}), {p.Pings.ToString("N0", c)} pings";
            string Dur(TimeSpan? t) => t is null ? "-" : IncidentLog.FormatDuration(t.Value);

            var h = new StringBuilder();
            h.AppendLine("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\" /><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\" /><title>PingTool comparison of two periods</title>");
            h.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#222;max-width:900px}h1{font-size:22px}h2{font-size:16px;margin-top:28px;border-bottom:1px solid #ccc;padding-bottom:4px}"
                + "table{border-collapse:collapse;width:100%;font-size:13px}th,td{border:1px solid #ccc;padding:4px 8px;text-align:left}th{background:#f0f0f0}td.n{text-align:right}"
                + ".box{background:#fff4e5;border:1px solid #e0b060;padding:8px 12px}.better{color:#1a7f37;font-weight:600}.worse{color:#b00020;font-weight:600}.same{color:#555}.w{overflow-x:auto}small{color:#555}</style></head><body>");
            h.AppendLine("<h1>PingTool - comparison of two periods</h1>");
            h.AppendLine(c, $"<p><small>Generated {E(generated.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", c))}. Targets are paired by address. Lower is better for every figure but availability.</small></p>");
            h.AppendLine("<table><tr><th></th><th>Period</th></tr>");
            h.AppendLine($"<tr><th>Before: {E(before.Name)}</th><td>{E(Span(before))}</td></tr><tr><th>After: {E(after.Name)}</th><td>{E(Span(after))}</td></tr></table>");

            var paired = before.Hosts.Where(kv => after.Hosts.ContainsKey(kv.Key)).OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
            int better = 0, worse = 0, same = 0;
            var tables = new StringBuilder();

            foreach (var (host, b) in paired)
            {
                var a = after.Hosts[host];
                tables.AppendLine(c, $"<h2>{E(host)}</h2><div class=\"w\"><table><tr><th>Figure</th><th>Before</th><th>After</th><th>Change</th></tr>");

                void Line(string name, string left, string right, string? verdict, string change, bool count = false)
                {
                    string css = verdict switch { "better" => "better", "worse" => "worse", _ => "same" };
                    tables.AppendLine($"<tr><th>{E(name)}</th><td class=\"n\">{E(left)}</td><td class=\"n\">{E(right)}</td><td class=\"{css}\">{E(change)}{(verdict is null ? "" : " - " + verdict)}</td></tr>");
                    if (!count) return;
                    if (verdict == "better") better++; else if (verdict == "worse") worse++; else if (verdict == "about the same") same++;
                }

                string Diff(double? x, double? y, string unit) => x is double p && y is double q ? (q - p >= 0 ? "+" : "") + (q - p).ToString("0.#", c) + unit : "";

                Line("Pings", b.Pings.ToString("N0", c), a.Pings.ToString("N0", c), null, "");
                Line("Time measured", Dur(b.Measured), Dur(a.Measured), null, "");
                double? availB = b.Availability.AvailabilityPercent, availA = a.Availability.AvailabilityPercent;
                // Higher is better: compared as the share of time spent down, so the same rule applies.
                Line("Availability", Pct(availB), Pct(availA),
                    Verdict(availB is null ? null : 100 - availB, availA is null ? null : 100 - availA, 0.01), Diff(availB, availA, " points"), true);
                Line("Outages", b.Availability.Outages.ToString(c), a.Availability.Outages.ToString(c), Verdict(b.Availability.Outages, a.Availability.Outages, 0, 0),
                    Diff(b.Availability.Outages, a.Availability.Outages, ""), true);
                Line("Total down", Dur(b.Availability.TotalDown), Dur(a.Availability.TotalDown),
                    Verdict(b.Availability.TotalDown.TotalSeconds, a.Availability.TotalDown.TotalSeconds, 5), Diff(b.Availability.TotalDown.TotalSeconds, a.Availability.TotalDown.TotalSeconds, " s"), true);
                Line("Longest outage", Dur(b.Availability.Longest), Dur(a.Availability.Longest),
                    Verdict(b.Availability.Longest?.TotalSeconds ?? 0, a.Availability.Longest?.TotalSeconds ?? 0, 5), "");
                Line("Ping loss", N(b.LossPercent, " %"), N(a.LossPercent, " %"), Verdict(b.LossPercent, a.LossPercent, 0.5), Diff(b.LossPercent, a.LossPercent, " points"), true);
                Line("Latency, average", N(b.Avg, " ms"), N(a.Avg, " ms"), Verdict(b.Avg, a.Avg, 2), Diff(b.Avg, a.Avg, " ms"), true);
                Line("Latency, median (p50)", N(b.P50, " ms"), N(a.P50, " ms"), Verdict(b.P50, a.P50, 2), Diff(b.P50, a.P50, " ms"));
                Line("Latency, p95", N(b.P95, " ms"), N(a.P95, " ms"), Verdict(b.P95, a.P95, 2), Diff(b.P95, a.P95, " ms"));
                Line("Latency, p99", N(b.P99, " ms"), N(a.P99, " ms"), Verdict(b.P99, a.P99, 2), Diff(b.P99, a.P99, " ms"));
                tables.AppendLine("</table></div>");
            }

            h.AppendLine("<h2>In short</h2>");
            h.AppendLine(paired.Count == 0
                ? "<p class=\"box\">No target is in both logs, so nothing can be compared. Targets are paired by address.</p>"
                : $"<p>{paired.Count.ToString(c)} target(s) in both periods. Across availability, outages, time down, loss and average latency: <span class=\"better\">{better.ToString(c)} better</span>, "
                    + $"<span class=\"worse\">{worse.ToString(c)} worse</span>, {same.ToString(c)} about the same. \"About the same\" means a change under 10 % or under the noise of the figure "
                    + "(2 ms, 0.5 point of loss, 5 s of outage).</p>");

            h.AppendLine("<h2>Read with care</h2><div class=\"box\"><ul>");
            foreach (string caution in Cautions(before, after)) h.AppendLine($"<li>{E(caution)}</li>");
            h.AppendLine("</ul></div>");
            h.Append(tables);
            h.AppendLine("<hr /><p><small>Generated by PingTool from its log files. The names of the targets are written as they are in the logs; this file is not masked.</small></p></body></html>");
            return h.ToString();
        }
    }
}
