using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PingTool
{
    // A change of the PC's own network during a run (a VPN, a cable unplugged, a wake from sleep, a Wi-Fi access point when it changes
    // the address or the gateway), dated, so that a cut seen by the pings can be put next to what happened on this side. A move between
    // two access points of the same network that keeps the address and the gateway is not seen by the cards: the SSID, the BSSID and the
    // signal come from the native Wi-Fi interface of Windows, read by WifiWatch (PIN507.2), whose sentences join these events.
    // Note: written by the user (Mark an event), not seen by the program; the report keeps the two apart.
    internal sealed record NetworkEvent(DateTimeOffset Time, string Text, bool Note = false)
    {
        public const int MaxNoteLength = 120;

        // The event for a note, or null when there is nothing to note: blank, or only control characters. Spaces are folded and control
        // characters dropped (a line break would break the report's table row and the timeline's line), the text cut to MaxNoteLength;
        // `target` (a name) starts the text when the note applies to one target only.
        public static NetworkEvent? FromNote(string? note, DateTimeOffset time, string? target)
        {
            string clean = string.Join(' ', new string((note ?? "").Where(c => !char.IsControl(c) || char.IsWhiteSpace(c)).ToArray())
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (clean.Length > MaxNoteLength) clean = TextCut.Units(clean, MaxNoteLength);   // not through an emoji
            if (clean.Length == 0) return null;

            return new NetworkEvent(time, target is null ? clean : target + ": " + clean, Note: true);
        }
    }

    // One network card that is up, as far as the events need it. Addresses = its IPv4 addresses, sorted, joined.
    internal sealed record NicState(string Name, string Addresses, string Gateway);

    internal static class NetworkWatch
    {
        public const int MaxTextLength = 220;

        // The cards that are up and have an IPv4 address (loopback and the tunnel pseudo-cards Teredo, 6to4 and ISATAP left out: they change
        // for no reason that matters; a VPN adapter of type Tunnel is kept - see IsPseudoTunnel). The Wi-Fi name (SSID) is not read here: Windows hands it out through a different
        // interface (WifiWatch reads it); the card name already tells Wi-Fi from Ethernet and from a VPN adapter.
        // null when the cards could not be read (all of them or one: a list with a card missing would be read as that card being
        // unplugged). The caller keeps what it knew and ignores this reading; an EMPTY list means the PC really has no network.
        // error: why the reading failed, null when it did not (it comes with THIS reading: kept in a field of the class it would be the reason
        // of an earlier failure after a good reading, or the one of another reading).
        // read: the reading itself, a parameter so that a failure can be replayed in a test.
        public static List<NicState>? Snapshot(out string? error, Func<List<NicState>>? read = null)
        {
            error = null;
            try
            {
                return (read ?? ReadCards)();
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
            {
                System.Diagnostics.Debug.WriteLine("Network cards could not be read: " + ex.Message);
                error = ex.Message;   // Debug output does not exist in a release build: the reason goes with the reading, for the window to say
                return null;
            }
        }

        // What changed between what the window knew (null = nothing yet) and this reading (null = could not be read): the new "last
        // known" state and the sentence, if any. A failed reading changes nothing in what is known; it is SAID once per run of failures
        // (failureSaid is kept by the caller), so that a person looking for the reason of a missing change finds it in the events.
        // The first reading only sets the base.
        // readError: the reason Snapshot gave with a reading that failed (null = it gave none).
        public static (List<NicState>? Last, string? Text) Compare(List<NicState>? last, List<NicState>? now, ref bool failureSaid, string? readError = null)
        {
            if (now is null)
            {
                if (failureSaid) return (last, null);
                failureSaid = true;
                string why = readError is { Length: > 0 } reason ? reason.Length <= 80 ? reason : TextCut.Units(reason, 77) + "..." : "no reason given";
                return (last, $"The network cards could not be read ({why}): changes of the network are not recorded until they can be.");
            }

            // The first good reading after a failure that was SAID closes the period it opened: without it the events say when the cards
            // stopped being read and never when they were again, and nobody can tell over which time the changes are unknown. What changed
            // meanwhile follows when it is known (against what was known before the failure).
            bool backAgain = failureSaid;
            failureSaid = false;
            string? changes = last is null ? null : Describe(last, now);
            if (!backAgain) return (now, changes);

            string text = changes is null ? ReadAgain : ReadAgain + " " + changes;
            return (now, text.Length <= MaxTextLength ? text : TextCut.Units(text, MaxTextLength - 3) + "...");
        }

        // Said when the cards can be read again after a failure that was said.
        internal const string ReadAgain = "The network cards can be read again.";

        // The tunnel cards Windows makes by itself (Teredo, 6to4, ISATAP: they come and go for no reason that matters). Not every card of
        // the type "Tunnel": a VPN adapter can be one, and its connecting, disconnecting or changing address is exactly what this watch is for.
        internal static bool IsPseudoTunnel(NetworkInterfaceType type, string name, string description) =>
            type == NetworkInterfaceType.Tunnel
            && new[] { name, description }.Any(t => t.Contains("Teredo", StringComparison.OrdinalIgnoreCase)
                || t.Contains("6to4", StringComparison.OrdinalIgnoreCase) || t.Contains("ISATAP", StringComparison.OrdinalIgnoreCase));

        private static List<NicState> ReadCards()
        {
            var states = new List<NicState>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || IsPseudoTunnel(nic.NetworkInterfaceType, nic.Name, nic.Description)) continue;

                var props = nic.GetIPProperties();
                var addresses = props.UnicastAddresses.Select(a => a.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).OrderBy(a => a, StringComparer.Ordinal).ToList();
                if (addresses.Count == 0) continue;

                states.Add(new NicState(nic.Name, string.Join(", ", addresses), GatewaysText(props.GatewayAddresses.Select(g => g.Address))));
            }

            return states;
        }

        // ALL the IPv4 gateways of a card, once each and in a fixed order (the order the system lists them in means nothing): a gateway that
        // is added, removed or replaced is a change, the same set listed differently is not. 0.0.0.0 is no gateway. "" when there is none.
        internal static string GatewaysText(IEnumerable<System.Net.IPAddress> gateways) =>
            string.Join(", ", gateways.Where(g => g.AddressFamily == AddressFamily.InterNetwork && !g.Equals(System.Net.IPAddress.Any))
                .Select(g => g.ToString()).Distinct(StringComparer.Ordinal).OrderBy(g => g, StringComparer.Ordinal));

        // null when nothing that matters changed; otherwise one line saying what.
        public static string? Describe(IReadOnlyList<NicState> before, IReadOnlyList<NicState> after)
        {
            var b = before.GroupBy(n => n.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var a = after.GroupBy(n => n.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            static string Gw(string g) => g.Length == 0 ? "none" : g;
            // "gateway 192.168.1.1", "gateways 192.168.1.1, 10.0.0.1": the word follows how many there are (on either side of a change).
            static string Word(params string[] gateways) => gateways.Any(g => g.Contains(", ", StringComparison.Ordinal)) ? "gateways" : "gateway";
            var parts = new List<string>();
            foreach (string name in b.Keys.Union(a.Keys).OrderBy(n => n, StringComparer.Ordinal))
            {
                bool had = b.TryGetValue(name, out var was), has = a.TryGetValue(name, out var now);
                if (!had) parts.Add($"{name} connected ({now!.Addresses}, {Word(now.Gateway)} {Gw(now.Gateway)})");
                else if (!has) parts.Add($"{name} disconnected");
                else if (was!.Addresses != now!.Addresses || was.Gateway != now.Gateway)
                {
                    string text = was.Addresses != now.Addresses ? $"{name}: {was.Addresses} -> {now.Addresses}" : $"{name}: {now.Addresses}";
                    if (was.Gateway != now.Gateway) text += $" ({Word(was.Gateway, now.Gateway)} {Gw(was.Gateway)} -> {Gw(now.Gateway)})";
                    parts.Add(text);
                }
            }

            if (parts.Count == 0) return null;
            // Only the cards with an IPv4 address are followed (see Snapshot): when none is left it is that which is said, not a "lost
            // connection" - a PC whose network is IPv6 may well still be connected.
            string line = after.Count == 0 ? "No network card with an IPv4 address is up now; " + string.Join("; ", parts) : string.Join("; ", parts);
            return line.Length <= MaxTextLength ? line : TextCut.Units(line, MaxTextLength - 3) + "...";   // the names of the cards are the user's: not through a character
        }
    }
}
