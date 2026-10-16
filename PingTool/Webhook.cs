using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace PingTool
{
    // One alert, as the webhooks receive it.
    internal sealed record WebhookEvent(string Host, HostChange Change, string Text, TimeSpan? Outage, DateTimeOffset Time);

    internal sealed record WebhookResult(string Label, bool Ok, string Detail);

    internal static class WebhookTest
    {
        // The text of the box "--test-webhooks" shows. A webhook that was left out of settings.json on loading (a mistyped address) is the
        // very fault this check is for: the load problem says how many, and when none is left it is said so, instead of "No webhook is set".
        public static string Summary(string? loadProblem, int ignored, int webhookCount, IReadOnlyList<WebhookResult> results, string settingsPath)
        {
            string left = loadProblem is null ? "" : loadProblem + "\r\n\r\n";
            if (webhookCount == 0)
                return left + (ignored > 0
                    ? "No webhook can be tested: none of the addresses in \"Webhooks\" in " + settingsPath + " is usable."
                    : "No webhook is set. Put the addresses in \"Webhooks\" in " + settingsPath + " (see the README).");

            return left + string.Join("\r\n", results.Select(r => (r.Ok ? "OK      " : "FAILED  ") + r.Label + " (" + r.Detail + ")"));
        }

        // The exit code of "--test-webhooks", for a script or a deployment that checks it: 0 only when there is at least one webhook,
        // none was left out of settings.json (a mistyped address is a fault of the configuration) and every one answered; 2 otherwise.
        public static int ExitCode(int ignored, int webhookCount, IReadOnlyList<WebhookResult> results) =>
            webhookCount > 0 && ignored == 0 && results.Count == webhookCount && results.All(r => r.Ok) ? HeadlessRunner.OkCode : HeadlessRunner.ErrorCode;
    }

    internal enum WebhookFormat { Json, Slack, Discord, Ntfy }

    // What each kind of service wants in the body. The format is read from the address (Slack, Discord and
    // ntfy have well-known ones); anything else gets a small JSON that also carries a "text" field, which
    // Teams (workflows), Mattermost and most other receivers accept as it is.
    internal static class WebhookPayload
    {
        public const int DiscordLimit = 2000;

        // At most limit characters, "..." included. The cut never falls between the two halves of a character outside the basic plane
        // (an emoji is a pair of UTF-16 units): a lone half would reach the receiver as an invalid character, or be refused.
        internal static string Shorten(string text, int limit)
        {
            if (text.Length <= limit) return text;
            int cut = Math.Max(0, limit - 3);
            if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
            return text[..cut] + "...";
        }

        // Only http and https addresses with a host: nothing else is ever contacted.
        public static bool TryParseUrl(string? text, out Uri url)
        {
            url = null!;
            if (!Uri.TryCreate((text ?? "").Trim(), UriKind.Absolute, out var parsed)) return false;
            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
            if (parsed.Host.Length == 0) return false;
            url = parsed;
            return true;
        }

        public static WebhookFormat Detect(Uri url)
        {
            string host = url.Host.ToLowerInvariant();
            if (host == "hooks.slack.com") return WebhookFormat.Slack;
            if ((host == "discord.com" || host.EndsWith(".discord.com", StringComparison.Ordinal) || host == "discordapp.com")
                && url.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal)) return WebhookFormat.Discord;
            if (host == "ntfy.sh" || host.StartsWith("ntfy.", StringComparison.Ordinal)) return WebhookFormat.Ntfy;
            return WebhookFormat.Json;
        }

        // Where a message went, WITHOUT the path or the query: a webhook address is a secret (whoever has it can post).
        public static string Label(Uri url) =>
            url.IsDefaultPort ? $"{url.Scheme}://{url.Host}" : $"{url.Scheme}://{url.Host}:{url.Port.ToString(CultureInfo.InvariantCulture)}";

        // The labels of a LIST of webhooks. Two of them on the same host and port (two channels of one chat service) would have the same
        // label, and a failure could not be told from the other's: those, and only those, carry their place in the list ("(webhook 2)").
        // The place is not a secret, and the path and the query that are stay out of it.
        public static string[] Labels(IReadOnlyList<Uri> urls)
        {
            var plain = urls.Select(Label).ToArray();
            return plain.Select((label, i) => plain.Count(other => string.Equals(other, label, StringComparison.Ordinal)) > 1
                ? $"{label} (webhook {(i + 1).ToString(CultureInfo.InvariantCulture)})" : label).ToArray();
        }

        public static string EventName(HostChange change) => change switch
        {
            HostChange.Down => "down",
            HostChange.Up => "up",
            HostChange.Degraded => "degraded",
            HostChange.Recovered => "recovered",
            HostChange.Notice => "notice",
            _ => "test",   // HostChange.None: the "--test-webhooks" message
        };

        public static HttpRequestMessage Build(Uri url, WebhookEvent e, string version)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            switch (Detect(url))
            {
                case WebhookFormat.Slack:
                    request.Content = Json(new { text = e.Text });
                    break;
                case WebhookFormat.Discord:
                    request.Content = Json(new { content = Shorten(e.Text, DiscordLimit) });
                    break;
                case WebhookFormat.Ntfy:
                    // ntfy takes the message as the body; its headers must be plain ASCII, so the host stays in the body.
                    request.Content = new StringContent(e.Text, new UTF8Encoding(false), "text/plain");
                    request.Headers.TryAddWithoutValidation("Title", "PingTool alert");
                    request.Headers.TryAddWithoutValidation("Priority", e.Change == HostChange.Down ? "4" : e.Change is HostChange.Degraded or HostChange.Notice ? "3" : "2");
                    request.Headers.TryAddWithoutValidation("Tags", e.Change switch
                    {
                        HostChange.Down => "red_circle",
                        HostChange.Degraded or HostChange.Notice => "warning",
                        _ => "green_circle",
                    });
                    break;
                default:
                    request.Content = Json(new
                    {
                        text = e.Text,
                        host = e.Host,
                        @event = EventName(e.Change),
                        outageSeconds = e.Outage is TimeSpan d ? (long?)Math.Max(0, (long)d.TotalSeconds) : null,
                        time = e.Time.ToString("o", CultureInfo.InvariantCulture),
                        source = "PingTool",
                        version,
                    });
                    break;
            }

            request.Headers.UserAgent.ParseAdd("PingTool/" + version);
            return request;
        }

        private static StringContent Json(object body) =>
            new(JsonSerializer.Serialize(body), new UTF8Encoding(false), "application/json");
    }

    // Sends alerts to the configured webhooks WITHOUT ever holding up the monitoring: Send only puts the alert
    // in a queue and returns; one background worker per webhook delivers it, with a short time limit, a few
    // retries when the receiver or the network fails for a moment, and a bounded queue (the oldest alerts go
    // first if a receiver stays down for hours).
    internal sealed class WebhookSender : IDisposable
    {
        public const int MaxQueued = 100;
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan[] DefaultRetryDelays = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4) };

        private sealed class Target
        {
            public required Uri Url { get; init; }
            public required string Label { get; init; }   // what a failure says (see WebhookPayload.Labels)
            public required Channel<WebhookEvent> Queue { get; init; }
        }

        private readonly List<Target> targets = new();
        private readonly HttpClient http;
        private readonly string version;
        private readonly TimeSpan timeout;
        private readonly TimeSpan[] retryDelays;
        private readonly CancellationTokenSource stop = new();
        private readonly List<Task> workers = new();

        // (label, reason) once a webhook has given up on an alert. Raised from a background thread.
        public event Action<string, string>? Failed;

        private readonly TimeSpan closeGrace;

        // The handler of the real sender. The program may run for days with this one client: its connections are renewed after a few
        // minutes (ConnectionLifetime), so that a receiver that moved to another address (a failover, a CDN) is reached again.
        internal static SocketsHttpHandler CreateHandler() => new() { AllowAutoRedirect = false, PooledConnectionLifetime = ProbeRunner.ConnectionLifetime };

        public WebhookSender(IEnumerable<Uri> urls, string version, HttpMessageHandler? handler = null,
                             TimeSpan[]? retryDelays = null, TimeSpan? timeout = null, TimeSpan? closeGrace = null)
        {
            this.version = version;
            this.closeGrace = closeGrace ?? DefaultCloseGrace;
            this.timeout = timeout ?? DefaultTimeout;
            this.retryDelays = retryDelays ?? DefaultRetryDelays;
            // No redirects: a POST that is redirected is turned into a GET by some servers, and the alert would be lost silently.
            http = new HttpClient(handler ?? CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };

            var all = urls.ToList();
            var labels = WebhookPayload.Labels(all);
            for (int n = 0; n < all.Count; n++)
            {
                var url = all[n];
                // A receiver that stays out of reach fills the queue and the oldest alerts are dropped: it is said (once per webhook and
                // run in the window, see MainForm), with the number so far, so that nobody reads a webhook history as complete.
                int dropped = 0, telling = 0;
                string label = labels[n];
                var target = new Target
                {
                    Url = url,
                    Label = label,
                    Queue = Channel.CreateBounded<WebhookEvent>(new BoundedChannelOptions(MaxQueued)
                    {
                        FullMode = BoundedChannelFullMode.DropOldest,
                        SingleReader = true,
                    }, evicted =>
                    {
                        // This runs on the thread that calls Send, the one that measures: it counts and nothing else. Telling the
                        // subscribers (a console, a window, a file) is done elsewhere, once for a burst of drops, with the count at that time.
                        Interlocked.Increment(ref dropped);
                        if (Interlocked.Exchange(ref telling, 1) == 0)
                            _ = Task.Run(() =>
                            {
                                Interlocked.Exchange(ref telling, 0);   // a drop after this one starts a new telling
                                RaiseFailed(label, $"{Volatile.Read(ref dropped)} alert(s) dropped: this webhook did not take them fast enough (queue of {MaxQueued} full), so what it received is incomplete");
                            });
                    }),
                };
                targets.Add(target);
                workers.Add(Task.Run(() => Work(target)));
            }
        }

        public int Count => targets.Count;

        // Never blocks, never throws.
        public void Send(WebhookEvent e)
        {
            foreach (var target in targets) target.Queue.Writer.TryWrite(e);
        }

        // One alert to every webhook, waiting for the answers (the "--test-webhooks" check). Same retries.
        public async Task<IReadOnlyList<WebhookResult>> SendNowAsync(WebhookEvent e, CancellationToken token = default)
        {
            var results = await Task.WhenAll(targets.Select(t => DeliverAsync(t.Label, t.Url, e, token)));
            return results;
        }

        private async Task Work(Target target)
        {
            try
            {
                await foreach (var e in target.Queue.Reader.ReadAllAsync(stop.Token))
                {
                    var result = await DeliverAsync(target.Label, target.Url, e, stop.Token);
                    // A delivery cut short because the sender is being closed did not fail: the user quit, nothing is wrong with the receiver.
                    if (!result.Ok && !stop.IsCancellationRequested) RaiseFailed(result.Label, result.Detail);
                }
            }
            catch (OperationCanceledException)
            {
                // Disposed.
            }
        }

        // Each subscriber on its own: one that throws (a console or a file that cannot be written) must not stop the worker, which
        // would leave every later alert of this webhook in the queue for ever, nor keep the other subscribers from hearing of it.
        private void RaiseFailed(string label, string detail)
        {
            foreach (var handler in Failed?.GetInvocationList() ?? Array.Empty<Delegate>())
            {
                try { ((Action<string, string>)handler)(label, detail); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"A subscriber to the webhook failure raised: {ex}"); }
            }
        }

        private async Task<WebhookResult> DeliverAsync(string label, Uri url, WebhookEvent e, CancellationToken token)
        {
            string last = "";
            var wait = TimeSpan.Zero;
            for (int attempt = 0; attempt <= retryDelays.Length; attempt++)
            {
                if (attempt > 0)
                {
                    // At least what the receiver asked for (Retry-After): a retry sent inside its restriction only uses up an attempt.
                    try { await Task.Delay(retryDelays[attempt - 1] > wait ? retryDelays[attempt - 1] : wait, token); }
                    catch (OperationCanceledException) { return new WebhookResult(label, false, "cancelled"); }
                }

                var (ok, retry, detail, retryAfter) = await TryOnce(url, e, token);
                if (ok) return new WebhookResult(label, true, detail);
                last = detail;
                wait = retryAfter ?? TimeSpan.Zero;
                if (!retry || token.IsCancellationRequested) break;
            }

            return new WebhookResult(label, false, last);
        }

        // The longest a Retry-After is obeyed: one webhook waits in its own worker, and a receiver that asks for an hour must not hold
        // up (and fill) the queue behind it nor the closing of PingTool. A longer one is NOT cut down to this (that would send the retry
        // inside the receiver's restriction and use up the attempts): the delivery is given up, and the failure says why.
        public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

        // Retry-After comes as a number of seconds or as a date; null when absent, unreadable or already past. As asked, not cut.
        internal static TimeSpan? RetryAfterOf(System.Net.Http.Headers.RetryConditionHeaderValue? header, DateTimeOffset now)
        {
            var wait = header?.Delta ?? (header?.Date is { } at ? at - now : (TimeSpan?)null);
            return wait is { } w && w > TimeSpan.Zero ? w : null;
        }

        // The failure text of an answer that asks for a longer wait than is obeyed.
        internal static string TooLongDetail(string detail, TimeSpan asked) =>
            detail + "; the receiver asked to wait " + Math.Ceiling(asked.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s, longer than the "
            + MaxRetryAfter.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s waited here, so it was not tried again";

        // ok, whether trying again could help, what to tell, and how long the receiver asked to wait.
        private async Task<(bool Ok, bool Retry, string Detail, TimeSpan? RetryAfter)> TryOnce(Uri url, WebhookEvent e, CancellationToken token)
        {
            try
            {
                using var request = WebhookPayload.Build(url, e, version);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(timeout);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
                int code = (int)response.StatusCode;
                if (code is >= 200 and < 300) return (true, false, "HTTP " + code.ToString(CultureInfo.InvariantCulture), null);

                // A 4xx means the address or the request is wrong: trying again will not fix it (except too-many-requests
                // and request-timeout); a 5xx is the receiver's trouble and may pass.
                bool transient = code >= 500 || response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout;
                string what = "HTTP " + code.ToString(CultureInfo.InvariantCulture);
                var asked = transient ? RetryAfterOf(response.Headers.RetryAfter, DateTimeOffset.UtcNow) : null;
                if (asked is { } w && w > MaxRetryAfter) return (false, false, TooLongDetail(what, w), null);   // a retry before that would only be refused again
                return (false, transient, what, asked);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return (false, true, "no answer within " + timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s", null);
            }
            catch (OperationCanceledException)
            {
                return (false, false, "cancelled", null);
            }
            catch (HttpRequestException ex)
            {
                // The message of a connection error never contains the address' path, but keep it short anyway.
                return (false, true, ex.InnerException?.Message ?? ex.Message, null);
            }
            catch (Exception ex)
            {
                // Whatever else goes wrong must end THIS delivery only: an exception escaping would silently end the
                // worker, and every later alert to this webhook with it.
                return (false, false, ex.GetType().Name + ": " + ex.Message, null);
            }
        }

        // How long closing waits for the alerts still queued to be sent (a "down" raised just before the user quits is the one that
        // matters), before what is left is cut short.
        public static readonly TimeSpan DefaultCloseGrace = TimeSpan.FromSeconds(2);

        private int disposed;

        public void Dispose()
        {
            // Once: a second call (a using and an explicit Dispose, two paths of cleanup) would cancel a source that is already disposed.
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;

            foreach (var target in targets) target.Queue.Writer.TryComplete();   // no new alert; the workers send what is queued, then end
            try { Task.WaitAll(workers.ToArray(), closeGrace); }
            catch (AggregateException) { /* a worker that ended badly has nothing more to say */ }

            stop.Cancel();   // only what the grace period did not finish
            try { Task.WaitAll(workers.ToArray(), TimeSpan.FromSeconds(1)); }
            catch (AggregateException) { /* workers end by cancellation */ }
            http.Dispose();
            stop.Dispose();
        }
    }
}
