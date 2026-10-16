namespace PingTool
{
    // One target's alert, kept back for a moment so that several can be told as one.
    internal sealed record HeldAlert(string Address, string Name, HostChange Change, string Text, TimeSpan? Outage, DateTimeOffset Time);

    // The optional grouping of alerts (Settings.AlertGroupSeconds, 0 = off): a connection that drops takes many targets down within a
    // few seconds, and one balloon, one sound and one webhook message each is a burst nobody can read. While the window is open, the
    // "down" alerts are held, and the "up" alerts apart; when it closes, two or more of a kind become ONE summary naming the targets,
    // and a single one is told as it always was. Degraded and recovered alerts are never held. The incidents themselves, the log and the
    // report are not touched: each target keeps its own outage there.
    internal sealed class AlertGrouper
    {
        public const int MaxSeconds = 120;
        public const int NamesShown = 4;

        public int WindowSeconds { get; set; }

        private readonly Dictionary<HostChange, List<HeldAlert>> held = new();

        public static bool CanGroup(HostChange change) => change is HostChange.Down or HostChange.Up;

        // True when the alert is kept back; `first` then says that it opened the window, so the caller has to flush when it closes.
        public bool Hold(HeldAlert alert, out bool first)
        {
            first = false;
            if (WindowSeconds <= 0 || !CanGroup(alert.Change)) return false;

            if (!held.TryGetValue(alert.Change, out var list)) held[alert.Change] = list = new List<HeldAlert>();
            first = list.Count == 0;
            list.Add(alert);
            return true;
        }

        public List<HeldAlert> Take(HostChange change)
        {
            if (!held.Remove(change, out var list)) return new List<HeldAlert>();
            return list;
        }

        // The text of several alerts of one kind told as one: how many, which, and for the targets back up how long the longest outage lasted.
        public static string Summary(IReadOnlyList<HeldAlert> items)
        {
            var names = items.Select(i => i.Name).Distinct().ToList();
            string list = string.Join(", ", names.Take(NamesShown)) + (names.Count > NamesShown ? " " + Loc.T("alert.group.more", names.Count - NamesShown) : "");
            if (items[0].Change == HostChange.Down) return Loc.T("alert.group.down", names.Count, list);

            TimeSpan? longest = items.Where(i => i.Outage is not null).Select(i => i.Outage!.Value).DefaultIfEmpty().Max();
            return longest is { } d && d > TimeSpan.Zero
                ? Loc.T("alert.group.up.after", names.Count, list, IncidentLog.FormatDuration(d))
                : Loc.T("alert.group.up", names.Count, list);
        }
    }
}
