using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PingTool
{
    // What the network card says about itself, as far as the diagnosis needs it.
    // Addresses = the card's own unicast addresses (IPv4 and IPv6): what tells which card the system routes the Internet traffic through.
    internal sealed record NicSnapshot(string Name, bool IsUp, bool IsVirtualOrLoopback, IReadOnlyList<IPAddress> Gateways, IReadOnlyList<IPAddress> DnsServers,
        IReadOnlyList<IPAddress>? Addresses = null);

    // "Diagnose my connection": the targets that let the report say WHERE the fault is (this PC, the router, the provider, the
    // Internet) for someone who does not know their gateway or DNS server addresses: the default gateway and the DNS servers read
    // from the active network card, two well-known Internet addresses, a name lookup and a public web address.
    internal static class DiagnosticTargets
    {
        public const string ProfileName = "Diagnose my connection";
        public const int MaxDnsServers = 2;

        // Internet references that do not depend on the user's provider (anycast, run by large operators).
        // A read-only view: `readonly` on an array only fixes the reference, the elements stay open to any code of the assembly.
        public static readonly IReadOnlyList<string> InternetReferences = Array.AsReadOnly(new[] { "1.1.1.1", "8.8.8.8", "dns://www.cloudflare.com", "https://www.cloudflare.com/" });

        // The same, for a PC that has no IPv4 gateway but an IPv6 one (an IPv6-only connection): the two literal addresses are IPv6 ones
        // (they would only fail there, and a failure of those proves nothing about the connection), the name lookup and the web address are
        // the same, a name reaches whichever family there is.
        public static readonly IReadOnlyList<string> InternetReferencesIPv6 = Array.AsReadOnly(new[] { "2606:4700:4700::1111", "2001:4860:4860::8888", "dns://www.cloudflare.com", "https://www.cloudflare.com/" });

        // The list of targets, local ones first, no duplicates. gatewayFound says whether the first one is a real gateway.
        // routedFrom6: the same, for IPv6 (the local IPv6 address the system sends from towards the Internet), used to tell which card carries
        // an IPv6-only connection when there are several.
        public static List<string> From(IEnumerable<NicSnapshot> nics, out bool gatewayFound, IPAddress? routedFrom = null, IPAddress? routedFrom6 = null)
        {
            var targets = new List<string>();
            gatewayFound = false;

            // The card the traffic leaves by: among the cards that are up, real, and have an IPv4 gateway, the one that holds the address
            // the system routes the Internet from (routedFrom: the system has weighed the routes and their metrics, the list of cards is
            // in no such order: a Wi-Fi and an Ethernet card up together, or a VPN adapter, would otherwise give the gateway of the wrong one).
            // Without that address (no route, or not asked) it is the first of them, as it always was.
            var usable = nics.Where(n => n.IsUp && !n.IsVirtualOrLoopback && n.Gateways.Any(IsUsableIPv4)).ToList();
            var nic = (routedFrom is null ? null : usable.FirstOrDefault(n => n.Addresses is not null && n.Addresses.Any(a => a.Equals(routedFrom))))
                ?? usable.FirstOrDefault();
            // No real card has a gateway, but the system does send the Internet traffic out by a card the heuristics took for virtual (the
            // only card of a virtual machine whose description says "virtual", a PC that reaches the Internet through a tunnel adapter
            // alone): that card IS the connection, and "no gateway" would be wrong. A real card with a gateway still comes first: with a
            // VPN up, the router to test is the physical one, not the far end of the tunnel.
            if (nic is null && routedFrom is not null)
                nic = nics.FirstOrDefault(n => n.IsUp && n.Gateways.Any(IsUsableIPv4) && n.Addresses is not null && n.Addresses.Any(a => a.Equals(routedFrom)));
            if (nic is not null)
            {
                gatewayFound = true;
                targets.Add(nic.Gateways.First(IsUsableIPv4).ToString());
                foreach (var dns in DnsOf(nic).Take(MaxDnsServers)) targets.Add(dns.ToString());
            }
            else if (V6Card(nics, routedFrom6) is { } v6)
            {
                // An IPv6-only connection: no IPv4 gateway anywhere, a real card with an IPv6 one. That the card also has IPv4 DNS servers
                // (handed out by DHCPv4, or by a router that does both) does not make its IPv6 router any less there: the gateway family
                // is told by the gateways, not by the DNS servers. The gateway is the router; its address keeps its zone (fe80::1%4: a
                // link-local address means nothing without it); the DNS servers of the card follow, the IPv6 ones first.
                gatewayFound = true;
                targets.Add(v6.Gateways.First(IsUsableIPv6).ToString());
                foreach (var dns in v6.DnsServers.Where(IsUsableIPv6).Concat(v6.DnsServers.Where(IsUsableIPv4)).Take(MaxDnsServers)) targets.Add(dns.ToString());
                targets.AddRange(InternetReferencesIPv6);
                return targets.Distinct(TargetKey.Comparer).ToList();
            }
            else
            {
                // No IPv4 gateway on a real card (an IPv6-only gateway, or none seen): the DNS servers of the active real card are
                // still worth testing, or the report can no longer tell a fault of the provider's DNS from the rest. gatewayFound stays
                // false: there is no router in the list, and the caller says so.
                var realUp = nics.Where(n => n.IsUp && !n.IsVirtualOrLoopback && DnsOf(n).Any()).ToList();
                var dnsCard = (routedFrom is null ? null : realUp.FirstOrDefault(n => n.Addresses is not null && n.Addresses.Any(a => a.Equals(routedFrom))))
                    ?? realUp.FirstOrDefault();
                if (dnsCard is not null)
                    foreach (var dns in DnsOf(dnsCard).Take(MaxDnsServers)) targets.Add(dns.ToString());
            }

            targets.AddRange(InternetReferences);
            return targets.Distinct(TargetKey.Comparer).ToList();
        }

        // The usable DNS servers of a card of both families, the IPv4 ones first (what the diagnosis always tested), the IPv6 ones after: a
        // connection whose DNS servers are IPv6 only is tested too. The limit is applied by the caller on this whole list, not per family.
        private static IEnumerable<IPAddress> DnsOf(NicSnapshot n) =>
            n.DnsServers.Where(IsUsableIPv4).Concat(n.DnsServers.Where(IsUsableIPv6)).Distinct();

        // The real card with an IPv6 gateway: the one that holds the address the system sends the Internet traffic from (routedFrom6) when
        // there are several, else the first, as it was.
        private static NicSnapshot? V6Card(IEnumerable<NicSnapshot> nics, IPAddress? routedFrom6)
        {
            var candidates = nics.Where(n => n.IsUp && !n.IsVirtualOrLoopback && n.Gateways.Any(IsUsableIPv6)).ToList();
            return (routedFrom6 is null ? null : candidates.FirstOrDefault(n => n.Addresses is not null && n.Addresses.Any(a => a.Equals(routedFrom6))))
                ?? candidates.FirstOrDefault();
        }

        // Said wherever the diagnosis targets are built (the profile, --diagnose, --headless --diagnose): one wording. It says what is missing
        // (the router) and what is tested all the same; the DNS servers of the active card are in the list when the card has some, so
        // it does not say that the Internet references are all there is.
        public const string NoGatewayMessage = "No network gateway was found (is the PC connected?). The router is not in the list: without it, the report cannot say whether the fault is on your side. The Internet references, and the DNS servers of the active network card when it has some, are tested all the same.";

        // The targets of the connection, and when no gateway was found the text to say so (the same message everywhere, with the reason when
        // part of the cards could not be read). The reason belongs to THIS discovery and comes back with it: it is not kept anywhere that
        // another discovery, running at the same time, could write over.
        public static List<string> Discover(out bool gatewayFound, out string noGatewayText)
        {
            var targets = Discover(CardReaders(), out gatewayFound, out string? cardProblem, LocalAddressToInternet(), LocalAddressToInternet(AddressFamily.InterNetworkV6));
            noGatewayText = gatewayFound ? "" : NoGatewayTextOf(cardProblem);
            return targets;
        }

        // The address the system would send from to reach the Internet, in the family asked: a UDP socket "connected" to a public address
        // only asks the routing table (nothing is sent), and tells the local address it chose. null when there is no route.
        internal static IPAddress? LocalAddressToInternet(AddressFamily family = AddressFamily.InterNetwork)
        {
            try
            {
                using var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect(IPAddress.Parse(family == AddressFamily.InterNetworkV6 ? "2606:4700:4700::1111" : "1.1.1.1"), 53);
                return (socket.LocalEndPoint as IPEndPoint)?.Address;
            }
            catch (SocketException)
            {
                return null;
            }
        }

        // One reader per card, each run on its own: a card whose properties cannot be read (a virtual adapter, a faulty driver) is
        // left out, and the real card next to it is still used. The list of cards itself failing leaves no card, as before.
        // problem: why cards were left out, null when all were read (kept for the message that says no gateway was found: Debug output does
        // not exist in a release build, and without it a missing gateway reads as "the PC is not connected" when a card the router is on
        // could not be read).
        internal static List<NicSnapshot> ReadCards(IEnumerable<Func<NicSnapshot>> readers, out string? problem)
        {
            var snapshots = new List<NicSnapshot>();
            int skipped = 0;
            string? why = null;
            foreach (var read in readers)
            {
                try
                {
                    snapshots.Add(read());
                }
                catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
                {
                    System.Diagnostics.Debug.WriteLine("A network card could not be read: " + ex.Message);
                    skipped++;
                    why ??= ex.Message;
                }
            }

            problem = skipped == 0 ? null : string.Create(CultureInfo.InvariantCulture,
                $"{skipped} network card(s) could not be read ({(why is { Length: > 80 } ? TextCut.Units(why, 77) + "..." : why)}): the gateway may be on one of them.");
            return snapshots;
        }

        // The message for "no gateway was found", with the reason when part of the cards could not be read.
        public static string NoGatewayTextOf(string? cardProblem) => cardProblem is { } problem ? NoGatewayMessage + " " + problem : NoGatewayMessage;

        internal static List<string> Discover(IEnumerable<Func<NicSnapshot>> readers, out bool gatewayFound, out string? cardProblem,
            IPAddress? routedFrom = null, IPAddress? routedFrom6 = null) =>
            From(ReadCards(readers, out cardProblem), out gatewayFound, routedFrom, routedFrom6);

        // Words of a card's description that say it is not a piece of hardware: the virtual switches of Hyper-V, VirtualBox, VMware and
        // Docker, the tunnel adapters of VPNs, the pseudo-interfaces. Most of them declare the type "Ethernet", so the type alone lets them
        // through. "Hyper-V" is NOT in the list on its own: "Microsoft Hyper-V Network Adapter" is the real card of a virtual machine.
        private static readonly string[] VirtualWords =
        {
            "virtual", "vethernet", "tap-windows", "tap-win", "wintun", "wireguard", "openvpn", "nordlynx", "tailscale", "zerotier", "hamachi",
            "docker", "vmnet", "vboxnet", "loopback", "pseudo", "npcap", "teredo", "isatap", "6to4", "miniport",
        };

        // Names the system gives its virtual interfaces (Linux and macOS: docker0, veth…, virbr0, br-…, tun0, tap0, wg0, utun0, lo).
        // Long or distinctive prefixes: whatever follows (docker0, veth9a9ac9f, br-e4de740698dd, virbr0). Short ones: digits only
        // (tun0, tap1, wg0, utun3, lo), so that "Tunnel to the office" and "lounge" are not taken for them.
        private static readonly string[] VirtualNamePrefixes = { "docker", "veth", "virbr", "br-", "vmnet", "vboxnet" };
        private static readonly string[] VirtualShortNames = { "tun", "tap", "wg", "utun", "lo" };

        internal static bool LooksVirtual(string name, string description, NetworkInterfaceType type)
        {
            if (type is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) return true;
            if (VirtualWords.Any(w => description.Contains(w, StringComparison.OrdinalIgnoreCase) || name.Contains(w, StringComparison.OrdinalIgnoreCase))) return true;
            return VirtualNamePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                || VirtualShortNames.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase) && name[p.Length..].All(char.IsDigit));
        }

        private static List<Func<NicSnapshot>> CardReaders()
        {
            var readers = new List<Func<NicSnapshot>>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    var card = nic;   // each reader keeps its own card
                    readers.Add(() =>
                    {
                        var props = card.GetIPProperties();
                        return new NicSnapshot(
                            card.Name,
                            card.OperationalStatus == OperationalStatus.Up,
                            LooksVirtual(card.Name, card.Description, card.NetworkInterfaceType),
                            props.GatewayAddresses.Select(g => g.Address).ToList(),
                            props.DnsAddresses.ToList(),
                            props.UnicastAddresses.Select(u => u.Address).ToList());   // both families: the route is told by either
                    });
                }
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
            {
                System.Diagnostics.Debug.WriteLine("Network cards could not be listed: " + ex.Message);
                // Said like a card that could not be read (see ReadCards), so that the "no gateway" message gives the reason.
                string reason = "the list of cards: " + ex.Message;
                readers.Add(() => throw new InvalidOperationException(reason));
            }

            return readers;
        }

        // The one rule for a gateway AND for a DNS server (they were two copies of the same line): an IPv4 address that is not 0.0.0.0
        // ("no gateway") and not the PC itself (the 127.0.0.53 stub resolver of Linux says nothing about the network). IPv6 addresses are
        // not taken here (IsUsableIPv6 does, for a connection with no IPv4). The link-local range 169.254.0.0/16 is NOT left out, on
        // purpose: a router never has such an address from DHCP, but resolvers do (the one of an AWS instance, 169.254.169.253, the
        // node-local DNS of a Kubernetes node, 169.254.20.10), and leaving them out would drop a DNS server that really answers.
        private static bool IsUsableIPv4(IPAddress a) =>
            a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any) && !IPAddress.IsLoopback(a);

        // The IPv6 counterpart, for a connection that has no IPv4 at all: not "::" (no gateway), not ::1. Link-local (fe80::%zone) is
        // kept, a router announces itself that way, with the zone the address carries. Site-local (fec0::/10, deprecated in 2004) is not:
        // Windows lists fec0:0:0:ffff::1, ::2 and ::3 as the DNS servers of a card that has none over IPv6, they never answer, and the
        // report would blame the provider's DNS for it. (ProbeRunner leaves them out of the servers it compares for the same reason.)
        private static bool IsUsableIPv6(IPAddress a) =>
            a.AddressFamily == AddressFamily.InterNetworkV6 && !a.Equals(IPAddress.IPv6Any) && !IPAddress.IsLoopback(a) && !a.IsIPv6SiteLocal;
    }
}
