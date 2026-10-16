using System.Globalization;
using System.Text;

namespace PingTool
{
    // Reads back the CSV that PingLog.WriteCsv and AutoLog write, so that a night recorded on disk can be looked at
    // again after PingTool was closed (or crashed): the same incident detection and the same report as live.
    internal static class PingLogReader
    {
        public const long MaxPlausibleRttMs = 3_600_000;   // one hour: no probe waits for an answer that long
        public const int MaxEntries = 2_000_000;   // about 23 days of one ping a second: a bigger file is not a PingTool log

        // Several files at once (one per host and day) are fine: the caller concatenates the entries.
        public static bool TryParse(string csv, out List<LogEntry> entries, out string error)
        {
            entries = new List<LogEntry>();
            error = "";
            // Row by row: a file that is far too big (or that is not a log at all) is refused at the limit, not after every row of it has
            // been turned into a list of strings.
            using var rows = Rows(csv.TrimStart('﻿')).GetEnumerator();

            if (!rows.MoveNext() || string.Join(",", rows.Current.Fields) != PingLog.CsvHeader)
            {
                error = "This is not a PingTool log: the first line should be \"" + PingLog.CsvHeader + "\".";
                return false;
            }

            while (rows.MoveNext())
            {
                // `line` is the line of the FILE where the record starts, as an editor numbers it (a field with a line break in it
                // makes the records after it come later than their rank).
                var (r, line, problem) = rows.Current;
                if (problem is not null) { error = $"Line {line}: {problem}"; return false; }
                if (r.Count == 1 && r[0].Length == 0) continue;   // blank line
                // A remark of the export (the log was incomplete): the mark in the first field and nothing in the others - alone (files written
                // before) or with the four empty fields that give it as many columns as the header. A ping never begins with the mark: its
                // first field is a date.
                if (r.Count >= 1 && r[0].StartsWith(PingLog.CommentMark, StringComparison.Ordinal) && r.Skip(1).All(f => f.Length == 0)) continue;
                if (r.Count != 5) { error = $"Line {line}: expected 5 fields, found {r.Count}."; return false; }
                if (!DateTimeOffset.TryParseExact(r[0], "yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                { error = $"Line {line}: the date \"{Clip(r[0])}\" is not in the log format."; return false; }

                long? rtt = null;
                if (r[3].Length > 0)
                {
                    if (!long.TryParse(r[3], NumberStyles.None, CultureInfo.InvariantCulture, out long ms))
                    { error = $"Line {line}: the round-trip time \"{Clip(r[3])}\" is not a number."; return false; }
                    // NumberStyles.None takes no sign, so a negative time (-1, the marker of a failure in memory) is already "not a number";
                    // what is bounded here is the other end: a time of years would drown every average of the replay.
                    if (ms > MaxPlausibleRttMs) { error = $"Line {line}: the round-trip time {ms.ToString(CultureInfo.InvariantCulture)} ms is not plausible (more than one hour)."; return false; }
                    rtt = ms;
                }

                if (r[1].Length == 0 || r[2].Length == 0) { error = $"Line {line}: the host or the status is empty."; return false; }
                // A ping that answered has its time (the writer gives "OK" only with one): a row that says OK and gives none is a damaged or
                // hand-made line, and would be replayed as a lost ping whose cause is "OK", which skews the loss and the incidents. The
                // other way round (a time on a row that names a failure) is read as the failure it names: nothing is skewed by it.
                if (rtt is null && Unprotect(r[2]) == "OK") { error = $"Line {line}: the status is OK but there is no round-trip time (a ping that answered has one)."; return false; }
                if (entries.Count >= MaxEntries) { error = "This file has more than " + MaxEntries.ToString("N0", CultureInfo.CurrentCulture) + " lines: too many to be a PingTool log."; return false; }
                entries.Add(new LogEntry(time, Unprotect(r[1]), Unprotect(r[2]), rtt, Unprotect(r[4])));
            }

            if (entries.Count == 0) { error = "This log has no ping in it."; return false; }
            return true;
        }

        // PingLog protects a field a spreadsheet would run as a formula by putting ' in front: take it off again,
        // but only when what follows would indeed have been protected (a host that really starts with ' keeps it).
        private static string Unprotect(string field) =>
            field.Length > 1 && field[0] == '\'' && PingLog.NeedsProtection(field[1..]) ? field[1..] : field;

        private static string Clip(string s) => s.Length <= 30 ? s : TextCut.Units(s, 30) + "...";

        // RFC 4180: quoted fields may hold commas, quotes ("") and line breaks.
        // One row at a time, as the reader asks for it (a row is handed over as soon as its line ends).
        // Each record comes with the line of the FILE where it starts (from 1, a CRLF or a lone CR or LF is one line break, inside a
        // quoted field too): a record is not a line once a field holds a line break.
        // A record that is not well formed carries what is wrong with it (Problem, null otherwise): text after the closing quote of a field
        // ("abc"def), or a quoted field still open when the file ends (the file was cut short, by a crash for one). Such a record is
        // refused by the reader instead of being taken for a ping whose last field happens to look right.
        private static IEnumerable<(List<string> Fields, int Line, string? Problem)> Rows(string text)
        {
            var row = new List<string>();
            var field = new StringBuilder();
            bool quoted = false, wasQuoted = false;
            (List<string> Fields, int Line, string? Problem)? finished = null;
            string? problem = null;
            int line = 1, rowStart = 1;

            void EndField() { row.Add(field.ToString()); field.Clear(); wasQuoted = false; }
            void EndRow() { EndField(); finished = (row, rowStart, problem); row = new List<string>(); problem = null; }

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                        else quoted = false;
                    }
                    else
                    {
                        field.Append(c);
                        // A line break held in a field: the physical line goes on although the record does not end.
                        if (c == '\n' || (c == '\r' && !(i + 1 < text.Length && text[i + 1] == '\n'))) line++;
                    }
                }
                else if (c == '"' && field.Length == 0 && !wasQuoted) { quoted = true; wasQuoted = true; }
                else if (c == ',') EndField();
                else if (c == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') i++; EndRow(); line++; rowStart = line; }
                else if (c == '\n') { EndRow(); line++; rowStart = line; }
                else
                {
                    if (wasQuoted) problem ??= "there is text after the closing quote of a field (the line is damaged).";
                    // RFC 4180: a field that does not begin with a quote cannot hold one. The writer (PingLog.Field) always quotes a field that
                    // holds a quote, so one met here, in the middle of a field, is a line that was damaged or written by something else:
                    // said, instead of taking it for a character of the host, the status or the detail.
                    else if (c == '"') problem ??= "there is a quote in a field that does not begin with one (the line is damaged).";
                    field.Append(c);
                }

                if (finished is { } done) { yield return done; finished = null; }
            }

            if (field.Length > 0 || row.Count > 0 || wasQuoted)   // last line without a line break
            {
                if (quoted) problem ??= "the file ends inside a quoted field: the log was cut short (a crash, a copy that did not finish). Delete this last line and open it again.";
                EndRow();
                yield return finished!.Value;
            }
        }
    }

    // What a replay gives back: one session per host (statistics, last 180 pings, state), the incidents, the period.
    // DuplicatesRemoved = identical pings that were present more than once in what was loaded and counted once.
    internal sealed record ReplayResult(Dictionary<string, HostSession> Sessions, IncidentLog Incidents, DateTimeOffset From, DateTimeOffset To, int DuplicatesRemoved = 0);

    internal static class LogReplay
    {
        // The pings go through the same HostMonitor and IncidentLog as live ones, in time order (a stable sort: two pings
        // of one instant keep the order of the file). The thresholds are the defaults: the log does not say which were used.
        // At least one ping: a replay has a first and a last time, and a window that opens it needs a host to show. The reader
        // (PingLogReader.TryParse) refuses a log with no ping, so the callers always have one; a caller that does not gets this said.
        public static ReplayResult Run(IReadOnlyCollection<LogEntry> entries)
        {
            ArgumentNullException.ThrowIfNull(entries);
            if (entries.Count == 0) throw new ArgumentException("A replay needs at least one ping: there is nothing to replay.", nameof(entries));

            var sorted = entries.Select((e, i) => (e, i)).OrderBy(x => x.e.Time).ThenBy(x => x.i).Select(x => x.e).ToList();
            // The same ping twice (an export and the automatic log of the same night, the same file opened twice, files that overlap)
            // is one ping: a LogEntry is a record, so "identical" is every field alike, the first kept. Counted, so it can be said.
            var ordered = sorted.Distinct().ToList();
            int duplicates = sorted.Count - ordered.Count;
            var sessions = new Dictionary<string, HostSession>(StringComparer.Ordinal);
            var incidents = new IncidentLog();
            var gapOf = GapLimits(ordered);
            var lastSeen = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

            foreach (var e in ordered)
            {
                if (!sessions.TryGetValue(e.Host, out var session)) sessions[e.Host] = session = new HostSession(e.Host);

                // A hole in the log (PingTool was closed, the PC slept, one file per day): the pings on either side of it are not
                // consecutive. Live, the run would have ended; here the incident open at the last ping before the hole ends there, and
                // the monitor starts again, instead of one incident that also counts the time when nothing was measured.
                if (lastSeen.TryGetValue(e.Host, out var before) && e.Time - before > gapOf[e.Host])
                {
                    incidents.CloseOpen(e.Host, before);
                    session.ApplyThresholds(HostMonitor.DefaultLatencyMs, HostMonitor.DefaultLossPercent, HostMonitor.DefaultDownAfter);
                }

                lastSeen[e.Host] = e.Time;

                bool ok = e.Status == "OK" && e.RttMs is not null;
                long ping = ok ? e.RttMs!.Value : -1;
                PingFailure? failure = ok ? null : new PingFailure(e.Status, e.Detail);
                session.Add(ping, failure, e.Time);
                var change = session.Monitor.Update(ping);
                incidents.Observe(e.Time, e.Host, ping, session.LastFailure, change, session.Monitor.WindowLossPercent, session.Monitor.WindowAvgMs);
            }

            return new ReplayResult(sessions, incidents, ordered[0].Time, ordered[^1].Time, duplicates);
        }

        // The silence after which two pings of one host are no longer consecutive: ten times its usual interval (the median of the
        // gaps between its pings), and never less than five minutes, so that a few missed pings are not a hole.
        public static readonly TimeSpan MinGap = TimeSpan.FromMinutes(5);
        public const int GapFactor = 10;
        public const int MinStepsForCadence = 5;                       // gaps between pings needed to call one of them "the usual one"
        public static readonly TimeSpan MaxGap = TimeSpan.FromHours(6);   // however slow the cadence, a silence this long is a hole

        internal static Dictionary<string, TimeSpan> GapLimits(IReadOnlyList<LogEntry> ordered)
        {
            var limits = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
            foreach (var host in ordered.GroupBy(e => e.Host, StringComparer.Ordinal))
            {
                var times = host.Select(e => e.Time).ToList();
                var steps = new List<TimeSpan>(times.Count);
                for (int i = 1; i < times.Count; i++) steps.Add(times[i] - times[i - 1]);
                steps.Sort();
                // A cadence is only read from enough steps: with two pings two days apart the only step IS the gap, and ten times it would
                // make a limit of twenty days that no hole could exceed. Too few steps, or a limit above MaxGap: the limits that hold anyway.
                var usual = steps.Count < MinStepsForCadence ? TimeSpan.Zero : steps[steps.Count / 2];
                // The product is only made when it cannot overflow: dates a few thousand years apart (the reader accepts any date) give a
                // step whose tenfold is longer than a TimeSpan can hold. A step above MaxGap / GapFactor makes a limit above MaxGap anyway.
                var limit = usual > MaxGap / GapFactor ? MaxGap : usual * GapFactor > MinGap ? usual * GapFactor : MinGap;
                limits[host.Key] = limit > MaxGap ? MaxGap : limit;
            }

            return limits;
        }
    }
}
