using System.Globalization;

namespace PingTool
{
    internal sealed record LogEntry(DateTimeOffset Time, string Host, string Status, long? RttMs, string Detail);

    // Timestamped record of every ping since Start, for export. Capped so a
    // session left running for days cannot eat the memory: the oldest go first.
    internal sealed class PingLog
    {
        public const int MaxEntries = 100_000;
        private static readonly char[] CsvSpecials = { ',', '"', '\r', '\n' };
        private readonly Queue<LogEntry> entries = new();
        private long dropped;

        // Every use of the queue and of the count of those let go is under the lock of `entries`. Today the window's thread is the only one
        // that uses the log (the answers of the probes come back to it), which is a fact about the callers and not a guarantee: a queue
        // that is changed while another thread copies or walks it throws, or hands out a copy that is not one, and the count and the queue
        // would no longer go together. The lock is held for the length of one enqueue, one copy: never while a file is written.
        public int Count { get { lock (entries) return entries.Count; } }

        // Oldest first, as it is NOW: a copy, so that nothing handed out can be cast back to the queue and changed behind Add and Clear
        // (the cap and Dropped would no longer say what the log holds), and a window that keeps it while pings go on sees a fixed
        // log, as its comment says. Each call copies: take it once, not once per item.
        public IReadOnlyCollection<LogEntry> Entries { get { lock (entries) return entries.ToArray(); } }

        // How many of the oldest entries were let go since the last Clear, to say so instead of letting
        // an export or a timeline pass for the whole session.
        public long Dropped { get { lock (entries) return dropped; } }

        // In the language of the window (Loc), the numbers in the regional format.
        public string? DroppedNote => Dropped == 0 ? null : Loc.T("log.dropped",
            MaxEntries.ToString("N0", CultureInfo.CurrentCulture), Dropped.ToString("N0", CultureInfo.CurrentCulture));

        public void Clear()
        {
            lock (entries)
            {
                entries.Clear();
                dropped = 0;
            }
        }

        // Returns the entry, so the automatic log writes exactly what the in-memory log holds.
        public LogEntry Add(DateTimeOffset time, string host, string status, long? rttMs, string detail = "")
        {
            var entry = new LogEntry(time, host, status, rttMs, detail);
            lock (entries)
            {
                entries.Enqueue(entry);
                while (entries.Count > MaxEntries)
                {
                    entries.Dequeue();
                    dropped++;
                }
            }

            return entry;
        }

        public const string CsvHeader = "timestamp,host,status,rtt_ms,detail";

        // notes: the notes of the person, kept in the file as remark rows after the header (see NoteStatus).
        public void WriteCsv(TextWriter writer, IEnumerable<NetworkEvent>? notes = null)
        {
            // One picture of the log for the whole file, taken now: the entries and the count of those let go go together (the remark under
            // the header must say how many are missing from THESE rows), and the queue is not walked while a slow writer is at work, where a
            // ping added meanwhile would end the walk with an exception, in the middle of the file.
            LogEntry[] snapshot; long letGo;
            lock (entries)   // both in one look: the entries and the count of those let go are of the same moment
            {
                snapshot = entries.ToArray();
                letGo = dropped;
            }

            // CR LF written explicitly: the writer belongs to the caller, whose own line ending is none of our business.
            writer.Write(CsvHeader + "\r\n");
            // A log that let its oldest pings go says so IN the file (the window said it once, to whoever exported): whoever receives
            // the file cannot tell a whole session from the end of one. A row under the header with the same five columns, the remark in
            // the first and the other four empty, which the reader skips (PingLogReader.CommentMark) and a spreadsheet shows as a line of
            // text; a tool that wants as many fields in every row as the header has them finds them.
            if (letGo > 0) writer.Write(IncompleteRow(letGo) + "\r\n");
            foreach (var note in (notes ?? Array.Empty<NetworkEvent>()).Where(n => n.Note)) writer.Write(NoteRow(note.ToRemark()) + "\r\n");
            foreach (var e in snapshot) writer.Write(CsvLine(e) + "\r\n");
        }

        // The remark row for a log that let `dropped` of its oldest pings go (also the first row of the measures in a report archive).
        // Five fields like every row (four empty ones after the remark). Files written before had the remark alone: the reader takes both.
        // Written WITHOUT a comma (plain digits, a semicolon): a field with a comma is put in quotes, and the line would then begin with a
        // quote instead of the mark, so a tool that tells a remark by the first character of the raw line (as many do) would take it for a
        // ping that is badly formed. The reader takes both forms, this one and the quoted one that earlier versions wrote.
        public static string IncompleteRow(long dropped) =>
            Field(CommentMark + " Incomplete: only the latest " + MaxEntries.ToString(CultureInfo.InvariantCulture)
                + " pings are in this file; the " + dropped.ToString(CultureInfo.InvariantCulture) + " older ones were no longer kept.") + ",,,,";

        // The mark of a row that is a remark and not a ping.
        public const string CommentMark = "#";

        // A note of the person ("Mark an event") kept in the file, as a remark row: "# note {json}" in the first field, the other four empty,
        // so that a reader that does not know notes skips it like the other remarks. Queued for the automatic log as a LogEntry with this
        // status (its Host says which file takes it, its Detail is the JSON of NetworkEvent.ToRemark): never a ping, never in the counts.
        public const string NoteStatus = "#note";

        public static string NoteRow(string json) => Field(CommentMark + " " + NetworkEvent.RemarkTag + json) + ",,,,";

        // One row, shared by the export and the automatic log so the two files read the same.
        public static string CsvLine(LogEntry e) => e.Status == NoteStatus ? NoteRow(e.Detail) : string.Join(",",
            e.Time.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            Field(e.Host),
            Field(e.Status),
            e.RttMs?.ToString(CultureInfo.InvariantCulture) ?? "",
            Field(e.Detail));

        // What gets a ' in front of it when written: a value that starts like a formula, and also a value that already starts with ' and
        // would then start like a formula once that ' is taken off ("'=x" written as it is would be read back as "=x": the ' that belongs to
        // the value would be taken for the one the writer added). Read back by PingLogReader.Unprotect with the same rule.
        // Taking the ' off one at a time (a call and a copy of the text for each) is the same as taking all the leading ones off at once and
        // looking at what follows: a long run of them, which a log file one opens can hold, made that quadratic in time and deep in the stack.
        internal static bool NeedsProtection(string value)
        {
            if (StartsLikeFormula(value)) return true;

            int quotes = 0;
            while (quotes < value.Length && value[quotes] == '\'') quotes++;
            return quotes > 0 && quotes < value.Length && StartsLikeFormula(value.AsSpan(quotes));
        }

        // A spreadsheet runs a field as a formula when it begins with = + - @, or with a tab or a CR.
        // Looking at the very first character is not enough: spaces, a no-break space, a line feed, a
        // zero-width space or a byte-order mark in front hide the formula character from a check on
        // value[0] while a spreadsheet may still skip them. So: the first VISIBLE character decides.
        internal static bool StartsLikeFormula(ReadOnlySpan<char> value)
        {
            if (value.Length > 0 && (value[0] == '\t' || value[0] == '\r')) return true;

            foreach (char c in value)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
                return "=+-@".Contains(c, StringComparison.Ordinal);
            }

            return false;
        }

        // RFC 4180 quoting, plus a leading ' on anything a spreadsheet would
        // run as a formula: the host is whatever the user typed.
        private static string Field(string value)
        {
            if (NeedsProtection(value)) value = "'" + value;
            if (value.IndexOfAny(CsvSpecials) < 0) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
