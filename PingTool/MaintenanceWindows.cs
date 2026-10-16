using System.Globalization;

namespace PingTool
{
    // Times when a fault is expected (a router restart, a provider's night works): "Sat,Sun 00:00-06:00", "Mon-Fri 02:00-03:30", "22:00-02:00"
    // (every day; a window that crosses midnight belongs to the day it starts). Edited in settings.json (MaintenanceWindows). During a
    // window the alerts are not raised (sound, balloon, webhooks), and an incident that BEGINS in one is marked planned in the report, which
    // also gives the availability without them. Nothing is hidden: the measures, the log and the incidents are the same.
    internal sealed class MaintenanceWindows
    {
        public const int MaxWindows = 20;

        private readonly List<(HashSet<DayOfWeek> Days, TimeSpan From, TimeSpan To)> windows = new();

        public static readonly MaintenanceWindows None = new();

        public bool Any => windows.Count > 0;

        // The windows of the list, and the entries that could not be read (each is said, not dropped in silence).
        public static MaintenanceWindows Parse(IEnumerable<string>? entries, out List<string> ignored)
        {
            var result = new MaintenanceWindows();
            ignored = new List<string>();
            foreach (string entry in entries ?? Array.Empty<string>())
            {
                if (result.windows.Count >= MaxWindows || !TryParseOne(entry, out var days, out var from, out var to)) ignored.Add(entry);
                else result.windows.Add((days, from, to));
            }

            return result;
        }

        private static readonly string[] DayNames = { "sun", "mon", "tue", "wed", "thu", "fri", "sat" };

        internal static bool TryParseOne(string? text, out HashSet<DayOfWeek> days, out TimeSpan from, out TimeSpan to)
        {
            days = new HashSet<DayOfWeek>();
            from = to = default;
            string t = (text ?? "").Trim();
            int space = t.LastIndexOf(' ');
            string range = space < 0 ? t : t[(space + 1)..];
            string dayPart = space < 0 ? "" : t[..space].Trim();

            int dash = range.IndexOf('-', StringComparison.Ordinal);
            if (dash <= 0 || !TryTime(range[..dash], out from) || !TryTime(range[(dash + 1)..], out to) || from == to) return false;

            if (dayPart.Length == 0)
            {
                for (int d = 0; d < 7; d++) days.Add((DayOfWeek)d);
                return true;
            }

            foreach (string piece in dayPart.Split(',', StringSplitOptions.TrimEntries))
            {
                var ends = piece.Split('-', StringSplitOptions.TrimEntries);
                int a = Array.IndexOf(DayNames, ends[0].ToLowerInvariant());
                int b = ends.Length == 2 ? Array.IndexOf(DayNames, ends[1].ToLowerInvariant()) : a;
                if (ends.Length > 2 || a < 0 || b < 0) return false;

                // Mon-Fri runs forward from the first to the last (Sat-Mon is Sat, Sun, Mon).
                for (int d = a; ; d = (d + 1) % 7)
                {
                    days.Add((DayOfWeek)d);
                    if (d == b) break;
                }
            }

            return true;
        }

        // "02:00", "2:00" and "24:00" (the end of a day) - not "2", not "02:60".
        private static bool TryTime(string text, out TimeSpan time)
        {
            time = default;
            var parts = text.Trim().Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int h)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int m) || parts[1].Length != 2 || m > 59) return false;
            if (h > 24 || (h == 24 && m != 0)) return false;
            time = new TimeSpan(h, m, 0);
            return true;
        }

        // Is this moment (read in the computer's time zone) inside a window? A window that crosses midnight runs into the next day.
        public bool IsActive(DateTimeOffset moment)
        {
            if (windows.Count == 0) return false;

            var local = moment.ToLocalTime();
            var now = local.TimeOfDay;
            foreach (var (days, from, to) in windows)
            {
                if (from < to)
                {
                    if (days.Contains(local.DayOfWeek) && now >= from && now < to) return true;
                }
                else
                {
                    // Across midnight: from the evening of a listed day until the morning after it.
                    if (days.Contains(local.DayOfWeek) && now >= from) return true;
                    if (days.Contains(local.AddDays(-1).DayOfWeek) && now < to) return true;
                }
            }

            return false;
        }
    }
}
