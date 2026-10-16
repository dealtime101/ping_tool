using System.Net;
using System.Text.RegularExpressions;

namespace PingTool
{
    // What the sender hides before a report leaves the computer (an ISP, a forum, a colleague): the report keeps its figures, its dates and
    // the links between targets, incidents and routes, but not what identifies the network.
    internal sealed record ReportMasking(bool Targets, bool Ips, bool NetworkNames, bool Computer)
    {
        public static readonly ReportMasking None = new(false, false, false, false);

        public bool Any => Targets || Ips || NetworkNames || Computer;

        // The command line's --hide: "targets,ips" or "all" (targets, ips, cards, computer). An empty list or an unknown word is refused.
        public static bool TryParse(string? text, out ReportMasking masking)
        {
            masking = None;
            if (string.IsNullOrWhiteSpace(text)) return false;

            bool targets = false, ips = false, cards = false, computer = false;
            foreach (string word in text.Split(',', StringSplitOptions.TrimEntries))
            {
                switch (word.ToLowerInvariant())
                {
                    case "targets": targets = true; break;
                    case "ips": ips = true; break;
                    case "cards": cards = true; break;
                    case "computer": computer = true; break;
                    case "all": targets = ips = cards = computer = true; break;
                    default: return false;
                }
            }

            masking = new ReportMasking(targets, ips, cards, computer);
            return true;
        }

        // The sentence the report carries about it, so that its reader knows "target-1" is not a name that was typed.
        public string Describe()
        {
            var hidden = new List<string>();
            if (Targets) hidden.Add("target names and addresses");
            if (Ips) hidden.Add("IP addresses");
            if (NetworkNames) hidden.Add("names of the network cards, Wi-Fi networks and access points");
            if (Computer) hidden.Add("the computer name");
            return "Hidden by the sender: " + string.Join(", ", hidden) + ". The same pseudonym (target-1, public-ip-2, local-ip-1, network-card-1) stands for the same "
                + "target, address or card everywhere in this report, so incidents and routes stay linked.";
        }
    }

    // The report data with what was hidden, the key (for the sender only) and the two ways to mask any other text.
    internal sealed record MaskedReport(ReportData Data, List<string> Key, Func<string, string> Host, Func<string, string> Text);

    // Replaces what a ReportMasking hides by pseudonyms, in a COPY of the report data (the data of the window is never touched). The pseudonyms
    // follow the order of the targets, so two reports of the same targets read the same; `key` says what each one stands for, for the sender
    // only: it is never put in the report.
    internal static class ReportAnonymizer
    {
        public static ReportData Apply(ReportData d, ReportMasking masking, out List<string> key)
        {
            var masked = Mask(d, masking);
            key = masked.Key;
            return masked.Data;
        }

        // The same, with what is needed to mask more than the report: the measures of an archive go through the same pseudonyms.
        public static MaskedReport Mask(ReportData d, ReportMasking masking)
        {
            var key = new List<string>();
            if (!masking.Any) return new MaskedReport(d, key, text => text, text => text);

            var s = new Scrubber(d, masking, key);
            var hosts = d.Hosts.Select(x => x with
            {
                Address = s.HostName(x.Address),
                Label = masking.Targets || x.Label is null ? null : s.Text(x.Label),
                IpText = masking.Targets ? s.NoIp(x.IpText) : s.Text(x.IpText),
                Notice = x.Notice is null ? null : s.Text(x.Notice),
                Service = x.Service is null ? null : s.Text(x.Service),
            }).ToList();

            var incidents = d.Incidents.Select(i => new Incident
            {
                Host = s.HostName(i.Host),
                Kind = i.Kind,
                Start = i.Start,
                End = i.End,
                FailedPings = i.FailedPings,
                Cause = s.Text(i.Cause),
                LossPercent = i.LossPercent,
                AvgMs = i.AvgMs,
                Occurrence = i.Occurrence,
                Path = i.Path,   // only there for "has a route"; what the report shows is PathTextOverride
                PathTextOverride = i.Path is null ? null : s.Text(i.PathText()),
            }).ToList();

            var events = d.NetworkEvents?.Select(n => n with { Text = s.Text(masking.NetworkNames ? s.Cards(n.Text) : n.Text) }).ToList();

            var data = d with
            {
                Machine = masking.Computer ? "hidden" : d.Machine,
                Hosts = hosts,
                Diagnosis = d.Diagnosis is null ? null : s.Text(d.Diagnosis),
                IncidentSummary = s.Text(d.IncidentSummary),
                Incidents = incidents,
                NetworkEvents = events,
                MaskedNote = masking.Describe(),
            };
            s.AddAddressesAndCardsToKey();
            return new MaskedReport(data, key, s.HostName, s.Text);
        }

        // The pings of a measures file, under the same pseudonyms as the report.
        public static List<LogEntry> MaskEntries(MaskedReport masked, IEnumerable<LogEntry> entries) =>
            entries.Select(e => e with { Host = masked.Host(e.Host), Detail = masked.Text(e.Detail) }).ToList();

        private sealed class Scrubber
        {
            private static readonly Regex WifiSentence = new(@"^Wi-Fi (?:connected to|disconnected from|moved from|roamed to another access point of|signal (?:weak|back to)) ", RegexOptions.CultureInvariant);
            private static readonly Regex Ipv4 = new(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?!\d)", RegexOptions.CultureInvariant);
            private static readonly Regex Ipv6 = new(@"(?<![\w:.])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?![\w:])", RegexOptions.CultureInvariant);

            private readonly ReportMasking masking;
            private readonly List<string> key;
            private readonly Dictionary<string, string> targetNames = new(TargetKey.Comparer);   // typed address -> pseudonym
            private readonly Dictionary<string, string> replacements = new(StringComparer.OrdinalIgnoreCase);   // what to find in a text -> pseudonym
            private readonly Dictionary<string, string> ipNames = new(StringComparer.Ordinal);   // normalised address -> pseudonym
            private readonly Dictionary<string, string> cardNames = new(StringComparer.Ordinal);
            private readonly Dictionary<string, string> serviceNames = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, string> ssidNames = new(StringComparer.Ordinal);
            private readonly Dictionary<string, string> accessPoints = new(StringComparer.Ordinal);
            private int localIps, publicIps;
            private Regex? targets;

            public Scrubber(ReportData d, ReportMasking masking, List<string> key)
            {
                this.masking = masking;
                this.key = key;
                if (!masking.Targets) return;

                int n = 0;
                foreach (var x in d.Hosts)
                {
                    string name = "target-" + (++n).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    targetNames[x.Address] = name;
                    Add(x.Address, name);
                    if (ProbeTarget.TryParse(x.Address, out var probe, out _) && probe is not null) Add(probe.Host, name);
                    if (!string.IsNullOrWhiteSpace(x.Label)) Add(x.Label.Trim(), name);
                    // A service name is a name the user chose (and the diagnosis lines say it): service-1, service-2...
                    if (!string.IsNullOrWhiteSpace(x.Service) && !serviceNames.ContainsKey(x.Service.Trim()))
                    {
                        string service = "service-" + (serviceNames.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        serviceNames[x.Service.Trim()] = service;
                        Add(x.Service.Trim(), service);
                        key.Add(service + " = " + x.Service.Trim());
                    }

                    // The address a name resolved to says as much as the name: it goes under the same pseudonym.
                    int space = x.IpText.LastIndexOf(' ');
                    if (space >= 0 && IPAddress.TryParse(x.IpText[(space + 1)..], out var resolved)) Add(resolved.ToString(), name);
                    key.Add(name + " = " + x.Address + (string.IsNullOrWhiteSpace(x.Label) ? "" : " (" + x.Label.Trim() + ")"));
                }

                // The target address of each route (a name seen from here may resolve to another address than later).
                foreach (var i in d.Incidents)
                    if (i.Path is { } path && targetNames.TryGetValue(i.Host, out var name)) Add(path.Target.ToString(), name);

                if (replacements.Count == 0) return;   // an empty alternation would match everywhere

                var found = replacements.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape);
                targets = new Regex(@"(?<![\w.\-])(?:" + string.Join("|", found) + @")(?![\w\-]|\.\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            private void Add(string real, string name)
            {
                real = real.Trim();
                if (real.Length > 0 && !replacements.ContainsKey(real)) replacements[real] = name;
            }

            // A typed target address: its pseudonym when the targets are hidden, otherwise the text as it would be scrubbed.
            public string HostName(string address) =>
                masking.Targets && targetNames.TryGetValue(address, out var name) ? name : Text(address);

            // Any text of the report.
            public string Text(string text)
            {
                if (targets is not null) text = targets.Replace(text, m => replacements[m.Value]);
                if (masking.Ips) text = Ipv6.Replace(Ipv4.Replace(text, IpName), IpName);
                return text;
            }

            // The address of a resolved target, when the targets are hidden: not even a pseudonym, it would tell nothing.
            public string NoIp(string text) => Ipv6.Replace(Ipv4.Replace(text, "(hidden)"), "(hidden)");

            private string IpName(Match m)
            {
                if (!IPAddress.TryParse(m.Value, out var ip)) return m.Value;   // not an address after all (a time, a version)
                string id = ip.ToString();
                if (ipNames.TryGetValue(id, out var known)) return known;
                string name = IsLocal(ip)
                    ? "local-ip-" + (++localIps).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "public-ip-" + (++publicIps).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return ipNames[id] = name;
            }

            // Private, loopback and link-local addresses stay told apart from public ones: that a hop is inside the home or the company
            // network is something the reader needs to know, and does not identify it.
            internal static bool IsLocal(IPAddress ip)
            {
                if (IPAddress.IsLoopback(ip)) return true;
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
                var b = ip.GetAddressBytes();
                return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
            }

            // "Ethernet connected (192.168.0.5, gateway 192.168.0.1)" -> "network-card-1 connected (...)": the names of the cards sit at the
            // start of each part of the sentence NetworkWatch.Describe writes. Sentences that are not about one card are left alone.
            public string Cards(string text)
            {
                // A card is often called "Wi-Fi" too: only the sentences WifiTracker writes are about a Wi-Fi network.
                if (WifiSentence.IsMatch(text)) return Wifi(text);

                var parts = text.Split("; ");
                for (int p = 0; p < parts.Length; p++)
                {
                    string part = parts[p];
                    if (part.StartsWith("The network cards could not be read", StringComparison.Ordinal) || part.StartsWith("No network card", StringComparison.Ordinal)) continue;

                    var named = Regex.Match(part, @"^(?<n>.+?) (?:connected \(|disconnected$)", RegexOptions.CultureInvariant);
                    if (!named.Success) named = Regex.Match(part, @"^(?<n>[^:]+): ", RegexOptions.CultureInvariant);
                    if (!named.Success) continue;

                    string card = named.Groups["n"].Value;
                    if (!cardNames.TryGetValue(card, out var name))
                        cardNames[card] = name = "network-card-" + (cardNames.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    parts[p] = name + part[card.Length..];
                }

                return string.Join("; ", parts);
            }

            // "Wi-Fi roamed to another access point of "Home" (aa:bb:cc:dd:ee:01 -> aa:bb:cc:dd:ee:02, ...)" -> the names of the Wi-Fi networks
            // (between quotes, see WifiWatch) and the access points (their addresses) become wifi-network-N and access-point-N.
            private string Wifi(string text)
            {
                text = Regex.Replace(text, "\"(?<n>[^\"]*)\"", m => "\"" + Pseudonym(ssidNames, m.Groups["n"].Value, "wifi-network-") + "\"", RegexOptions.CultureInvariant);
                return Regex.Replace(text, @"(?<![0-9A-Fa-f:])(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f:])",
                    m => Pseudonym(accessPoints, m.Value.ToLowerInvariant(), "access-point-"), RegexOptions.CultureInvariant);
            }

            private static string Pseudonym(Dictionary<string, string> names, string real, string prefix)
            {
                if (!names.TryGetValue(real, out var name))
                    names[real] = name = prefix + (names.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return name;
            }

            public void AddAddressesAndCardsToKey()
            {
                foreach (var (real, name) in ipNames) key.Add(name + " = " + real);
                foreach (var (real, name) in cardNames) key.Add(name + " = " + real);
                foreach (var (real, name) in ssidNames) key.Add(name + " = " + real);
                foreach (var (real, name) in accessPoints) key.Add(name + " = " + real);
            }
        }
    }
}
