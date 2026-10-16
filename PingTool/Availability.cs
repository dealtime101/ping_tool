using System.Globalization;

namespace PingTool
{
    // One clock hour of one host: the pings sent and lost in it. Hour = the start of the hour on the PC's wall clock.
    // Repeats = how many real hours this clock hour stands for: 2 for the hour that happens twice when the clocks go back (the grid has
    // one column per clock hour, so it shows the worse of the two and says so).
    // Offset = the UTC offset of the real hour these figures are those of (null when it is not known): the two hours that share a clock
    // time are told apart by it. Seen = how many of the real hours had pings: 2 for a repeated hour measured in both, 1 when only one was.
    // NoSuchHour = a clock hour that the clocks skipped (the day they went forward): nothing was missed, there was no such hour.
    internal sealed record HourCell(DateTime Hour, long Sent, long Lost, int Repeats = 1, TimeSpan? Offset = null, int Seen = 1, bool NoSuchHour = false)
    {
        public double LossPercent => Sent == 0 ? 0 : 100.0 * Lost / Sent;
    }

    // The figures a provider's support asks for first: how much of the time the target was reachable, how many cuts, how long.
    internal sealed record AvailabilityRow(string Host, double? AvailabilityPercent, int Outages, TimeSpan TotalDown, TimeSpan? Mean, TimeSpan? Longest);

    internal static class Availability
    {
        public const int MaxGridDays = 31;

        // Availability = the share of the monitoring period NOT spent in an outage (an outage runs from the first failed ping
        // to the first success after it; a slowdown is not counted as down). An outage still going on counts up to `end`.
        public static AvailabilityRow For(string host, IEnumerable<Incident> incidents, DateTimeOffset from, DateTimeOffset end)
        {
            // Only the part of an outage that lies INSIDE the period counts: one that began before `from` (the log of an earlier day, a
            // report for a window) is cut at `from`, and one still going on is cut at `end`. An outage wholly outside is not one of this period,
            // and neither is one that only TOUCHES it: ended exactly at `from` or began exactly at `end`, it has no time in the period and would
            // be a cut counted, with a mean pulled down and a longest of zero, for nothing.
            var outages = incidents.Where(i => i.Host == host && i.Kind == IncidentKind.Outage && i.Start < end && (i.End ?? end) > from).ToList();

            // Outages that cover the same time (two lists of incidents put together, an incident given twice) are one stretch of downtime:
            // added, the same minutes would be counted twice and the availability would fall below the truth, even to 0. The incident log
            // of one host never produces two at once, so this holds the figures right whatever list is handed in.
            var spans = outages
                .Select(i => (Start: i.Start > from ? i.Start : from, Stop: (i.End ?? end) < end ? (i.End ?? end) : end))
                .Select(s => s.Stop < s.Start ? (s.Start, Stop: s.Start) : s)
                .OrderBy(s => s.Start).ThenBy(s => s.Stop)
                .ToList();
            var merged = new List<(DateTimeOffset Start, DateTimeOffset Stop)>();
            foreach (var s in spans)
            {
                if (merged.Count > 0 && s.Start <= merged[^1].Stop) merged[^1] = (merged[^1].Start, s.Stop > merged[^1].Stop ? s.Stop : merged[^1].Stop);
                else merged.Add(s);
            }

            var durations = merged.Select(m => Clamp(m.Stop - m.Start)).ToList();
            var down = durations.Aggregate(TimeSpan.Zero, (a, b) => a + b);
            var period = end - from;

            double? percent = period <= TimeSpan.Zero ? null : Math.Clamp(100.0 * (1 - down.Ticks / (double)period.Ticks), 0, 100);
            return new AvailabilityRow(host, percent, merged.Count, down,
                durations.Count == 0 ? null : TimeSpan.FromTicks(down.Ticks / durations.Count),
                durations.Count == 0 ? null : durations.Max());
        }

        private static TimeSpan Clamp(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;

        // A share of lost pings to one decimal, without ever saying more or less than is true: a real loss that rounds to 0 reads "<0.1"
        // (not "0", which would call a lossy host clean), a loss below 100 that rounds to 100 reads ">99.9" (not "100").
        public static string FormatLoss(double percent, IFormatProvider culture)
        {
            if (percent <= 0) return "0";
            if (percent >= 100) return "100";
            double rounded = Math.Round(percent, 1, MidpointRounding.AwayFromZero);
            // The limits are numbers like the others: in the decimal mark of the culture asked for ("<0,1" in French), not a fixed point.
            if (rounded <= 0) return "<" + 0.1.ToString("0.#", culture);
            if (rounded >= 100) return ">" + 99.9.ToString("0.#", culture);
            return rounded.ToString("0.#", culture);
        }

        // The colour class of one cell of the hour-by-day grid: none = no ping that hour, then by the share of lost pings.
        public static string LossClass(HourCell? cell) => cell is null || cell.Sent == 0 ? "hn"
            : cell.Lost == 0 ? "h0"
            : cell.LossPercent <= 5 ? "h1"
            : cell.LossPercent <= 30 ? "h2"
            : "h3";

        // One row per CALENDAR day, the most recent MaxGridDays of them from the first day with data (or the last 31 days if there are more),
        // 24 cells each (null = no ping that hour). A day with no ping at all (the PC was off, the tool stopped) is a row of empty cells:
        // leaving it out would close the gap on screen and let "the last 31 days" reach back much further than 31 days.
        // zone: the time zone the clock hours are in (the PC's), a parameter so that the hour that happens twice can be tested.
        public static List<(DateTime Day, HourCell?[] Hours)> Grid(IEnumerable<HourCell> cells, TimeZoneInfo? zone = null)
        {
            zone ??= TimeZoneInfo.Local;
            var byDay = cells.GroupBy(c => c.Hour.Date).ToDictionary(g => g.Key, g => g.ToList());
            var rows = new List<(DateTime, HourCell?[])>();
            if (byDay.Count == 0) return rows;

            DateTime last = byDay.Keys.Max();
            DateTime first = byDay.Keys.Min();
            DateTime earliest = last.AddDays(-(MaxGridDays - 1));
            if (first < earliest) first = earliest;

            for (DateTime day = first; day <= last; day = day.AddDays(1))
            {
                var hours = new HourCell?[24];
                if (byDay.TryGetValue(day, out var present))
                    foreach (var cell in present)
                    {
                        var previous = hours[cell.Hour.Hour];
                        // The hour that happens twice when the clocks go back (two real hours, one column): the figures of ONE hour, the worse
                        // one, so that a bad hour is not averaged away by the good one, and the cell says it stands for two (Title).
                        hours[cell.Hour.Hour] = previous is null ? cell
                            : (cell.LossPercent > previous.LossPercent ? cell : previous)
                                with { Repeats = previous.Repeats + cell.Repeats, Seen = previous.Seen + cell.Seen };
                    }

                // A clock hour that happens twice but was measured in one of its two only: a single figure, and it must not read as an
                // ordinary hour (the other one of the two is not in the data, and which one this is would be lost).
                for (int h = 0; h < 24; h++)
                    if (hours[h] is { Repeats: 1 } single && zone.IsAmbiguousTime(day.AddHours(h)))
                        hours[h] = single with { Repeats = 2 };

                // The hour the clocks skipped (02:00 to 03:00 the day they go forward) has no ping because it did not exist, which is not
                // the same thing as a hour that was not measured: said so, instead of "no ping". An hour of which only a part was skipped
                // (a change of half an hour) exists, and has its pings in the cell.
                for (int h = 0; h < 24; h++)
                    if (hours[h] is null && DoesNotExist(zone, day.AddHours(h)))
                        hours[h] = new HourCell(day.AddHours(h), 0, 0, NoSuchHour: true);

                rows.Add((day, hours));
            }

            return rows;
        }

        // True when no minute of the clock hour starting at `start` existed: the start, the half and the last minute are all in the hour that the
        // clocks skipped. A change of half an hour (Lord Howe) leaves the second half of the hour: that hour exists.
        internal static bool DoesNotExist(TimeZoneInfo zone, DateTime start) =>
            zone.IsInvalidTime(start) && zone.IsInvalidTime(start.AddMinutes(30)) && zone.IsInvalidTime(start.AddMinutes(59));

        // The hours of the grid in which pings were lost, one sentence each (the same as the hover text of the square), oldest first: for
        // whoever has no hover (a touch screen) and no screen reader, and cannot read a colour. Empty when no hour lost a ping.
        public static string LossHoursText(IEnumerable<(DateTime Day, HourCell?[] Hours)> grid) =>
            string.Join("; ", grid.SelectMany(row => Enumerable.Range(0, 24)
                .Where(hour => row.Hours[hour] is { Lost: > 0 })
                .Select(hour => Title(row.Day, hour, row.Hours[hour]))));

        public static string Title(DateTime day, int hour, HourCell? cell) =>
            cell is { NoSuchHour: true }
                ? string.Create(CultureInfo.InvariantCulture, $"{day:yyyy-MM-dd} {hour:00}:00 - this hour does not exist (the clocks went forward)")
            : cell is null || cell.Sent == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{day:yyyy-MM-dd} {hour:00}:00 - no ping")
                : string.Create(CultureInfo.InvariantCulture, $"{day:yyyy-MM-dd} {hour:00}:00 - {FormatLoss(cell.LossPercent, CultureInfo.InvariantCulture)}% lost ({cell.Lost} of {cell.Sent})")
                    + (cell.Repeats <= 1 ? ""
                        : cell.Seen > 1 ? " - this clock hour happened twice (the clocks went back): the worse of the two is shown"
                        : " - this clock hour happened twice (the clocks went back) and only one of the two has pings" + (cell.Offset is { } o ? $" (UTC{(o < TimeSpan.Zero ? "-" : "+")}{o.Duration():hh\\:mm})" : ""));
    }
}
