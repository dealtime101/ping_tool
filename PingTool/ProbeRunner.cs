using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PingTool
{
    // One probe's answer. Rtt < 0 = failure, and then Failure says why. Error keeps the raw
    // exception when there was one, for the debug trace.
    // Warning = something to say although the probe succeeded (a certificate about to expire).
    internal sealed record ProbeOutcome(long Rtt, PingFailure? Failure, IPAddress? Ip, Exception? Error = null, string? Warning = null);

    // How close a certificate is to its end, in words. null = nothing to say.
    internal static class CertWatch
    {
        public static string? Warning(string host, DateTime notAfterUtc, DateTime nowUtc, int warnDays)
        {
            if (warnDays <= 0) return null;

            double days = (notAfterUtc - nowUtc).TotalDays;
            if (days >= warnDays) return null;

            string when = notAfterUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (days < 0) return $"The certificate of {host} expired on {when}";
            int whole = (int)Math.Floor(days);
            return whole == 0 ? $"The certificate of {host} expires today or tomorrow ({when})"
                : $"The certificate of {host} expires in {whole.ToString(CultureInfo.InvariantCulture)} day{(whole == 1 ? "" : "s")} ({when})";
        }
    }

    // Runs ONE probe against a target. The caller loops. Cancelling the token (Stop, Close)
    // throws OperationCanceledException; every other failure comes back as a ProbeOutcome.
    internal static class ProbeRunner
    {
        // One client for the whole program (a client per probe leaks sockets). No redirects: a 301
        // from the server means it answered, which is all this probe asks.
        private static readonly HttpClient Web = CreateWebClient();

        private static HttpClient CreateWebClient()
        {
            // HttpClientHandler (not SocketsHttpHandler.SslOptions): only its callback is handed the request, which is where
            // the end date of the certificate is kept.
            var client = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = ValidateServerCertificate,
            }) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PingTool/" + AppVersion.Number);
            return client;
        }

        // The clients of #family=4 and #family=6: they connect themselves, to an address of that family only, and the name stays the one in the
        // URL for the Host header and the certificate. The end date of the certificate is not read on these (its callback is not handed the
        // request): the plain target of the same name keeps watching it.
        private static readonly HttpClient Web4 = CreateFamilyClient(4), Web6 = CreateFamilyClient(6);

        private static HttpClient CreateFamilyClient(int family)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = async (context, ct) =>
                {
                    string host = context.DnsEndPoint.Host;
                    IPAddress[] addresses;
                    try { addresses = await Dns.GetHostAddressesAsync(host, FamilyOf(family), ct); }
                    catch (SocketException)
                    {
                        // Told apart: the name exists but has nothing of this family (no record), or does not resolve at all (the error stays).
                        if ((await Dns.GetHostAddressesAsync(host, ct)).Length == 0) throw;
                        throw new NoRecordException(family);
                    }

                    if (addresses.Length == 0) throw new NoRecordException(family);
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch { socket.Dispose(); throw; }
                },
            };
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, errors) => errors == SslPolicyErrors.None;
            var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PingTool/" + AppVersion.Number);
            return client;
        }

        public static async Task<ProbeOutcome> RunAsync(ProbeTarget target, int timeoutMs, Ping ping, byte[]? buffer, CancellationToken token)
        {
            try
            {
                return target.Kind switch
                {
                    ProbeKind.Icmp => await IcmpAsync(target, timeoutMs, ping, buffer, token),
                    ProbeKind.Tcp => await TcpAsync(target, timeoutMs, token),
                    ProbeKind.Http => await HttpAsync(target, timeoutMs, token),
                    ProbeKind.Dns => await DnsAsync(target, timeoutMs, token),
                    _ => throw new ArgumentOutOfRangeException(nameof(target)),
                };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                // Our own deadline fired, not the user's Stop.
                return new ProbeOutcome(-1, PingFailure.Timeout, null, ex);
            }
            catch (Exception ex) when (FindNoRecord(ex) is { } none)
            {
                return NoRecord(none.Family);
            }
            catch (Exception ex)
            {
                return new ProbeOutcome(-1, PingFailure.From(ex), null, ex);
            }
        }

        private static NoRecordException? FindNoRecord(Exception? ex)
        {
            for (; ex is not null; ex = ex.InnerException) if (ex is NoRecordException none) return none;
            return null;
        }

        // The token goes to the call: Stop or Close cancels a ping in flight, like the other probes (the class promises it). A null buffer
        // is the system's default one.
        private static async Task<ProbeOutcome> IcmpAsync(ProbeTarget t, int timeoutMs, Ping ping, byte[]? buffer, CancellationToken token)
        {
            var reply = await ping.SendPingAsync(t.Host, TimeSpan.FromMilliseconds(timeoutMs), buffer, null, token);

            return reply.Status == IPStatus.Success
                ? new ProbeOutcome(reply.RoundtripTime, null, reply.Address)
                : new ProbeOutcome(-1, PingFailure.From(reply.Status, reply.Address?.AddressFamily), null);
        }

        internal static AddressFamily FamilyOf(int family) => family == 6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;

        internal const string NoRecordShort = "No record";

        internal static ProbeOutcome NoRecord(int family) =>
            new(-1, new PingFailure(NoRecordShort, $"The name has no {(family == 6 ? "AAAA (IPv6)" : "A (IPv4)")} record: nothing to reach on this family, which is not a network failure"), null);

        // Thrown by the connection of a #family= web target when the name has no address of that family.
        internal sealed class NoRecordException(int family) : Exception("no record")
        {
            public int Family { get; } = family;
        }

        // The time to open the connection, nothing more: that is "the port is reachable".
        private static Task<ProbeOutcome> TcpAsync(ProbeTarget t, int timeoutMs, CancellationToken token) => TcpAsync(t, timeoutMs, Dns.GetHostAddressesAsync, token);

        // The name is resolved FIRST and the clock starts after it: a slow resolver (or a first lookup that is not cached yet) is not a slow
        // port. The whole probe, resolution included, still has to fit in the timeout. `resolve` is injected so that a test can make it slow.
        internal static async Task<ProbeOutcome> TcpAsync(ProbeTarget t, int timeoutMs,
            Func<string, CancellationToken, Task<IPAddress[]>> resolve, CancellationToken token)   // the token last (CA1068)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeoutMs);
            using var client = new TcpClient();

            var addresses = await resolve(t.Host, deadline.Token);
            if (addresses.Length == 0) return new ProbeOutcome(-1, new PingFailure("No host", "The name resolved to no address"), null);

            // #family=: only that family is tried; a name with none of it has no record of that kind, which is not a network failure.
            if (t.Family != 0)
            {
                addresses = addresses.Where(a => a.AddressFamily == FamilyOf(t.Family)).ToArray();
                if (addresses.Length == 0) return NoRecord(t.Family);
            }

            // The addresses are tried in the order the system gave them, as it does itself: what a slow first one costs before the
            // second one answers is part of what a connection to this name costs.
            var clock = Stopwatch.StartNew();
            await client.ConnectAsync(addresses, t.Port, deadline.Token);
            long rtt = clock.ElapsedMilliseconds;

            // A dual-stack socket reports an IPv4 peer as ::ffff:a.b.c.d.
            var peer = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            return new ProbeOutcome(rtt, null, peer is { IsIPv4MappedToIPv6: true } ? peer.MapToIPv4() : peer);
        }

        // Where the certificate's end date of a request is kept while the request is in flight (see ValidateServerCertificate).
        private static readonly HttpRequestOptionsKey<DateTime> CertificateEnd = new("PingTool.CertificateEnd");

        // "404 Not Found", and just "599" when the server gave no phrase (HTTP/2 has none on the wire, and a status the framework does not
        // know has none to give): never "599 " with a space and nothing after it.
        internal static string StatusText(int code, string? reasonPhrase)
        {
            string number = code.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(reasonPhrase) ? number : number + " " + reasonPhrase.Trim();
        }

        // How much of a page is read when an expected text is looked for (see Search): the first 64 KiB.
        public const int MaxBodyBytes = 64 * 1024;

        // The default validation, plus a note of when the server's certificate ends. Returning "no policy error" is exactly what the
        // default check does, so nothing is trusted that was not before.
        public static bool ValidateServerCertificate(HttpRequestMessage request, X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors)
        {
            if (certificate is not null) request.Options.Set(CertificateEnd, certificate.NotAfter.ToUniversalTime());
            return errors == SslPolicyErrors.None;
        }

        // The time until the status line and headers arrive, on a NEW connection each time (so the
        // figure includes the TCP and TLS handshakes, like a first visit). 4xx and 5xx are failures.
        private static Task<ProbeOutcome> HttpAsync(ProbeTarget t, int timeoutMs, CancellationToken token) => HttpAsync(t, timeoutMs, t.Family == 4 ? Web4 : t.Family == 6 ? Web6 : Web, token);

        internal static async Task<ProbeOutcome> HttpAsync(ProbeTarget t, int timeoutMs, HttpClient client, CancellationToken token)   // the token last (CA1068)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeoutMs);
            using var request = new HttpRequestMessage(HttpMethod.Get, t.Url);
            request.Headers.ConnectionClose = true;

            var clock = Stopwatch.StartNew();
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            try
            {
                // #final=: the redirects are followed by hand (5 at most), and the page they end on is the one that is judged.
                Uri? reached = t.Url;
                if (t.FinalUrl is not null)
                {
                    for (int hops = 0; (int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next; hops++)
                    {
                        if (hops == MaxRedirects)
                            return new ProbeOutcome(-1, new PingFailure("Final URL", $"More than {MaxRedirects} redirects in a row"), null);
                        reached = new Uri(reached!, next);
                        using var hop = new HttpRequestMessage(HttpMethod.Get, reached);
                        hop.Headers.ConnectionClose = true;
                        var following = await client.SendAsync(hop, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                        response.Dispose();
                        response = following;
                    }
                }

                return await Judge(t, response, reached, clock.ElapsedMilliseconds, request, deadline.Token);
            }
            finally { response.Dispose(); }
        }

        private const int MaxRedirects = 5;

        // The answer against what the target expects: transport problems never get here (they throw), so each failure below is
        // the page being wrong, with its own name (HTTP, Status, Final URL, Redirect, Content, JSON).
        private static async Task<ProbeOutcome> Judge(ProbeTarget t, HttpResponseMessage response, Uri? reached, long rtt, HttpRequestMessage request, CancellationToken token)
        {
            int code = (int)response.StatusCode;
            if (t.ExpectStatus is { } wanted)
            {
                if (code != wanted)
                    return new ProbeOutcome(-1, new PingFailure("Status", $"The server answered {StatusText(code, response.ReasonPhrase)} instead of the expected {wanted}"), null);
            }
            else if (code >= 400)
                return new ProbeOutcome(-1, new PingFailure("HTTP " + code, "The server answered " + StatusText(code, response.ReasonPhrase)), null);

            if (t.FinalUrl is { } final && reached is not null
                && !(reached.Host + reached.PathAndQuery).StartsWith(final, StringComparison.OrdinalIgnoreCase))
                return new ProbeOutcome(-1, new PingFailure("Final URL", $"The page reached is {reached.Host}{reached.PathAndQuery}, not {final}: a login page or a captive portal does this"), null);

            byte[]? body = null;
            int bodyLength = 0;
            bool bodyCut = false;
            if (t.Json is not null)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                (body, bodyLength, bodyCut) = await ReadAll(stream, token);
            }

            // The page must contain the expected text: a redirect (to a login page, as a captive portal does) or a maintenance
            // page that answers "200" is then a failure, not a success.
            if (t.ExpectText is { } expected)
            {
                if (code is >= 300 and < 400)
                    return new ProbeOutcome(-1, new PingFailure("Redirect", $"The server redirected (HTTP {code}) instead of serving the page with the expected text: a login page of a captive portal does this"), null);

                var (found, truncated) = body is null ? await ReadStart(response, expected, token)
                    : (Decode(body, bodyLength, response.Content.Headers.ContentType?.CharSet).Contains(expected, StringComparison.OrdinalIgnoreCase), bodyCut);
                if (!found)
                    return new ProbeOutcome(-1, new PingFailure("Content", truncated
                        ? $"The text was not found in the first {MaxBodyBytes / 1024} KiB of the page, which is all that is searched; the page is longer, so the text may be further down"
                        : "The page answered but does not contain the expected text (a maintenance page, an error page or a captive portal)"), null);
            }

            if (t.Json is { } wantedJson && body is not null && JsonProblem(body, bodyLength, bodyCut, wantedJson) is { } problem)
                return new ProbeOutcome(-1, new PingFailure("JSON", problem), null);

            string? warning = request.Options.TryGetValue(CertificateEnd, out DateTime end)
                ? CertWatch.Warning(t.Host, end, DateTime.UtcNow, t.CertWarnDays) : null;
            return new ProbeOutcome(rtt, null, null, Warning: warning);
        }

        private static async Task<(byte[] Buffer, int Count, bool Truncated)> ReadAll(Stream stream, CancellationToken token)
        {
            var buffer = new byte[MaxBodyBytes];
            int total = 0, read;
            while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), token)) > 0) total += read;
            bool more = total == buffer.Length && await stream.ReadAsync(new byte[1], token) > 0;
            return (buffer, total, more);
        }

        // Null when the body holds the expected value at the path; otherwise what is wrong, never the body itself (it is not kept).
        internal static string? JsonProblem(byte[] body, int count, bool truncated, JsonExpect expected)
        {
            if (truncated) return $"The body is longer than {MaxBodyBytes / 1024} KiB, which is all that is read, so it cannot be checked as JSON";
            System.Text.Json.JsonElement node;
            try { node = System.Text.Json.JsonDocument.Parse(body.AsMemory(0, count)).RootElement; }
            catch (System.Text.Json.JsonException) { return "The body is not valid JSON (an HTML page, a login page or a captive portal?)"; }

            foreach (string step in expected.Path.Split('.'))
            {
                bool found = false;
                if (node.ValueKind == System.Text.Json.JsonValueKind.Object) found = node.TryGetProperty(step, out node);
                else if (node.ValueKind == System.Text.Json.JsonValueKind.Array && int.TryParse(step, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < node.GetArrayLength())
                {
                    node = node[index];
                    found = true;
                }

                if (!found) return $"The JSON has nothing at \"{expected.Path}\"";
            }

            if (node.ValueKind == System.Text.Json.JsonValueKind.Null) return $"The JSON value at \"{expected.Path}\" is null";
            if (expected.Value is null) return null;
            string actual = node.ValueKind == System.Text.Json.JsonValueKind.String ? node.GetString() ?? "" : node.GetRawText();
            return string.Equals(actual, expected.Value, StringComparison.OrdinalIgnoreCase) ? null
                : $"The JSON value at \"{expected.Path}\" is not the expected one";
        }

        private static async Task<(bool Found, bool Truncated)> ReadStart(HttpResponseMessage response, string expected, CancellationToken token)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            return await Search(stream, response.Content.Headers.ContentType?.CharSet, expected, token);
        }

        // Looks for `expected` in the first MaxBodyBytes of the stream, as the page arrives: it stops at the first read that brings the text
        // (a page that has already said what we wait for and then sends the rest slowly must not turn into a timeout). What was read is
        // decoded again whole after each read, so a text cut in two by two reads is found, at the price of a few decodings of 64 KiB.
        // Truncated: the page goes on beyond the limit, and the text was not in what was read - "not found" then may simply mean
        // "further down", which the message must not call a maintenance page.
        internal static async Task<(bool Found, bool Truncated)> Search(Stream stream, string? charset, string expected, CancellationToken token)
        {
            var buffer = new byte[MaxBodyBytes];
            int total = 0, read;
            while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), token)) > 0)
            {
                total += read;
                if (Decode(buffer, total, charset).Contains(expected, StringComparison.OrdinalIgnoreCase)) return (true, false);
            }

            bool more = total == buffer.Length && await stream.ReadAsync(new byte[1], token) > 0;
            return (false, more);
        }

        // The text of a page in the encoding it says it has: a byte order mark first (it is certain), then the charset of the
        // Content-Type, and UTF-8 when there is neither or the name is not one this system knows. A page in ISO-8859-1 or UTF-16 read
        // as UTF-8 would not contain an expected text with an accent, and a good answer would count as a failure.
        internal static string Decode(byte[] buffer, int count, string? charset)
        {
            var bom = buffer.AsSpan(0, count);
            if (bom.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return Encoding.UTF8.GetString(buffer, 3, count - 3);
            if (bom.StartsWith(new byte[] { 0xFF, 0xFE })) return Encoding.Unicode.GetString(buffer, 2, count - 2);
            if (bom.StartsWith(new byte[] { 0xFE, 0xFF })) return Encoding.BigEndianUnicode.GetString(buffer, 2, count - 2);

            Encoding encoding = Encoding.UTF8;
            if (!string.IsNullOrWhiteSpace(charset))
            {
                try
                {
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);   // windows-1252 and the other code pages (idempotent)
                    encoding = Encoding.GetEncoding(charset.Trim().Trim('"', '\''));
                }
                catch (ArgumentException) { }   // a name we do not know: UTF-8, as before
            }

            return encoding.GetString(buffer, 0, count);
        }

        // Through the operating system's resolver, so its cache applies: a name looked up a moment
        // ago answers in 0 ms. It still catches a resolver that stopped answering.
        private static async Task<ProbeOutcome> DnsAsync(ProbeTarget t, int timeoutMs, CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeoutMs);

            if (t.DnsOptions is { } options) return await DnsCompareAsync(t, options, deadline.Token, token);

            var clock = Stopwatch.StartNew();
            var addresses = await Dns.GetHostAddressesAsync(t.Host, deadline.Token);
            long rtt = clock.ElapsedMilliseconds;

            return addresses.Length == 0
                ? new ProbeOutcome(-1, new PingFailure("No host", "The name resolved to no address"), null)
                : new ProbeOutcome(rtt, null, addresses[0]);
        }

        // The last answer of each dns:// target with #change, to say when it is not the same.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> LastAnswers = new();

        // dns:// with options: every resolver is asked at once. A resolver that fails is a failure naming it (an error); resolvers that
        // answer differently are a notice, or a failure with #same (a CDN answers differently from place to place, which is no fault);
        // #change adds a notice when the answer is not the one of the previous probe. The time is the slowest resolver's. The messages
        // give the records, which are public data, trimmed to a few.
        private static async Task<ProbeOutcome> DnsCompareAsync(ProbeTarget t, DnsExpect options, CancellationToken token, CancellationToken user)
        {
            var servers = options.Servers.Count > 0 ? options.Servers : SystemDnsServers();
            if (servers.Count == 0) return new ProbeOutcome(-1, new PingFailure("No resolver", "No DNS server is configured on the active network card; add #server=ip"), null);

            var clock = Stopwatch.StartNew();
            async Task<(IPAddress Server, DnsClient.Reply? Reply, string? Error, long Ms)> Ask(IPAddress server)
            {
                try
                {
                    var reply = await DnsClient.QueryAsync(t.Host, options.Type, server, token);
                    string? error = reply.Rcode == 0 ? (reply.Records.Count == 0 ? $"no {options.Type} record" : null) : reply.Rcode == 3 ? "the name does not exist" : $"DNS error {reply.Rcode}";
                    return (server, reply, error, clock.ElapsedMilliseconds);
                }
                catch (OperationCanceledException) { return (server, null, "no answer in time", clock.ElapsedMilliseconds); }
                catch (SocketException ex) { return (server, null, ex.SocketErrorCode.ToString(), clock.ElapsedMilliseconds); }
            }

            var asked = await Task.WhenAll(servers.Select(Ask));
            user.ThrowIfCancellationRequested();

            var failed = asked.Where(a => a.Error is not null).ToList();
            if (failed.Count > 0)
                return new ProbeOutcome(-1, new PingFailure("Resolver", string.Join("; ", failed.Select(a => $"{a.Server}: {a.Error}")) +
                    (failed.Count < asked.Length ? $" ({asked.Length - failed.Count} of {asked.Length} resolvers answered)" : "")), null);

            static string Set(IEnumerable<string> records) { var all = records.Order(StringComparer.Ordinal).ToList(); return string.Join(", ", all.Take(4)) + (all.Count > 4 ? $" (+{all.Count - 4})" : ""); }
            var sets = asked.Select(a => (a.Server, Text: Set(a.Reply!.Records))).ToList();
            IPAddress? first = asked[0].Reply!.Records.Select(r => IPAddress.TryParse(r, out var ip) ? ip : null).FirstOrDefault(ip => ip is not null);
            long slowest = asked.Max(a => a.Ms);

            string? warning = null;
            if (sets.Select(s => s.Text).Distinct().Count() > 1)
            {
                string detail = "the resolvers answer differently: " + string.Join("; ", sets.Select(s => $"{s.Server} gives {s.Text}"));
                if (options.MustMatch) return new ProbeOutcome(-1, new PingFailure("Different", $"{t.Host} {options.Type}: {detail}"), first);
                warning = $"{t.Host} {options.Type}: {detail}";
            }

            if (options.WatchChange)
            {
                string key = t.Host + "|" + options.Type + "|" + string.Join(",", servers), now = string.Join(" | ", sets.Select(s => s.Text).Distinct());
                if (LastAnswers.TryGetValue(key, out string? before) && before != now)
                    warning = (warning is null ? "" : warning + ". ") + $"{t.Host} {options.Type}: the answer changed, from {before} to {now}";
                LastAnswers[key] = now;
            }

            return new ProbeOutcome(slowest, null, first, Warning: warning);
        }

        // The DNS servers of the first network card that is up and has any.
        private static List<IPAddress> SystemDnsServers() =>
            System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .Select(n => n.GetIPProperties().DnsAddresses.Where(a => !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal).Take(DnsExpect.MaxServers).ToList())
                .FirstOrDefault(l => l.Count > 0) ?? new List<IPAddress>();
    }
}
