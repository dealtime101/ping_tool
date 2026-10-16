using System.Diagnostics;
using System.Net.NetworkInformation;

namespace PingTool
{
    internal sealed record HeadlessResult(int ExitCode, string Summary, ReportData Report);

    // PingTool without a window ("--headless --duration 8h --report night.html"): for the Task Scheduler or a script. It probes the targets
    // with the same probes, monitor, incident detection and report as the window, for the given time, writes the report and says by its
    // exit code whether anything went wrong: 0 = no incident, 1 = at least one outage or slowdown, 2 = it could not run.
    internal static class HeadlessRunner
    {
        public const int OkCode = 0, IncidentCode = 1, ErrorCode = 2;
        public static readonly TimeSpan CheckpointEvery = TimeSpan.FromMinutes(10);

        // What the command line ends with when it did not start the program: the usage text asked for with --help was answered (0), anything
        // else is a command line that could not be understood (2).
        public static int ExitCodeForStartupMessage(string message) => message == StartupOptions.Usage ? OkCode : ErrorCode;

        // The three exit codes are what a script or a scheduled task reads: an error nobody foresaw (a settings file that cannot be opened,
        // a failing disk, a bug) must end as "could not run" (2) too, not as the code of a crashed runtime. It is written to the error
        // output and handed to `record` (the crash log) first. Cancellation of the whole process is not an error of the run and passes.
        public static int Guarded(Func<int> run, Action<Exception> record)
        {
            try
            {
                return run();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                try { record(ex); }
                catch (Exception) { /* the report of the error must not become another one */ }
                Console.Error.WriteLine("Unexpected error, the monitoring could not run: " + ex.GetType().Name + ": " + ex.Message);
                return ErrorCode;
            }
        }

        // What the end of the summary says of the report: where it is, or that it is not there. The writer of the command line catches its
        // own errors (and says them on the error output), so the run does not know that the last write failed: the caller does, and a path
        // given as "Report: ..." after a failure would send a script to a file that is not there (or is the one of an earlier checkpoint).
        public static string ReportNote(string? reportPath, bool lastWriteFailed) =>
            reportPath is null ? "" : lastWriteFailed ? " The report could not be written to " + reportPath + "." : " Report: " + reportPath;

        public delegate Task<ProbeOutcome> ProbeFunc(ProbeTarget target, int timeoutMs, CancellationToken token);

        // addresses: what to probe. Limits, interval, timeout and names come from `settings`. probe: the probe itself (injected: tests do not
        // touch the network). checkpoint: called with a fresh report every `checkpointEvery` and at the very end (so that a process killed
        // in the night leaves a report that is at most that old). alert: each state change, as the window would raise it.
        public static async Task<HeadlessResult> RunAsync(IReadOnlyList<string> addresses, Settings settings, TimeSpan duration, ProbeFunc probe,
            Action<ReportData>? checkpoint = null, TimeSpan? checkpointEvery = null, Action<WebhookEvent>? alert = null,
            Func<DateTimeOffset>? clock = null, CancellationToken stop = default)
        {
            clock ??= () => DateTimeOffset.Now;
            // The same ceiling as the command line (30 days). Past it, or below zero, the timer of the run would throw its own raw
            // "Parameter 'delay'" error: a clear message in the same family as the one for the targets instead. Zero is refused too: the run
            // would be cancelled before the first probe and end as "No probe was made", with nothing to say that the duration was why (the
            // command line asks for 5 seconds at least, see StartupOptions.MinDuration; this door only needs a duration that lets a probe be made).
            if (duration <= TimeSpan.Zero || duration > StartupOptions.MaxDuration)
                throw new ArgumentException($"The duration must be longer than zero and no longer than {(int)StartupOptions.MaxDuration.TotalDays} days.");

            var targets = new List<(string Address, ProbeTarget Target)>();
            foreach (string address in addresses.Distinct(TargetKey.Comparer))
            {
                if (!ProbeTarget.TryParse(address, out var t, out string problem))
                    throw new ArgumentException($"\"{address}\": {problem}");
                targets.Add((address, t));
            }

            if (targets.Count == 0) throw new ArgumentException("No target to probe: give addresses, --diagnose, or save some in the window first.");

            var sessions = new List<HostSession>();
            foreach (var (address, _) in targets)
            {
                settings.TargetOptions.TryGetValue(address, out var options);
                var (slow, loss, down) = TargetOptions.Effective(options, settings.DegradedLatencyMs, settings.DegradedLossPercent, settings.DownAfter);
                sessions.Add(new HostSession(address, slow, loss, down, settings.DegradedMos) { Options = options });
            }

            var maintenance = settings.GetMaintenance();
            var incidents = new IncidentLog { PlannedWhen = maintenance.IsActive };
            var gate = new object();   // the probes run side by side; what they feed is not made for that
            var start = clock();

            ReportData Build()
            {
                var now = clock();
                lock (gate)
                    return new ReportData(now, start, Environment.MachineName, AppVersion.Display, settings.IntervalMs, settings.TimeoutMs, settings.PacketSize,
                        settings.DegradedLatencyMs, settings.DegradedLossPercent,
                        sessions.Select(s => new HostReport(s.Address, s.IpText, s.Monitor.State, s.Stats.Sent, s.Stats.Lost, s.Stats.LossPercent,
                            s.Stats.Min, s.Stats.Avg, s.Stats.Max, s.Stats.Jitter, s.History.ToArray(), s.Stats.Hours,
                            s.Options?.Label, s.Options is null ? null : TargetOptions.DescribeLimits(s.Options), s.Notice, s.Mos, s.Options?.Service)).ToList(),
                        Diagnosis.For(sessions.Select(s => s.ToTarget()).ToList()), incidents.Summary(now), incidents.Incidents.ToList(), DownAfter: settings.DownAfter, DegradedMos: settings.DegradedMos);
            }

            using var run = CancellationTokenSource.CreateLinkedTokenSource(stop);
            run.CancelAfter(duration);
            int alertProblemSaid = 0;   // 1 once the error output has been told that an alert failed

            async Task Loop(HostSession session, ProbeTarget target)
            {
                while (!run.Token.IsCancellationRequested)
                {
                    var clockWatch = Stopwatch.StartNew();
                    long ping; PingFailure? failure; string? warning = null;
                    try
                    {
                        var outcome = await probe(target, settings.TimeoutMs, run.Token);
                        if (run.Token.IsCancellationRequested) break;   // a reply that lands after the end is not counted
                        ping = outcome.Rtt; failure = outcome.Failure; warning = outcome.Warning;
                    }
                    catch (OperationCanceledException) when (run.Token.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        // Whatever a probe throws is a failed ping of THIS host, not the end of the run.
                        ping = -1; failure = PingFailure.From(ex);
                    }

                    var now = clock();
                    WebhookEvent? toRaise = null;
                    lock (gate)
                    {
                        session.Add(ping, failure, now);
                        session.Notice = warning;
                        var change = session.Monitor.Update(ping);
                        incidents.Observe(now, session.Address, ping, session.LastFailure, change, session.Monitor.WindowLossPercent, session.Monitor.WindowAvgMs);
                        // In a maintenance window the incident is recorded (marked planned) but nobody is told.
                        if (change != HostChange.None && alert is not null && !maintenance.IsActive(now))
                        {
                            TimeSpan? outage = change == HostChange.Up
                                ? incidents.Incidents.LastOrDefault(i => i.Host == session.Address && i.Kind == IncidentKind.Outage)?.Duration(now) : null;
                            toRaise = new WebhookEvent(session.Address, change,
                                AlertMessage.For(session.DisplayName, change, session.Monitor.WindowLossPercent, session.Monitor.WindowAvgMs, outage, session.Monitor.Voice), outage, now);
                        }
                    }

                    // Outside the lock, and guarded: an alert that cannot be raised (the callback threw) must not end the probing of this
                    // host, nor reach Task.WhenAll and take the final report and the exit code with it. It is said once; the run goes on.
                    if (toRaise is not null)
                    {
                        try { alert!(toRaise); }
                        catch (Exception ex)
                        {
                            // An OperationCanceledException from the callback (its own timeout, a send that gave up) is a failed alert like any
                            // other: the run was not cancelled, and letting it out would end this host's loop and the final report with it. When
                            // the run IS over, it is just the end and there is nothing to say.
                            bool theEnd = ex is OperationCanceledException && run.Token.IsCancellationRequested;
                            if (!theEnd && Interlocked.Exchange(ref alertProblemSaid, 1) == 0)
                                Console.Error.WriteLine("An alert could not be raised, the monitoring goes on: " + ex.GetType().Name + ": " + ex.Message);
                        }
                    }

                    // Probes start one interval apart (see Cadence).
                    try { await Task.Delay(Cadence.WaitMs(settings.IntervalMs, clockWatch.ElapsedMilliseconds), run.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }

            // A checkpoint that fails (a file open in a browser, an antivirus holding it, anything the writer did not foresee) loses THAT
            // checkpoint: the next one is tried when its time comes, and the final one at the end. It is said once. Not guarded, it
            // ended the periodic saves for the rest of the night and then took the final report and the exit code down with it.
            int checkpointProblemSaid = 0;
            // False when the callback threw: for a checkpoint of the night the next one will try again; for the final one nobody will.
            bool SafeCheckpoint(ReportData data, bool final = false)
            {
                try { checkpoint!(data); return true; }
                catch (Exception ex)   // an OperationCanceledException of the callback's own too: it is a report that was not written
                {
                    // A checkpoint of the night is told once (the next one tries again); the final report is always told, whatever was said
                    // before about the checkpoints: nobody tries again, and it is the one a scheduled task is waiting for.
                    if (final) Console.Error.WriteLine("The final report could not be written: " + ex.GetType().Name + ": " + ex.Message);
                    else if (Interlocked.Exchange(ref checkpointProblemSaid, 1) == 0)
                        Console.Error.WriteLine("A report checkpoint could not be written, the next one will be tried: " + ex.GetType().Name + ": " + ex.Message);
                    return false;
                }
            }

            async Task Checkpoints()
            {
                if (checkpoint is null) return;
                try
                {
                    while (true)
                    {
                        await Task.Delay(checkpointEvery ?? CheckpointEvery, run.Token);
                        SafeCheckpoint(Build());
                    }
                }
                catch (OperationCanceledException) { /* the end */ }
            }

            var loops = targets.Select((t, i) => Loop(sessions[i], t.Target)).ToList();
            loops.Add(Checkpoints());
            await Task.WhenAll(loops);

            var report = Build();
            // The last report is the one that was asked for: when it cannot be written the run did not do its job, whatever it saw - a
            // scheduled task must not read "no incident" and take an old report, or none, for the night's.
            bool finalWritten = checkpoint is null || SafeCheckpoint(report, final: true);

            long sent = sessions.Sum(s => s.Stats.Sent);
            string summary = sent == 0
                ? "No probe was made."
                : $"{sent} probes on {sessions.Count} target(s) in {IncidentLog.FormatDuration(report.GeneratedAt - start)}. {report.IncidentSummary}"
                    + (finalWritten ? "" : " The final report could not be written.");
            int code = sent == 0 || !finalWritten ? ErrorCode : incidents.Incidents.Count > 0 ? IncidentCode : OkCode;
            return new HeadlessResult(code, summary, report);
        }
    }
}
