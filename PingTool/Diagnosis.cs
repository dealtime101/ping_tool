using System.Drawing;
using System.Net;
using System.Net.Sockets;

namespace PingTool
{
    // What the comparison needs to know about one monitored host.
    // OnLink: the address is in the network of one of this PC's own cards (same prefix): it is reached without the Internet even when
    // its address is a global one (a LAN machine with a global IPv6 address, a public IPv4 range handed out on the LAN).
    // Kind, Host, Port: what the target is probed with and where (see ServiceDiagnosis: targets of one host name are one service seen from
    // different sides). Host is empty when it is not known. Service: the service the user declared for it (TargetOptions), or null.
    // NetworksKnown: the cards were read and have networks, so "not in any of them" means something: a private address that is not
    // on the link is then a ROUTED one (a network behind the router, or at the far end of a VPN), whose path is not known. Without it
    // (the cards could not be read) the ranges of the addresses alone decide, as they always did.
    internal sealed record Target(string Name, IPAddress? Ip, HostState State, bool HasData, bool OnLink = false,
        ProbeKind Kind = ProbeKind.Icmp, string Host = "", int Port = 0, string? Service = null, int Family = 0, bool NoRecord = false,
        bool NetworksKnown = false);

    // The networks this PC is directly attached to, read from its cards (address and prefix length), kept for a while: the diagnosis is
    // asked for at every refresh and the cards do not change that often.
    internal static class LocalNetworks
    {
        public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

        // A seam for the tests: what the cards say.
        internal static Func<IReadOnlyList<(IPAddress Address, int Prefix)>> Read = ReadCards;
        private static IReadOnlyList<(IPAddress Address, int Prefix)>? cached;
        private static DateTime cachedAt;
        private static readonly object gate = new();

        private static IReadOnlyList<(IPAddress Address, int Prefix)> ReadCards()
        {
            System.Net.NetworkInformation.NetworkInterface[] nics;
            try { nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces(); }
            catch (Exception ex) when (IsCardFault(ex)) { return Array.Empty<(IPAddress, int)>(); }   // no card can be listed: the ranges alone decide (as before)

            return ReadFrom(nics.Select(nic => (Func<IReadOnlyList<(IPAddress, int)>>)(() => OneCard(nic))));
        }

        private static IReadOnlyList<(IPAddress, int)> OneCard(System.Net.NetworkInformation.NetworkInterface nic)
        {
            var found = new List<(IPAddress, int)>();
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                || nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) return found;
            foreach (var u in nic.GetIPProperties().UnicastAddresses)
                if (u.PrefixLength > 0) found.Add((u.Address, u.PrefixLength));
            return found;
        }

        private static bool IsCardFault(Exception ex) =>
            ex is System.Net.NetworkInformation.NetworkInformationException or InvalidOperationException or PlatformNotSupportedException;

        // The networks of the cards, one card at a time: a card that cannot be read (a virtual driver, a card being removed) is left out and
        // the others are still read. The whole list used to stop at the first one, and what was missing was kept for a minute (Lifetime).
        internal static IReadOnlyList<(IPAddress Address, int Prefix)> ReadFrom(IEnumerable<Func<IReadOnlyList<(IPAddress, int)>>> cards)
        {
            var found = new List<(IPAddress, int)>();
            foreach (var card in cards)
            {
                try { found.AddRange(card()); }
                catch (Exception ex) when (IsCardFault(ex)) { }   // this card tells nothing; the next one may
            }

            return found;
        }

        // The shortest prefix that still names a local network, by family. IPv4: 8 bits (10.0.0.0/8 is the widest private range). IPv6: 48,
        // the size of a site (a LAN is a /64, a corporate ULA a /48): a tunnel or VPN card announces a /32 (Teredo, 2001::/32) or a /16
        // (6to4), which holds a very large part of the Internet, and the hosts there would be called "on the link" and so the PC's own fault.
        internal static int MinPrefix(System.Net.Sockets.AddressFamily family) => family == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 48 : 8;

        // True when `ip` is in the same network as one of the (address, prefix) pairs: same family and the same first `prefix` bits.
        internal static bool Contains(IPAddress ip, IEnumerable<(IPAddress Address, int Prefix)> networks)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            byte[] target = ip.GetAddressBytes();
            foreach (var (address, prefix) in networks)
            {
                var a = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
                byte[] mine = a.GetAddressBytes();
                // A prefix shorter than the shortest of a real network would take in a large part of the Internet (a tunnel's odd mask): not
                // a local network.
                if (a.AddressFamily != ip.AddressFamily || prefix < MinPrefix(a.AddressFamily) || prefix > mine.Length * 8) continue;

                int whole = prefix / 8, rest = prefix % 8;
                bool same = target.AsSpan(0, whole).SequenceEqual(mine.AsSpan(0, whole));
                if (same && rest > 0)
                {
                    int mask = 0xFF << (8 - rest) & 0xFF;
                    same = (target[whole] & mask) == (mine[whole] & mask);
                }

                if (same) return true;
            }

            return false;
        }

        public static bool IsOnLink(IPAddress ip) => Contains(ip, Current());

        // True when the cards were read and have networks: "not in any of them" then says that the address is not on the link.
        public static bool IsKnown() => Current().Count > 0;

        // Listing the cards can take a while (a card being removed, a driver that is slow to answer), so it is done OUTSIDE the lock: the lock
        // guards the two fields only. Held through the read, it kept every other asker waiting for it, and the thread of the system that
        // says "the network changed" (Forget) waited for the read it was telling to be thrown away.
        private static IReadOnlyList<(IPAddress Address, int Prefix)> Current()
        {
            int seen;
            lock (gate)
            {
                if (cached is not null && DateTime.UtcNow - cachedAt <= Lifetime) return cached;
                seen = generation;
            }

            var read = Read();
            lock (gate)
            {
                // A Forget during the read says that what was read may be out of date already: it answers this question, and is not kept.
                if (seen == generation) { cached = read; cachedAt = DateTime.UtcNow; }
            }

            return read;
        }

        private static int generation;   // changed by every Forget, under the lock

        internal static void Forget() { lock (gate) { cached = null; generation++; } }
    }

    // Reads the states of SEVERAL targets side by side and says where the fault
    // most likely is. It is a hint from the pattern, not a proof: pinging cannot
    // tell a dead service from a firewall rule, and the sentence says so ("likely").
    internal static class Diagnosis
    {
        private const int MaxNames = 3;

        private static bool IsLoopback(IPAddress ip) => IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);

        // Private, loopback and link-local ranges: reachable without the Internet.
        public static bool IsLocal(IPAddress ip) => IsLinkScope(ip) || IsPrivateRange(ip);

        // Loopback and link-local addresses: on this PC or on its link by their very nature, whatever the cards say.
        private static bool IsLinkScope(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (IPAddress.IsLoopback(ip)) return true;
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                return b[0] == 169 && b[1] == 254;
            }

            return ip.IsIPv6LinkLocal;
        }

        // The private ranges (10/8, 172.16/12, 192.168/16, fc00::/7): an address of a network that is not on the Internet, but that is
        // not necessarily the network of this PC - it can be one behind the router, or at the far end of a VPN.
        private static bool IsPrivateRange(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                return b[0] == 10
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && b[1] == 168);
            }

            return ip.IsIPv6UniqueLocal;
        }

        // A private address that the cards say is NOT on the link: its path (a router, a VPN) is not known, so a fault there is not a
        // fault of "a local device".
        private static bool IsRouted(Target t) => t.Ip is { } ip && t.NetworksKnown && !t.OnLink && IsPrivateRange(ip);

        // null = nothing useful to say (fewer than two targets have answered or timed out yet). The comparison of all the targets, then, on
        // their own lines, what the probes of one service (the targets of one host name) say side by side (see ServiceDiagnosis).
        public static string? For(IReadOnlyList<Target> all)
        {
            string? general = Compare(all);
            if (general is null) return null;

            var services = ServiceDiagnosis.For(all);
            if (services.Count == 0) return general;

            var shown = services.Take(ServiceDiagnosis.MaxServices).ToList();
            if (services.Count > shown.Count) shown.Add($"(+{services.Count - shown.Count} more services)");
            return general + "\n" + string.Join("\n", shown);
        }

        private static string? Compare(IReadOnlyList<Target> all)
        {
            // A loopback target (127.0.0.1, ::1) answers from this PC's own stack whatever its network card does: it proves nothing about
            // the link, the router or the Internet, and would make "Local targets answer" true with the cable unplugged. It is left
            // out of the comparison (the window still shows its state).
            var targets = all.Where(t => t.HasData && !(t.Ip is { } ip && IsLoopback(ip))).ToList();
            int n = targets.Count;
            if (n < 2) return null;

            var down = targets.Where(t => t.State == HostState.Down).ToList();
            var slow = targets.Where(t => t.State == HostState.Degraded).ToList();

            if (down.Count == 0)
            {
                if (slow.Count == 0) return $"All {n} targets answer normally.";
                if (slow.Count == n) return $"All {n} targets are slow or losing packets: likely this PC's link or the path they share.";
                return $"{Names(slow)} slow or losing packets while {Others(n - slow.Count, "is fine", "are fine")}: likely specific to {Specific(slow.Count)}.";
            }

            if (down.Count == n)
                // Possibilities, not a verdict: this PC, its link, or a path (or a fault) the targets share can each give this; the targets
                // may even be one device. Ruling the targets out would need independent ones and a check point that answers.
                return $"All {n} targets are down: this PC, its network link, or something the targets share (a path, a provider) can be the cause; pinging alone cannot tell which.";

            // Some are down and EVERY other one is slow or losing packets: nothing answers well, whatever the target: that points at what they
            // share (this PC's link, the path), not at the one that is down. (It used to say that the others "answer".)
            if (slow.Count > 0 && down.Count + slow.Count == n)
                return $"{down.Count} of {n} targets {(down.Count == 1 ? "is" : "are")} down and the other {(slow.Count == 1 ? "one is" : slow.Count + " are")} slow or losing packets: likely this PC's link or the path they share.";

            // Local: a link-local address, or one in the network of this PC's own cards (a global address on the LAN), or a private
            // address when the cards could not say otherwise. A private address of a network the cards are NOT in (behind a router, at the
            // far end of a VPN) is neither local nor Internet: its path is unknown, so it is in neither list and a fault there is not
            // put on "a local device" (the general sentence says "that target or its path").
            var local = targets.Where(t => t.Ip is not null && !IsRouted(t) && (t.OnLink || IsLocal(t.Ip))).ToList();
            var remote = targets.Where(t => t.Ip is not null && !IsRouted(t) && !(t.OnLink || IsLocal(t.Ip))).ToList();

            // A target whose address is not known (a local name that does not resolve, "nas.local") is neither local nor Internet:
            // counting it as Internet would blame the router or the link for a device that may be off. Without an address for
            // every target the local / Internet reading is not made, and the general sentence below is used.
            bool allKnown = targets.All(t => t.Ip is not null);
            if (allKnown && local.Count > 0 && remote.Count > 0)
            {
                // The number follows the targets, as everywhere else in this file: one router is "the local target", not "the local ones".
                // The ones that "answer" may be slow or losing packets: that is said too, or the sentence would blame one side only.
                if (local.All(t => t.State != HostState.Down) && remote.All(t => t.State == HostState.Down))
                {
                    var slowLocal = local.Where(t => t.State == HostState.Degraded).ToList();
                    return $"{(local.Count == 1 ? "The local target answers" : "Local targets answer")} but {(remote.Count == 1 ? "the Internet target is" : "every Internet target is")} down: likely the router or the Internet link."
                        + (slowLocal.Count == 0 ? "" : $" {Names(slowLocal)} slow or losing packets, so the local network is not clean either.");
                }

                if (local.All(t => t.State == HostState.Down) && remote.All(t => t.State != HostState.Down))
                {
                    var slowRemote = remote.Where(t => t.State == HostState.Degraded).ToList();
                    return $"{(remote.Count == 1 ? "The Internet target answers" : "Internet targets answer")} but "
                        + (local.Count == 1 ? "the local target is down: likely that local device." : "the local ones are down: likely those local devices.")
                        + (slowRemote.Count == 0 ? "" : $" {Names(slowRemote)} slow or losing packets.");
                }
            }

            // Slow ones among the rest are said, not counted as answering normally.
            if (slow.Count > 0)
                return $"Only {Names(down)} down and {Names(slow)} slow or losing packets while {Others(n - down.Count - slow.Count, "answers", "answer")}: likely specific to {Specific(down.Count + slow.Count)}.";

            return $"Only {Names(down)} down while {Others(n - down.Count, "answers", "answer")}: likely specific to {Specific(down.Count)}.";
        }

        // "that target or its path" / "those targets or their paths": the possessive follows the number too.
        private static string Specific(int count) => count == 1 ? "that target or its path" : "those targets or their paths";

        private static string Others(int count, string one, string many) =>
            count == 1 ? $"the other target {one}" : $"the other {count} targets {many}";

        private static string Names(List<Target> list)
        {
            var shown = list.Take(MaxNames).Select(t => t.Name).ToList();
            string text = string.Join(", ", shown);
            if (list.Count > MaxNames) text += $" +{list.Count - MaxNames}";
            return text + (list.Count == 1 ? " is" : " are");
        }
    }

    // One colour per host on the comparison graph. Okabe-Ito colours that stay
    // apart for the common colour-blindness types, all at least 3:1 on the graph's
    // 40,40,40 background; they repeat after six hosts, so the legend carries the names.
    internal static class HostPalette
    {
        private static readonly Color[] Colors =
        {
            Color.FromArgb(86, 180, 233),   // sky blue
            Color.FromArgb(230, 159, 0),    // orange
            Color.FromArgb(0, 158, 115),    // bluish green
            Color.FromArgb(240, 228, 66),   // yellow
            Color.FromArgb(204, 121, 167),  // reddish purple
            Color.FromArgb(213, 94, 0),     // vermillion
        };

        public static int Count => Colors.Length;

        public static Color ColorFor(int index) => Colors[((index % Colors.Length) + Colors.Length) % Colors.Length];
    }
}
