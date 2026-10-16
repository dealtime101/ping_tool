using System.Net;

namespace PingTool
{
    // Several targets can be ONE service seen from different sides: dns://example.com, example.com (ping), tcp://example.com:443,
    // https://example.com/. Read side by side they say more than separate lights: "the name does not resolve" is not "the server is down".
    // The service is declared by the user (the Service box of "Name and limits": the targets that share it are one service, whatever their
    // host names) or, when nobody declared one, it is the targets with the same host name. A target typed as an address joins the service
    // whose name resolved to that address (so that "reached by address" can be told from "reached by name").
    //
    // The sentences are hints from the pattern, worded as such ("likely"): pinging cannot tell a dead service from a firewall rule.
    internal static class ServiceDiagnosis
    {
        public const int MaxServices = 2;

        // One sentence per service that has a story to tell, the ones with a problem only; empty when there is nothing to say. Nothing is said
        // when every target is down: that points at this PC or its link, and a failing name would then be a consequence, not a cause.
        public static List<string> For(IReadOnlyList<Target> targets)
        {
            var said = new List<string>();
            var data = targets.Where(t => t.HasData && t.Host.Length > 0 && !t.NoRecord && !t.IsOwn && !(t.Ip is { } ip && IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip))).ToList();
            if (data.Count < 2 || data.All(t => t.State == HostState.Down)) return said;

            // A declared service gathers its targets whatever their host names; without one, the targets of a host name are the service.
            // The keys are told apart (a service called like a host is not that host).
            var services = new Dictionary<string, (string Name, List<Target> Named, List<Target> ByAddress)>(StringComparer.OrdinalIgnoreCase);
            void Join(string key, string name, Target t, bool byAddress)
            {
                if (!services.TryGetValue(key, out var group)) services[key] = group = (name, new List<Target>(), new List<Target>());
                (byAddress ? group.ByAddress : group.Named).Add(t);
            }

            foreach (var t in data)
            {
                bool literal = IPAddress.TryParse(t.Host, out _);
                if (t.Service is { Length: > 0 } declared) Join("service:" + declared, declared, t, literal);
                else if (!literal) Join("host:" + t.Host, t.Host, t, false);
            }

            // The targets typed as an address, with no service of their own, join the service whose name resolved to that address.
            foreach (var t in data.Where(t => t.Service is not { Length: > 0 } && IPAddress.TryParse(t.Host, out _)))
            {
                var literal = IPAddress.Parse(t.Host);
                foreach (var group in services.Values)
                    if (group.Named.Any(n => n.Ip is { } resolved && Same(resolved, literal))) group.ByAddress.Add(t);
            }

            foreach (var (name, named, byAddress) in services.Values)
                if (named.Count + byAddress.Count > 1 && Read(name, named, byAddress) is { } sentence) said.Add(sentence);

            return said;
        }

        private static bool Same(IPAddress a, IPAddress b) =>
            (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).Equals(b.IsIPv4MappedToIPv6 ? b.MapToIPv4() : b);

        // The state of one kind of probe of the service: down when every one is, slow when the ones that answer are slow, up otherwise;
        // null when there is none of that kind.
        private static HostState? State(IEnumerable<Target> targets, ProbeKind kind) => Worst(targets.Where(t => t.Kind == kind).ToList());

        private static HostState? Worst(List<Target> list)
        {
            if (list.Count == 0) return null;
            var alive = list.Where(t => t.State != HostState.Down).ToList();
            if (alive.Count == 0) return HostState.Down;
            return alive.All(t => t.State == HostState.Degraded) ? HostState.Degraded : HostState.Up;
        }

        // The sentence for one service, or null when its probes do not tell a story (all well, or too few kinds to compare).
        internal static string? Read(string name, List<Target> named, List<Target> byAddress)
        {
            // A dual-stack service probed on each family (#family=4 and #family=6 entries): one family weaker than the other is told first.
            var v4 = Worst(named.Where(t => t.Family == 4).ToList());
            var v6 = Worst(named.Where(t => t.Family == 6).ToList());
            if (v4 is not null && v6 is not null && v4 != v6 && (v4 == HostState.Up || v6 == HostState.Up))
            {
                string weak = v6 == HostState.Up ? "IPv4" : "IPv6", strong = weak == "IPv4" ? "IPv6" : "IPv4";
                string how = (weak == "IPv4" ? v4 : v6) == HostState.Down ? "fails" : "is slow or failing at times";
                return $"{name}: {weak} {how} while {strong} answers normally: likely a {weak} problem (the path, the provider or the {weak} side of the server), not the service as a whole; an application that tries {weak} first waits or fails while one that tries {strong} first does not.";
            }

            var dns = State(named, ProbeKind.Dns);
            var ping = State(named, ProbeKind.Icmp);
            var tcp = State(named, ProbeKind.Tcp);
            var web = State(named, ProbeKind.Http);
            var direct = Worst(byAddress);

            int kinds = new[] { dns, ping, tcp, web }.Count(s => s is not null);
            if (kinds < 2 && !(dns is not null && direct is not null)) return null;

            // The kinds other than the name lookup that are present.
            var others = new[] { ping, tcp, web }.Where(s => s is not null).ToList();

            if (dns == HostState.Down && direct is not null && direct != HostState.Down)
                return $"{name}: the name does not resolve (the DNS probe fails) while the same address answers when reached directly: likely the DNS service or the resolver, not the server.";

            if (dns == HostState.Down && others.Count > 0 && others.All(s => s == HostState.Down))
                return $"{name}: the name does not resolve, so the other probes of this name fail too: likely DNS (the resolver or the zone); a server that is down is not ruled out.";

            if (dns != HostState.Down && others.Count >= 2 && others.All(s => s == HostState.Down))
                return $"{name}: {(dns == HostState.Up ? "the name resolves but " : "")}the server answers none of the probes (ping, port, web): likely the server or the path to it.";

            if (ping is not null and not HostState.Down && tcp == HostState.Down)
                return $"{name}: the host answers ping but port {named.First(t => t.Kind == ProbeKind.Tcp && t.State == HostState.Down).Port} does not open: likely the service is stopped or a firewall blocks the port.";

            if (web == HostState.Down && tcp is not null and not HostState.Down)
                return $"{name}: the port opens but the web request fails: likely the web application (or its certificate, or a redirect), not the network.";

            if (web == HostState.Down && tcp is null && ping is not null and not HostState.Down)
                return $"{name}: the host answers ping but the web request fails: likely the web application or its port, not the network.";

            if (web == HostState.Degraded && (tcp == HostState.Up || ping == HostState.Up))
                return $"{name}: the network answers normally but the web response is slow or failing at times: likely the web application or the load of its server.";

            return null;
        }
    }
}
