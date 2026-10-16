using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace PingTool
{
    internal enum ProbeKind { Icmp, Tcp, Http, Dns }

    // A value looked for in a JSON body: the dotted path (object names and array indexes) and the text it must have; null = present and not null.
    internal sealed record JsonExpect(string Path, string? Value);

    // What to probe, parsed from what the user typed in the address box:
    //   google.ca                 ping (ICMP), as before
    //   tcp://example.com:443     can a TCP connection be opened to that port?
    //   http://host/path          does the web server answer? (https:// too)
    //   dns://example.com         does the name resolve?
    //
    // A web address may carry options after a #, which is never sent to the server:
    //   https://example.com/#contains=Welcome        the page must contain that text (a captive portal or a maintenance page
    //                                                answers "200" or redirects, but does not contain it)
    //   https://example.com/#cert=30                 warn when the certificate expires in less than 30 days (default 14, 0 = never)
    //   https://example.com/#contains=a%20b&cert=7   both (spaces and special characters written %20 etc.)
    //   https://example.com/#status=200              the status must be exactly that (a 3xx or 4xx is then a success when it is the one expected)
    //   https://example.com/#final=example.com/home  the redirects are followed (5 at most) and the page reached must be at that host and path
    //   dns://example.com#server=1.1.1.1,8.8.8.8     ask those resolvers (up to 4) at once and compare what they answer; also #type=AAAA|MX|NS|TXT|CNAME,
    //                                                #same (a difference is a failure) and #change (a notice when the answer is not the previous one)
    //   https://example.com/api#json=status=ok      the body must be JSON holding that value at that dotted path (a.b.0.c); no =value means "present, not null"
    internal sealed record ProbeTarget(ProbeKind Kind, string Host, int Port, Uri? Url, string? ExpectText = null, int CertWarnDays = ProbeTarget.DefaultCertWarnDays,
        int? ExpectStatus = null, string? FinalUrl = null, JsonExpect? Json = null, DnsExpect? DnsOptions = null, int Family = 0)
    {
        public const int DefaultCertWarnDays = 14;
        public const int MaxCertWarnDays = 365;
        public const int MaxExpectLength = 200;

        public const string Help =
            "No prefix = ping.  tcp://host:port = open a TCP connection.  http://url or https://url = web request.  dns://name = name lookup.  " +
            "Web options after #, joined by &: #contains=text (the page must hold it; spaces as %20) and #cert=days (https only: warn that many days before the certificate ends, 0 = never), " +
            "for example https://example.com/#contains=Welcome&cert=30. Also #status=200 (the exact status), #final=host/path (where the redirects must end) and #json=path=value (a value in a JSON body). dns://name takes #server=ip,ip (resolvers to compare), #type=AAAA|MX|NS|TXT|CNAME, #same and #change.";

        // The addresses a file or the command line gave, minus those the address box would refuse (a hand-edited
        // settings.json, a mistyped argument): the same rule as typing them, and the count says how many were left out.
        public static List<string> KeepValid(IEnumerable<string> addresses, out int skipped)
        {
            var kept = new List<string>();
            skipped = 0;
            foreach (var address in addresses)
            {
                if (TryParse(address, out _, out _)) kept.Add(address);
                else skipped++;
            }

            return kept;
        }

        public static bool TryParse(string? input, [NotNullWhen(true)] out ProbeTarget? target, out string error)
        {
            target = null;
            error = "";
            string text = (input ?? "").Trim();
            if (text.Length == 0) { error = "Enter an address first, or add a host to the list."; return false; }

            int sep = text.IndexOf("://", StringComparison.Ordinal);
            if (sep < 0)
            {
                // A ping takes a host name or an IP address, nothing else. "example.com:443" or "www.example.com/page" would only
                // come back as an unresolved name, which does not tell the user that a prefix was missing. A colon is fine in an IPv6 address.
                if (text.Any(char.IsWhiteSpace) || text.Contains('/', StringComparison.Ordinal)
                    || (text.Contains(':', StringComparison.Ordinal) && !IPAddress.TryParse(text, out _)))
                {
                    error = "Without a prefix the address is pinged, so it must be just a host name or an IP address. " +
                        "For a port use tcp://example.com:443, for a web page https://example.com/page";
                    return false;
                }

                // Just a name, as for tcp:// and dns://: what is not one (a ?query, a user@, a #fragment, a leading dash, an empty label)
                // would only come back from the ping as a name that does not resolve, without saying it was never a name.
                if (!IPAddress.TryParse(text, out _) && Uri.CheckHostName(text) == UriHostNameType.Unknown)
                {
                    error = "That is not a valid host name or IP address. Without a prefix the address is pinged, so it must be just a name such as example.com or an address such as 8.8.8.8 (no ?, @ or # parts).";
                    return false;
                }

                target = new ProbeTarget(ProbeKind.Icmp, text, 0, null);
                return true;
            }

            string scheme = text[..sep].ToLowerInvariant();
            string rest = text[(sep + 3)..];

            switch (scheme)
            {
                case "tcp":
                    // A trailing slash is what a pasted address often ends with; dns:// already accepts it.
                    // Option after #: tcp://example.com:443#family=6 (see TryFamily).
                    int tcpHash = rest.IndexOf('#', StringComparison.Ordinal);
                    int tcpFamily = 0;
                    if (tcpHash >= 0)
                    {
                        string fragment = rest[(tcpHash + 1)..];
                        rest = rest[..tcpHash];
                        if (!fragment.StartsWith("family=", StringComparison.OrdinalIgnoreCase) || !TryFamily(fragment[7..], out tcpFamily, out error))
                        {
                            if (error.Length == 0) error = "The only option after # of a tcp address is #family=4 or #family=6, for example tcp://example.com:443#family=6";
                            return false;
                        }
                    }

                    if (!TryHostPort(rest.TrimEnd('/'), out string host, out int port))
                    {
                        error = "tcp:// needs a host and a port from 1 to 65535, for example tcp://example.com:443"
                            + (rest.StartsWith('[') ? " (brackets are only for an IPv6 address, for example tcp://[2001:db8::1]:443)" : "");
                        return false;
                    }

                    if (tcpFamily != 0 && IPAddress.TryParse(host, out _))
                    {
                        error = "#family= needs a host name, not an IP address: an address has one family already.";
                        return false;
                    }

                    target = new ProbeTarget(ProbeKind.Tcp, host, port, null, Family: tcpFamily);
                    return true;

                case "http":
                case "https":
                    if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Host.Length == 0)
                    {
                        error = scheme + "://: that is not a valid web address, for example https://example.com/";
                        return false;
                    }

                    // As for tcp://: a port is from 1 to 65535. Uri accepts :0 (and the address would only fail at the connection, with an
                    // error that does not say the port was never one); above 65535 it refuses the address itself, above.
                    if (url.Port is < 1 or > 65535)
                    {
                        error = scheme + "://: the port must be from 1 to 65535, for example https://example.com:8443/";
                        return false;
                    }

                    if (!TryHttpOptions(url.Fragment, url.Scheme == Uri.UriSchemeHttps, out var o, out error)) return false;

                    // The host as tcp:// gives it: an IPv6 literal without its URI brackets ("[2001:db8::1]" is how a URI writes it, the
                    // resolver and the certificate check want the bare address).
                    if (o.Family != 0 && url.HostNameType != UriHostNameType.Dns)
                    {
                        error = "#family= needs a host name, not an IP address: an address has one family already.";
                        return false;
                    }

                    target = new ProbeTarget(ProbeKind.Http, url.HostNameType == UriHostNameType.IPv6 ? url.Host.Trim('[', ']') : url.Host, url.Port, url,
                        o.Expect, o.CertDays, o.Status, o.Final, o.Json, Family: o.Family);
                    return true;

                case "dns":
                    // Options after #: dns://example.com#server=1.1.1.1,8.8.8.8&type=AAAA&same&change
                    string name = rest;
                    DnsExpect? dnsOptions = null;
                    int hash = rest.IndexOf('#', StringComparison.Ordinal);
                    if (hash >= 0)
                    {
                        name = rest[..hash];
                        if (!TryDnsOptions(rest[(hash + 1)..], out dnsOptions, out error)) return false;
                    }

                    name = name.TrimEnd('/');
                    // An IP address is not looked up: the system hands it back without asking any server, so the target would answer for ever
                    // whatever the DNS does (this takes the short forms too: 127.1, 2130706433). It is told BEFORE the test of a name below: an
                    // IPv6 address has colons, and would be refused there as "not a name" without saying that it is an address.
                    if (System.Net.IPAddress.TryParse(name, out _) || Uri.CheckHostName(name) is UriHostNameType.IPv4 or UriHostNameType.IPv6)
                    {
                        error = "dns:// needs a name, not an IP address: an address is not looked up, so nothing would be tested. For example dns://example.com (to test an address, write it without a prefix to ping it).";
                        return false;
                    }

                    // Just a name: the rest of what a URL can carry (?query, #fragment, user@, spaces) is not part of it, and would be sent to
                    // the resolver as if it were. CheckHostName also refuses a leading dash or an empty label; internationalised names pass.
                    if (name.Length == 0 || name.Contains(':', StringComparison.Ordinal) || name.Contains('/', StringComparison.Ordinal)
                        || Uri.CheckHostName(name) == UriHostNameType.Unknown)
                    {
                        error = "dns:// needs just a name, for example dns://example.com";
                        return false;
                    }

                    target = new ProbeTarget(ProbeKind.Dns, name, 0, null, DnsOptions: dnsOptions);
                    return true;

                default:
                    error = string.Format(CultureInfo.CurrentCulture,
                        "Unknown prefix \"{0}://\". Use tcp://, http://, https:// or dns://, or no prefix to ping.", scheme);
                    return false;
            }
        }

        // The part after # of a web address: "contains=text" and/or "cert=days", separated by &. Anything else is refused: a typo
        // ("#contain=...") silently ignored would leave the check the user thinks they set up doing nothing.
        private sealed record HttpOptions(string? Expect, int CertDays, int? Status, string? Final, JsonExpect? Json, int Family = 0);

        // #family=4 or 6: the probe uses only that IP family of the name, so that two entries of one name (one #family=4, one #family=6)
        // are the two sides of a dual-stack service. A name with no record of that family is "No record", not a network failure.
        private static bool TryFamily(string value, out int family, out string error)
        {
            family = value == "4" ? 4 : value == "6" ? 6 : 0;
            error = family == 0 ? "#family= needs 4 or 6, for example https://example.com/#family=6" : "";
            return family != 0;
        }

        private static bool TryHttpOptions(string fragment, bool secure, out HttpOptions options, out string error)
        {
            string? expect = null, final = null;
            int certDays = DefaultCertWarnDays, family = 0;
            int? status = null;
            JsonExpect? json = null;
            options = new HttpOptions(null, certDays, null, null, null);
            error = "";
            string text = fragment.TrimStart('#');
            if (text.Length == 0) return true;

            var seen = new HashSet<string>();
            foreach (string part in text.Split('&'))
            {
                // An empty segment (a & at the end, two in a row) sets nothing: it is not an option, so it is not an unknown one, and nothing is
                // left silently unchecked by skipping it (a typo is still refused; "#" alone was already accepted).
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=', StringComparison.Ordinal);
                string key = (eq < 0 ? part : part[..eq]).ToLowerInvariant();
                string value = eq < 0 ? "" : part[(eq + 1)..];

                if (key is "status" or "final" or "json" or "family" && seen.Add(key))
                {
                    string decoded = Uri.UnescapeDataString(value);
                    if (decoded.Length == 0 || decoded.Length > MaxExpectLength || decoded.Any(char.IsControl))
                    {
                        error = $"#{key}= needs a value of 1 to {MaxExpectLength} characters (write spaces as %20), for example https://example.com/#status=200";
                        return false;
                    }

                    if (key == "family")
                    {
                        if (!TryFamily(decoded, out family, out error)) return false;
                    }
                    else if (key == "status")
                    {
                        if (!int.TryParse(decoded, NumberStyles.None, CultureInfo.InvariantCulture, out int code) || code is < 100 or > 599)
                        {
                            error = "#status= needs a status code from 100 to 599, for example https://example.com/#status=200";
                            return false;
                        }

                        status = code;
                    }
                    else if (key == "final")
                    {
                        // host/path, the scheme ignored: what the browser would show once the redirects are over.
                        final = decoded.Contains("://", StringComparison.Ordinal) ? decoded[(decoded.IndexOf("://", StringComparison.Ordinal) + 3)..] : decoded;
                    }
                    else
                    {
                        int at = decoded.IndexOf('=', StringComparison.Ordinal);
                        string path = at < 0 ? decoded : decoded[..at];
                        if (path.Length == 0 || path.Split('.').Any(s => s.Length == 0))
                        {
                            error = "#json= needs a dotted path, optionally followed by =value, for example https://example.com/health#json=status=ok";
                            return false;
                        }

                        json = new JsonExpect(path, at < 0 ? null : decoded[(at + 1)..]);
                    }

                    continue;
                }

                if (key == "contains" && seen.Add(key))
                {
                    // UnescapeDataString never throws for a bad sequence: "%zz", a lone "%" ("100%") and bytes that are not UTF-8 stay as
                    // typed, which is what someone looking for a text with a percent sign in it means. A decoded control character (%00,
                    // %0A) is refused below.
                    string decoded = Uri.UnescapeDataString(value);
                    if (decoded.Length == 0 || decoded.Length > MaxExpectLength || decoded.Any(char.IsControl))
                    {
                        error = $"#contains= needs a text of 1 to {MaxExpectLength} characters (write spaces as %20), for example https://example.com/#contains=Welcome";
                        return false;
                    }

                    expect = decoded;
                }
                else if (key == "cert" && seen.Add(key))
                {
                    // An option that can never do anything is refused, like the unknown ones: a person who wrote it believes a certificate is watched.
                    if (!secure)
                    {
                        error = "#cert= only applies to https:// addresses: an http:// address has no certificate to check. Use https://, or leave #cert= out.";
                        return false;
                    }

                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out certDays) || certDays > MaxCertWarnDays)
                    {
                        error = $"#cert= needs a number of days from 0 (never warn) to {MaxCertWarnDays}, for example https://example.com/#cert=30";
                        return false;
                    }
                }
                else if (key is "contains" or "cert" or "status" or "final" or "json" or "family")
                {
                    // A known option, written a second time: not a typo to look for, the same one twice (and which one is kept is not a guess to make).
                    error = $"#{key}= is given twice in the web address: use each option once, separated by & (#contains=text&cert=days).";
                    return false;
                }
                else
                {
                    error = "Unknown option after # in the web address: use #contains=text, #cert=days, #status=code, #final=host/path, #json=path=value or #family=4|6 (separated by &), each once.";
                    return false;
                }
            }

            options = new HttpOptions(expect, certDays, status, final, json, family);
            return true;
        }

        // #server=ip[,ip...] (up to 4 resolvers, IP addresses: a name would need a lookup itself), #type=A|AAAA|CNAME|MX|NS|TXT,
        // #same (the resolvers must give the same answer: a difference is a failure, not just a notice) and #change (a notice when the
        // answer is not the one of the previous probe). Each once; without #server the DNS servers of the active network card are asked.
        private static bool TryDnsOptions(string text, out DnsExpect? options, out string error)
        {
            options = null;
            error = "";
            var servers = new List<IPAddress>();
            string type = "A";
            bool same = false, change = false;
            var seen = new HashSet<string>();

            foreach (string part in text.Split('&'))
            {
                if (part.Length == 0) continue;   // an empty segment sets nothing: see TryHttpOptions
                int eq = part.IndexOf('=', StringComparison.Ordinal);
                string key = (eq < 0 ? part : part[..eq]).ToLowerInvariant();
                string value = eq < 0 ? "" : part[(eq + 1)..];
                if (key is not ("server" or "type" or "same" or "change"))
                {
                    error = "Unknown option after # in the dns address: use #server=ip,ip, #type=A|AAAA|CNAME|MX|NS|TXT, #same and #change (separated by &), each once.";
                    return false;
                }

                if (!seen.Add(key))
                {
                    error = $"#{key} is given twice in the dns address: use each option once.";
                    return false;
                }

                if (key == "server")
                {
                    foreach (string s in value.Split(','))
                    {
                        if (!IPAddress.TryParse(s.Trim(), out var ip) || servers.Count == DnsExpect.MaxServers)
                        {
                            error = $"#server= needs 1 to {DnsExpect.MaxServers} IP addresses separated by commas (a name would need a lookup itself), for example dns://example.com#server=1.1.1.1,8.8.8.8";
                            return false;
                        }

                        servers.Add(ip);
                    }
                }
                else if (key == "type")
                {
                    type = value.ToUpperInvariant();
                    if (!DnsExpect.Types.Contains(type))
                    {
                        error = "#type= needs A, AAAA, CNAME, MX, NS or TXT, for example dns://example.com#type=AAAA";
                        return false;
                    }
                }
                else if (value.Length > 0)
                {
                    error = $"#{key} takes no value: write it alone, for example dns://example.com#server=1.1.1.1,8.8.8.8&{key}";
                    return false;
                }
                else if (key == "same") same = true;
                else change = true;
            }

            options = new DnsExpect(servers, type, same, change);
            return true;
        }

        // "host:443", "1.2.3.4:80", "[2001:db8::1]:443"
        private static bool TryHostPort(string text, out string host, out int port)
        {
            host = "";
            port = 0;
            string portText;

            if (text.StartsWith('['))
            {
                int close = text.IndexOf(']', StringComparison.Ordinal);
                if (close < 2 || close + 1 >= text.Length || text[close + 1] != ':') return false;
                host = text[1..close];
                // Brackets are how a URI writes an IPv6 LITERAL: a name or an IPv4 address in them ("[example.com]:443") is a mistake, not
                // another way to write the host, and taking it for the host would hide it (a zone, "fe80::1%4", is part of the literal).
                if (!IPAddress.TryParse(host, out var literal) || literal.AddressFamily != AddressFamily.InterNetworkV6) { host = ""; return false; }
                portText = text[(close + 2)..];
            }
            else
            {
                int colon = text.LastIndexOf(':');
                if (colon < 1) return false;
                host = text[..colon];
                portText = text[(colon + 1)..];
                if (host.Contains(':', StringComparison.Ordinal)) return false;   // a bare IPv6 address needs brackets
            }

            // "host/x" or "my host" would only fail at the connection, with an error that does not say why.
            if (Uri.CheckHostName(host) == UriHostNameType.Unknown) return false;

            return int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;
        }
    }
}
