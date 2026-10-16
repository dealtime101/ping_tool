namespace PingTool
{
    // How the incident list is sorted when a column header is clicked: by the VALUES (dates, durations and counts as numbers, not
    // as the text on screen, where "9" comes after "10" and a date after the next one depending on the regional format).
    internal static class IncidentOrder
    {
        // The columns of IncidentsForm, in order.
        public const int Host = 0, Type = 1, Start = 2, End = 3, Duration = 4, Failed = 5, Cause = 6, Number = 7, Columns = 8;

        // Negative when a goes before b in ASCENDING order of the column. Equal values: the most recent start first, then the host, then
        // the kind and the number of the incident, so that the order is the same every time.
        public static int Compare(Incident a, Incident b, int column, DateTimeOffset now)
        {
            int c = column switch
            {
                Host => string.Compare(a.Host, b.Host, StringComparison.OrdinalIgnoreCase),   // an address or a host name: not prose, same order in every culture
                Type => a.Kind.CompareTo(b.Kind),
                Start => a.Start.CompareTo(b.Start),
                End => (a.End ?? DateTimeOffset.MaxValue).CompareTo(b.End ?? DateTimeOffset.MaxValue),   // an ongoing incident ends "last"
                Duration => a.Duration(now).CompareTo(b.Duration(now)),
                Failed => FailedOf(a).CompareTo(FailedOf(b)),
                Cause => CompareCause(a, b),
                Number => a.Occurrence.CompareTo(b.Occurrence),
                _ => 0,
            };
            if (c != 0) return c;

            c = b.Start.CompareTo(a.Start);
            if (c == 0) c = string.Compare(a.Host, b.Host, StringComparison.OrdinalIgnoreCase);
            // Same start on the same host (an outage and a slowdown noticed together, a host written in two cases): the kind, then the
            // number of the incident of that kind, which is never twice the same for one host: two different incidents are never equal,
            // so the sort (which does not keep the order it was given) puts them in the same order every time.
            if (c == 0) c = a.Kind.CompareTo(b.Kind);
            if (c == 0) c = a.Occurrence.CompareTo(b.Occurrence);
            // The number is only unique for ONE host as written: "Serveur" and "serveur" are two hosts of the log, each with an incident 1 of the
            // same kind at the same instant, which the comparison above (case ignored) would have found equal. The exact spelling decides last.
            return c != 0 ? c : string.CompareOrdinal(a.Host, b.Host);
        }

        // A slowdown has no failed pings: it counts as none, so it sorts below every outage.
        private static int FailedOf(Incident i) => i.Kind == IncidentKind.Outage ? i.FailedPings : -1;

        // What the "Cause / detail" column shows is the cause of an outage (words) and the figures of a slowdown (numbers). The outages go
        // before the slowdowns (as in the column Type); the outages are put in the order of their cause, as text (the program's own English
        // words), and the slowdowns in the order of their FIGURES, the loss and then the average: as text "950 ms" came after "1200 ms", and
        // a thousands separator of the regional format could change the order.
        private static int CompareCause(Incident a, Incident b)
        {
            int kinds = a.Kind.CompareTo(b.Kind);
            if (kinds != 0) return kinds;
            if (a.Kind == IncidentKind.Outage) return string.Compare(a.Cause, b.Cause, StringComparison.OrdinalIgnoreCase);

            int loss = a.LossPercent.CompareTo(b.LossPercent);
            return loss != 0 ? loss : (a.AvgMs ?? -1).CompareTo(b.AvgMs ?? -1);   // no average (a window with no reply) sorts first
        }

        // The keyboard way to the same sort (a column header takes no focus, so a click is out of reach without a mouse): Ctrl+1 is the
        // first column, Ctrl+2 the second... digit is the number on the key, 1 to 9. null when there is no such column.
        public static int? ColumnOfDigit(int digit, int columnCount) => digit >= 1 && digit <= columnCount ? digit - 1 : null;

        // What a click on a header does: another column starts ascending; the same column reverses.
        public static (int Column, bool Ascending) Click(int currentColumn, bool currentAscending, int clicked) =>
            clicked == currentColumn ? (clicked, !currentAscending) : (clicked, true);
    }
}
