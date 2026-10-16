using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PingTool
{
    // Writes every ping to disk within a few seconds, so that a night of monitoring is on disk even if the program is killed: a crash
    // loses at most what was waiting for the next Flush (the last ~5 seconds, the timer's period), and, while the folder cannot be
    // written, the pings that waited longer than the queue holds (MaxPending: they are counted and said, see DropNote). Not "nothing":
    // the entries are in memory until they are written.
    // One CSV per host per day (a new file at midnight), same columns as the manual export.
    //
    // Entries wait in memory and are written by Flush (the form calls it every few seconds, at Stop
    // and on exit). A file that cannot be written right now - a spreadsheet holding it, a full
    // disk, a missing share - is not an error that stops monitoring: its entries stay queued and
    // go out, in order, at the next Flush that works.
    internal sealed class AutoLog
    {
        // What a stuck folder can cost in memory: 20 000 entries, i.e. about 5 h 33 min of ONE host at one ping a second (1 000 entries
        // = 16.7 min), shared by all the hosts watched: with 4 hosts it is about 1 h 23 min. The oldest are dropped past this, like the
        // in-memory log does.
        public const int MaxPending = 20_000;
        private const int MaxNameLength = 80;

        private readonly string folder;
        private readonly List<LogEntry> pending = new();

        // The queue is touched by the window (Add, on its thread) and by a flush running in the background: this lock covers every
        // use of `pending`, and only that, never the disk. Flushes take turns on the other lock, so two of them never write the same rows.
        private readonly object queue = new();
        private readonly object flushing = new();
        private int running;   // 1 while a background flush is in flight

        private readonly Action<string>? beforeDiskAccess;   // called before each file is touched: a seam to make the disk slow in a test
        private readonly Action<FileStream>? afterWrite;      // called after the rows were written: a seam to make the disk fail then
        private readonly Action<FileStream, long> cutBack;    // brings a file back to a length (a seam to make that fail)

        // A write that failed AFTER part of its rows reached the disk, and could not be cut back (the disk was still failing): where the file
        // was before, and what was being written. The batch stays queued, so the next write to that file first takes these rows back, or the
        // retry would append them a second time. Only touched under `flushing`.
        private readonly Dictionary<string, (long Start, byte[] Written)> unrecovered = new();

        // The files this run has already written to: the first time, CutPartialLine reads the whole file (see `deep`). Under `flushing`.
        private readonly HashSet<string> touched = new(StringComparer.OrdinalIgnoreCase);

        public AutoLog(string folder, Action<string>? beforeDiskAccess = null, Action<FileStream>? afterWrite = null, Action<FileStream, long>? cutBack = null)
        {
            this.folder = folder;
            this.beforeDiskAccess = beforeDiskAccess;
            this.afterWrite = afterWrite;
            this.cutBack = cutBack ?? ((stream, length) => stream.SetLength(length));
        }

        // Takes back the rows of a failed write that stayed on disk. True when the file can be written again. Under the file's gate.
        // The rows are cut only if what follows `start` is still exactly (the beginning of) what was written: if another window added
        // rows meanwhile, cutting would destroy them, so the rows are left (a duplicate is the lesser harm) and the user is told.
        private bool TakeBack(string path, (long Start, byte[] Written) failed, out string? note)
        {
            note = null;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                long after = stream.Length - failed.Start;
                if (after <= 0) { unrecovered.Remove(path); return true; }   // nothing of it left on disk
                var tail = new byte[after];
                stream.Position = failed.Start;
                stream.ReadExactly(tail);
                if (after > failed.Written.Length || !failed.Written.AsSpan(0, tail.Length).SequenceEqual(tail))
                {
                    unrecovered.Remove(path);
                    note = Loc.T("log.takeback.changed", Path.GetFileName(path));
                    return true;
                }

                cutBack(stream, failed.Start);
                unrecovered.Remove(path);
                return true;
            }
            catch (FileNotFoundException) { unrecovered.Remove(path); return true; }
            catch (Exception ex) when (IsFileFailure(ex)) { note = ex.Message; return false; }
        }

        public static string DefaultFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PingTool", "logs");

        public int Pending { get { lock (queue) return pending.Count; } }

        public string Folder => folder;

        // What the next run logs to, given the log of the run before (null = it had none) and the folder wanted (null = no log).
        // Same folder: the same log goes on, queue included (the file names are per host and day, so nothing changes). Another
        // folder, or no log at all: what the old one still holds is not thrown away, it goes into `retired` and keeps being retried.
        // The retired logs together are kept within MaxRetiredPending entries (CapRetired): what is given up is said through `warn`.
        public static AutoLog? Next(AutoLog? previous, string? folder, List<AutoLog> retired, Action<string>? warn = null)
        {
            if (previous is not null && folder is not null && string.Equals(previous.folder, folder, StringComparison.OrdinalIgnoreCase)) return previous;
            if (previous is { Pending: > 0 })
            {
                retired.Add(previous);
                if (CapRetired(retired) is { } note) warn?.Invoke(note);
            }

            return folder is null ? null : new AutoLog(folder);
        }

        // For the closing of the window: what is queued is written NOW, but the window never waits longer than `limit` for a disk that does
        // not answer (a network share that went away holds a write for tens of seconds, once per file): the work goes on a pool thread
        // and what it has not finished is left (the process ends anyway). True when everything got out in time.
        public static bool FlushWithin(AutoLog? current, List<AutoLog> retired, TimeSpan limit)
        {
            // Both are tried whatever the first gives; a retired log that is still there afterwards could not get everything out.
            var work = Task.Run(() =>
            {
                FlushRetired(retired);
                bool currentOut = current?.Flush() ?? true;
                return currentOut && retired.Count == 0;
            });
            try { return work.Wait(limit) && work.Result; }
            catch (AggregateException) { return false; }
        }

        public static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(3);

        // One try for each retired log; the ones that got everything out are forgotten.
        public static void FlushRetired(List<AutoLog> retired) => retired.RemoveAll(r => r.Flush());

        // The timer's try for the retired logs, off the window's thread: each log has at most ONE flush going at a time (FlushInBackground
        // says "still going" at once instead of starting another). Without that, a share that stops answering holds the first flush for tens
        // of seconds and every tick sends one more pool thread to wait for the same lock, for hours. The logs that got everything out are
        // returned; one whose flush is still going is not (it reports for itself when it ends, and is tried again at the next tick).
        public static async Task<List<AutoLog>> FlushRetiredInBackground(IReadOnlyList<AutoLog> retired)
        {
            var attempts = retired.Select(r => (Log: r, Done: r.FlushInBackground())).ToList();
            var emptied = new List<AutoLog>();
            foreach (var (log, done) in attempts)
                if (await done == true) emptied.Add(log);

            return emptied;
        }

        // Why the last Flush could not write everything; null when it did. Written by a flush that may run in the background.
        private volatile string? lastError;
        public string? LastError => lastError;

        // The pings that were thrown away because the folder stayed unwritable too long: how many, and the time they span. The log
        // would otherwise look complete with hours missing.
        public int Dropped { get; private set; }
        public DateTimeOffset? DroppedFrom { get; private set; }   // the first and the last ping dropped, whatever was written between
        public DateTimeOffset? DroppedTo { get; private set; }

        // The gaps themselves: a folder blocked, repaired, blocked again leaves two, and what was written in between is on the disk. A drop
        // that follows the previous one with nothing written between them goes on the same gap. Written under `queue` (by Add and by
        // CapRetired, both on the window's thread today), and read under it too: a list that is read while it changes can throw, or give
        // the last gap half changed, and nothing but a comment kept the readers on the window's thread.
        private readonly List<(DateTimeOffset From, DateTimeOffset To)> gaps = new();
        private volatile bool writtenSinceDrop;

        public int Gaps { get { lock (queue) return gaps.Count; } }

        // Whether a write error was on record when pings were given up. If none was, the queue filled because writing did not keep up (a
        // slow or stalled drive), not because the folder refused the writes: the sentence must not say "not writable" then.
        private bool droppedWhileFailing;

        // "from 10/16/2026 4:00:00 AM to 10/16/2026 4:05:00 AM" in the language and the regional format of the person.
        private static string SpanText(DateTimeOffset from, DateTimeOffset to) =>
            Loc.T("log.drop.span", from.ToLocalTime().ToString("G", CultureInfo.CurrentCulture), to.ToLocalTime().ToString("G", CultureInfo.CurrentCulture));

        // The sentence for the user; null when nothing was lost.
        public string? DropNote
        {
            get
            {
                // One look at the count, the reason and the gaps together, under the lock that the drops are made under: the sentence
                // never counts more pings than the gaps it names, or names a gap that was changing.
                lock (queue)
                {
                    if (Dropped == 0) return null;
                    string reason = Loc.T(droppedWhileFailing ? "log.drop.reason.failing" : "log.drop.reason.slow");
                    string Span((DateTimeOffset From, DateTimeOffset To) g) => SpanText(g.From, g.To);
                    if (gaps.Count == 1)
                        return Loc.T("log.drop.one", Dropped, Span(gaps[0]), reason);

                    // At most three are named; the rest is counted, so the sentence stays one a balloon can hold.
                    string named = string.Join("; ", gaps.Take(3).Select(Span)) + (gaps.Count > 3 ? Loc.T("log.drop.more", gaps.Count - 3) : "");
                    return Loc.T("log.drop.many", Dropped, reason, gaps.Count, named);
                }
            }
        }

        // On the window's thread: it is also the only one that writes Dropped, DroppedFrom and DroppedTo.
        public void Add(LogEntry entry)
        {
            lock (queue)
            {
                pending.Add(entry);
                int over = pending.Count - MaxPending;
                if (over > 0) DropOldestLocked(over);
            }
        }

        // The oldest `over` entries are given up (the folder stayed unwritable too long, or writing did not keep up and the memory budget is
        // spent: told apart by whether a write error was on record, see droppedWhileFailing). Under `queue`.
        // The entries being written right now (the batch a flush took, always the front of `pending`) are not given up while the write goes on:
        // if it succeeds they are on the disk, and counting them lost would announce a gap in a log that has no hole. The ones after them go
        // instead, so the queue may stand above MaxPending by the size of the batch until the write ends. Under `queue`.
        private int inFlight;

        // Returns what was given up (first and last time, how many); Count is 0 when everything that could go is being written.
        private (DateTimeOffset From, DateTimeOffset To, int Count) DropOldestLocked(int over)
        {
            int skip = Math.Min(inFlight, pending.Count);
            over = Math.Min(over, pending.Count - skip);
            if (over <= 0) return default;

            var from = pending[skip].Time;
            var to = pending[skip + over - 1].Time;
            DroppedFrom ??= from;
            DroppedTo = to;
            // Same gap when nothing was written since the last drop; a new one when some pings reached the disk in between.
            if (gaps.Count > 0 && !writtenSinceDrop) gaps[^1] = (gaps[^1].From, to);
            else gaps.Add((from, to));
            writtenSinceDrop = false;
            if (lastError is not null) droppedWhileFailing = true;
            Dropped += over;
            pending.RemoveRange(skip, over);
            return (from, to, over);
        }

        // What all the retired logs together may hold in memory: one log is capped at MaxPending, and a folder that stays unwritable
        // through several runs would otherwise add one more full log each time, without end.
        public const int MaxRetiredPending = MaxPending;

        // Keeps the retired logs within MaxRetiredPending entries together, giving up the OLDEST pings first (a log that is emptied
        // is forgotten). Returns the sentence for the user, or null when nothing had to be given up. On the window's thread.
        internal static string? CapRetired(List<AutoLog> retired)
        {
            int lost = 0;
            DateTimeOffset? from = null, to = null;
            // One pass, the oldest log first: what a log holds that is being written right now cannot be given up (see inFlight), so it is
            // left and the next log is looked at; the cap is held again at the next try.
            foreach (var log in retired.ToList())
            {
                int total = retired.Sum(r => r.Pending);
                if (total <= MaxRetiredPending) break;

                lock (log.queue)
                {
                    // Counted under the lock that guards the queue: a flush in the background can have written (and removed) entries since
                    // `total` was read, and a number taken before the lock could be longer than what is left, or give up pings that the
                    // flush has just made unnecessary to give up. So the figures are taken again here: this log's, and the others' (each read
                    // under its own lock; a flush holds the lock of ONE log at a time and only the window's thread caps, so no cycle of waits).
                    int others = 0;
                    foreach (var other in retired)
                        if (!ReferenceEquals(other, log)) others += other.Pending;
                    int drop = ToGiveUp(log.pending.Count, others, MaxRetiredPending);
                    if (drop > 0 && log.DropOldestLocked(drop) is { Count: > 0 } gone)
                    {
                        from ??= gone.From;
                        to = gone.To;
                        lost += gone.Count;
                    }
                }

                if (log.Pending == 0) retired.Remove(log);
            }

            return lost == 0 ? null : Loc.T("log.retired.cap", lost, SpanText(from!.Value, to!.Value), MaxRetiredPending);
        }

        // How many of the `here` pings of one log go so that the retired logs together keep at most `cap`: what exceeds the cap, and never
        // more than this log holds (the others are looked at next).
        internal static int ToGiveUp(int here, int elsewhere, int cap) => Math.Clamp(here + elsewhere - cap, 0, here);

        // 6 bytes of the hash (12 hex characters, 48 bits), not 3: two typed hosts that change into the same name share a file only if their
        // hashes agree too, and with 24 bits a few thousand such hosts would already make that likely. (Logs written by earlier versions
        // for such a host keep their old name, with the short suffix.)
        private const int HashBytes = 6;

        // "PingTool-8.8.8.8-2026-10-03.csv". The host is whatever the user typed (tcp://x:443,
        // http://x/a?b): anything a file name cannot hold becomes "_", and a hash of the typed host ("-1a2b3c4d5e6f") is then added.
        public static string FileName(string host, DateTimeOffset time)
        {
            var name = new StringBuilder();
            foreach (char c in host.Trim())
                name.Append(char.IsControl(c) || "\\/:*?\"<>|".Contains(c, StringComparison.Ordinal) ? '_' : c);

            string typed = host.Trim();
            string safe = name.ToString().Trim('.', ' ');
            if (safe.Length > MaxNameLength) safe = TextCut.Units(safe, MaxNameLength).TrimEnd('.', ' ');   // a file name never ends on half an emoji
            if (safe.Length == 0) safe = "host";

            // A name that had to be changed (a character replaced, cut short) could be the same for another host ("x:1" and "x_1"):
            // a short hash of what was typed keeps them apart. A name that is the host itself stays as it is, readable.
            if (safe != typed)
                safe += "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(typed)), 0, HashBytes).ToLowerInvariant();

            return "PingTool-" + safe + "-" + time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv";
        }

        // What a file that cannot be written throws: a full disk comes out as ArgumentOutOfRangeException on some platforms.
        private static bool IsFileFailure(Exception ex) =>
            ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;

        // A power cut or a crash in the middle of an earlier write leaves the file ending in half a line (no line end after it). The
        // next rows would continue that line and spoil two rows at once, so what follows the last complete line is cut off before
        // appending: it was a half row that nothing can read, and the rows it belonged to were lost with that crash anyway. The check
        // reads the end of the file only; the append itself keeps FileMode.Append (two windows can log the same host at once).
        // A line feed INSIDE a quoted field (a detail that holds one) is not the end of a row: `deep` reads the whole file once, following
        // the quotes, to find the last line end that is outside them, so that a crash in the middle of such a field is cut back to the
        // row before it. It is done the first time a file is touched by this run (a crash is what it recovers from, and a crash ends the
        // run); the other flushes only look at the end of the file.
        internal static void CutPartialLine(string path, bool deep = true)
        {
            if (!File.Exists(path)) return;

            long keep;
            byte[]? tail = null;   // what follows the last line end, when it is short enough to be a row
            using (var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long length = read.Length;
                if (length == 0) return;

                keep = 0;
                if (deep) keep = LastRecordEnd(read);
                else
                {
                    var block = new byte[4096];
                    for (long end = length; end > 0 && keep == 0; end -= block.Length)
                    {
                        long from = Math.Max(0, end - block.Length);
                        read.Position = from;
                        int n = read.Read(block, 0, (int)(end - from));
                        int at = Array.LastIndexOf(block, (byte)'\n', n - 1, n);
                        if (at >= 0) keep = from + at + 1;
                    }
                }

                if (keep == length) return;   // ends on a line end: nothing partial

                if (length - keep <= MaxRowBytes)
                {
                    tail = new byte[length - keep];
                    read.Position = keep;
                    read.ReadExactly(tail);
                }
            }

            // A last row that is COMPLETE but has no line end (the file was opened and saved by an editor or a spreadsheet that does
            // not write a final one) is a real ping: it gets its line end instead of being cut. Only what does not read as a whole row
            // is a half row of a crash. ponytail: a crash that cut only the end of the last field (the detail) leaves a row that still
            // reads as whole, and is kept with its shortened detail.
            if (tail is not null && IsWholeRow(tail))
            {
                using var end = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                end.Write(tail[^1] == (byte)'\r' ? "\n"u8 : "\r\n"u8);
                return;
            }

            using var write = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            write.SetLength(keep);
        }

        // The position after the last line feed that is outside a quoted field (RFC 4180: a quote opens or closes, a doubled one does both
        // and changes nothing); 0 when there is none.
        private static long LastRecordEnd(FileStream read)
        {
            read.Position = 0;
            var buffer = new byte[64 * 1024];
            bool inQuotes = false;
            long position = 0, boundary = 0;
            int n;
            while ((n = read.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    if (buffer[i] == (byte)'"') inQuotes = !inQuotes;
                    else if (buffer[i] == (byte)'\n' && !inQuotes) boundary = position + i + 1;
                }

                position += n;
            }

            return boundary;
        }

        // More than this after the last line end is not a row of ours (a row is a timestamp, a host, a status, a time and a short detail).
        private const int MaxRowBytes = 64 * 1024;

        // The text reads as exactly one row of a PingTool log (the five fields, a date, a number or nothing, a host and a status).
        private static bool IsWholeRow(byte[] text) =>
            text.Length > 0
            && text.Count(b => b == (byte)'"') % 2 == 0   // a quote left open is a field cut short, which the reader would still accept
            && PingLogReader.TryParse(PingLog.CsvHeader + "\r\n" + new UTF8Encoding(false).GetString(text), out var entries, out _)
            && entries.Count == 1;

        // A lock between the windows (processes) of one user that write the same file: a named mutex per file, taken for the length of a
        // flush of that file, which is milliseconds. FileMode.Append alone does not do it: the position is kept by each process, so two of
        // them appending at once write over each other. ponytail: Local (this user's session) is the scope, as the logs are in the user's own folder.
        private sealed class FileGate : IDisposable
        {
            private readonly Mutex mutex;
            private FileGate(Mutex mutex) => this.mutex = mutex;

            public static FileGate? TryEnter(string path, TimeSpan wait)
            {
                string name = "PingTool.AutoLog." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant())));
                var mutex = new Mutex(false, name);
                try
                {
                    if (!mutex.WaitOne(wait)) { mutex.Dispose(); return null; }
                }
                catch (AbandonedMutexException)
                {
                    // The other window died holding it: the lock is ours, and CutPartialLine deals with a row it left half written.
                }
                catch
                {
                    // Any other failure of the wait: the lock is not ours, and the handle must not be left open.
                    mutex.Dispose();
                    throw;
                }

                return new FileGate(mutex);
            }

            public void Dispose()
            {
                // The mutex belongs to the thread that took it. Released from another one (the gate is taken and given back in one
                // method on one thread today), ReleaseMutex throws, and that exception would skip the Dispose below (a handle left open),
                // leave the flush before the rows it wrote are taken off the queue (they would be written a second time), and hide the
                // error that was on its way. Closing the handle frees the name when it was the last one, so the lock does not outlive it.
                try { mutex.ReleaseMutex(); }
                catch (ApplicationException) { }   // "called from an unsynchronized block of code": not ours to release
                finally { mutex.Dispose(); }
            }
        }

        // A flush that does not hold the window up: the disk work (which a share that went away makes last tens of seconds, once per
        // file) runs on a pool thread, one at a time. Null = one is already running, nothing was started; else whether everything pending
        // is on disk when it ends. The caller reads the result back on its own thread (await).
        public Task<bool?> FlushInBackground()
        {
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return Task.FromResult<bool?>(null);
            return Task.Run<bool?>(() =>
            {
                try { return Flush(); }
                finally { Volatile.Write(ref running, 0); }
            });
        }

        // True when everything pending is on disk. Waits for a flush in progress (Stop and exit want what is queued written NOW).
        public bool Flush()
        {
            lock (flushing) return FlushOnce();
        }

        private bool FlushOnce()
        {
            try { return FlushCore(); }
            finally { lock (queue) inFlight = 0; }   // whatever happened to the write, nothing is "being written" any more
        }

        private bool FlushCore()
        {
            lastError = null;
            // What is queued now; what arrives while the disk is being written waits for the next flush. The batch is the front of the queue
            // and stays in it until the write is over; it is not given up meanwhile (see inFlight).
            List<LogEntry> batch;
            lock (queue)
            {
                batch = pending.ToList();
                inFlight = batch.Count;
            }

            if (batch.Count == 0) return true;

            // Distinct hosts get distinct files (FileName adds a hash when it had to change the name); rows carry their host anyway.
            var written = new HashSet<LogEntry>(ReferenceEqualityComparer.Instance);
            // FileName once per host and day, not once per pending entry: with a stuck folder this runs every few seconds
            // over up to MaxPending entries.
            var names = new Dictionary<(string, DateOnly), string>();
            foreach (var group in batch.GroupBy(e =>
            {
                var key = (e.Host, DateOnly.FromDateTime(e.Time.DateTime));
                if (!names.TryGetValue(key, out var name)) names[key] = name = FileName(e.Host, e.Time);
                return name;
            }))
            {
                try
                {
                    beforeDiskAccess?.Invoke(group.Key);
                    Directory.CreateDirectory(folder);
                    string path = Path.Combine(folder, group.Key);
                    // Two windows can log the same host into the same file: what one does (cut a half row, append, take its batch back
                    // after a failure) must not interleave with what the other does. A window that cannot get its turn keeps its rows
                    // for the next flush, as for a file a spreadsheet holds.
                    using var gate = FileGate.TryEnter(path, TimeSpan.FromMilliseconds(250));
                    if (gate is null)
                    {
                        lastError = Loc.T("log.other.window");
                        continue;
                    }

                    if (unrecovered.TryGetValue(path, out var failed))
                    {
                        bool back = TakeBack(path, failed, out string? why);
                        if (!back)
                        {
                            lastError = Loc.T("log.takeback.wait", Path.GetFileName(path), why ?? "");
                            continue;
                        }

                        if (why is not null) lastError = why;
                    }

                    // The first time a file is written to, the whole of it is read (a row cut by a crash can sit inside a quoted field). The file
                    // counts as done only once that worked: if reading or repairing it fails, the next try reads it whole again.
                    CutPartialLine(path, deep: !touched.Contains(path));
                    touched.Add(path);
                    // ReadWrite sharing lets a spreadsheet that does not lock the file read it meanwhile.
                    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    long start = stream.Length;
                    bool isNew = start == 0;
                    var text = new StringBuilder();
                    if (isNew) text.Append(PingLog.CsvHeader).Append("\r\n");
                    foreach (var e in group) text.Append(PingLog.CsvLine(e)).Append("\r\n");
                    byte[] body = new UTF8Encoding(false).GetBytes(text.ToString());
                    // The byte-order mark makes a spreadsheet read the file as UTF-8; once, at the start.
                    byte[] bytes = isNew ? new UTF8Encoding(true).GetPreamble().Concat(body).ToArray() : body;
                    try
                    {
                        stream.Write(bytes);
                        // Flush(true) asks the operating system to commit the bytes to the disk itself: Flush() only empties the program's own
                        // buffer, and a power cut right after would lose rows that the log had counted as written (once per file per flush,
                        // every few seconds: the cost is a few milliseconds, the same call Settings.Save makes).
                        stream.Flush(true);
                        afterWrite?.Invoke(stream);
                    }
                    catch (Exception ex) when (IsFileFailure(ex))
                    {
                        // A full disk or a share that drops can leave part of the batch on disk before it throws. The batch
                        // stays queued, so cut the file back to where it was: otherwise the retry would append the rows a
                        // second time after a half-written line (and a new file would lose its header). If that fails too,
                        // it is remembered (`unrecovered`): the next write to this file takes the rows back first, or does not happen.
                        try { cutBack(stream, start); }
                        catch (Exception cut) when (IsFileFailure(cut))
                        {
                            unrecovered[path] = (start, bytes);
                            throw new IOException(ex.Message + " (and the rows already written could not be taken back: the next write will first try again)", ex);
                        }

                        throw;
                    }
                    foreach (var e in group) written.Add(e);
                }
                catch (Exception ex) when (IsFileFailure(ex))
                {
                    lastError = ex.Message;
                }
            }

            lock (queue)
            {
                inFlight = 0;   // before the removal: what stays of the batch is a plain waiting entry again
                if (written.Count > 0) writtenSinceDrop = true;   // what is dropped after this is a new gap
                pending.RemoveAll(written.Contains);
                return pending.Count == 0;
            }
        }
    }
}
