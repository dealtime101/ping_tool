using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace PingTool
{
    // The Wi-Fi connection as Windows (the WLAN AutoConfig service) gives it: the name of the network (SSID), the access point (BSSID) and the
    // signal quality, 0 to 100. NetworkWatch only sees a card's address and gateway: a move to another access point of the same network, or a
    // signal that fades until the pings are lost, changes neither. Quality is Windows' own scale (about 0 = -100 dBm, 100 = -50 dBm).
    internal sealed record WifiState(string Ssid, string Bssid, int Quality);

    // What one reading found. State = null: not connected to a Wi-Fi network (or no Wi-Fi on this PC). Unavailable = why nothing could be
    // read, when that is worth saying; a PC without the Wi-Fi service says nothing (that is not a fault).
    internal sealed record WifiReading(WifiState? State, string? Unavailable = null);

    internal static class WifiWatch
    {
        // A signal under 25 % is called weak, and it is back when it reaches 35 %: the gap keeps one that hovers at the limit from
        // writing a line at every reading.
        public const int WeakBelow = 25;
        public const int WeakRecovered = 35;

        // WLAN_CONNECTION_ATTRIBUTES (wlanapi.h), read by offset: isState (4 bytes), the connection mode (4), the profile name (256 UTF-16
        // characters), then WLAN_ASSOCIATION_ATTRIBUTES: the SSID (a length and 32 bytes), the BSS type, the BSSID (6 bytes), the PHY type
        // and index, the signal quality. The same on 32 and 64 bits (no pointer before them).
        private const int StateOffset = 0, SsidLengthOffset = 520, SsidOffset = 524, BssidOffset = 560, QualityOffset = 576, MinSize = 580;
        private const int InterfaceConnected = 1;   // wlan_interface_state_connected
        private const int OpcodeCurrentConnection = 7;   // wlan_intf_opcode_current_connection
        private const int InterfaceListHeader = 8, InterfaceInfoSize = 532, InterfaceStateOffset = 528;   // WLAN_INTERFACE_INFO: GUID, 256 characters, state
        private const int ServiceDoesNotExist = 1060, ServiceNotActive = 1062;   // no Wi-Fi service on this PC: nothing to say

        // The connection of one interface as WLAN_CONNECTION_ATTRIBUTES gives it; null when it is not connected or the data is too short to be one.
        internal static WifiState? ParseConnection(ReadOnlySpan<byte> data)
        {
            if (data.Length < MinSize) return null;
            if (BinaryPrimitives.ReadInt32LittleEndian(data[StateOffset..]) != InterfaceConnected) return null;

            int length = BinaryPrimitives.ReadInt32LittleEndian(data[SsidLengthOffset..]);
            if (length < 0 || length > 32) return null;

            string ssid = Clean(Encoding.UTF8.GetString(data.Slice(SsidOffset, length)));
            string bssid = string.Join(":", data.Slice(BssidOffset, 6).ToArray().Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
            int quality = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(data[QualityOffset..]), 0, 100);
            return new WifiState(ssid, bssid, quality);
        }

        // A name goes into a sentence between quotes: its own quotes and control characters are written as ' and ?; a network that hides its
        // name has none to show.
        private static string Clean(string ssid)
        {
            var text = new StringBuilder();
            foreach (char c in ssid) text.Append(c == '"' ? '\'' : char.IsControl(c) ? '?' : c);
            return text.Length == 0 ? "(hidden name)" : text.ToString();
        }

        // Reads the connection now. Never throws: Windows without the Wi-Fi component, a service that is stopped, an adapter that goes away
        // in the middle of the reading are all "nothing to read" (and said once when it is not the usual absence of Wi-Fi).
        public static WifiReading Read()
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                int result = Native.WlanOpenHandle(2, IntPtr.Zero, out _, out handle);
                if (result is ServiceDoesNotExist or ServiceNotActive) return new WifiReading(null);
                if (result != 0) return Unavailable(result);

                result = Native.WlanEnumInterfaces(handle, IntPtr.Zero, out IntPtr list);
                if (result != 0) return Unavailable(result);
                try
                {
                    int count = Marshal.ReadInt32(list, 0);
                    for (int i = 0; i < count && i < 16; i++)
                    {
                        IntPtr item = IntPtr.Add(list, InterfaceListHeader + i * InterfaceInfoSize);
                        if (Marshal.ReadInt32(item, InterfaceStateOffset) != InterfaceConnected) continue;

                        var guid = Marshal.PtrToStructure<Guid>(item);
                        if (Native.WlanQueryInterface(handle, ref guid, OpcodeCurrentConnection, IntPtr.Zero, out int size, out IntPtr data, IntPtr.Zero) != 0) continue;
                        try
                        {
                            var bytes = new byte[Math.Clamp(size, 0, 4096)];
                            Marshal.Copy(data, bytes, 0, bytes.Length);
                            if (ParseConnection(bytes) is { } state) return new WifiReading(state);
                        }
                        finally
                        {
                            Native.WlanFreeMemory(data);
                        }
                    }
                }
                finally
                {
                    Native.WlanFreeMemory(list);
                }

                return new WifiReading(null);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException or ArgumentException or OverflowException)
            {
                return new WifiReading(null);   // no Wi-Fi component in this Windows: the same as no Wi-Fi
            }
            finally
            {
                if (handle != IntPtr.Zero) Native.WlanCloseHandle(handle, IntPtr.Zero);
            }
        }

        private static WifiReading Unavailable(int code) =>
            new(null, "Wi-Fi details (network name, access point, signal) could not be read: Windows error " + code.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ". They are not recorded until they can be.");

        private static class Native
        {
            [DllImport("wlanapi.dll")]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            internal static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

            [DllImport("wlanapi.dll")]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            internal static extern int WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

            [DllImport("wlanapi.dll")]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            internal static extern int WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

            [DllImport("wlanapi.dll")]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            internal static extern int WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, int opCode, IntPtr reserved, out int dataSize, out IntPtr data, IntPtr opcodeValueType);

            [DllImport("wlanapi.dll")]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
            internal static extern void WlanFreeMemory(IntPtr memory);
        }
    }

    // Turns successive readings into the sentences of the network events: what changed, once. The first reading only sets the base. A reading
    // that could not be taken changes nothing (it is not "the Wi-Fi went away"), and is said once per run of failures.
    internal sealed class WifiTracker
    {
        private WifiState? last;
        private bool known, weak, unavailableSaid;

        public IReadOnlyList<string> Observe(WifiReading reading)
        {
            var said = new List<string>();
            if (reading.Unavailable is not null)
            {
                if (!unavailableSaid) said.Add(reading.Unavailable);
                unavailableSaid = true;
                return said;
            }

            unavailableSaid = false;
            var now = reading.State;
            if (!known)
            {
                known = true;
                last = now;
                weak = now is { Quality: < WifiWatch.WeakBelow };
                return said;
            }

            if (last is null && now is not null)
                said.Add($"Wi-Fi connected to \"{now.Ssid}\" (access point {now.Bssid}, signal {now.Quality}%)");
            else if (last is not null && now is null)
                said.Add($"Wi-Fi disconnected from \"{last.Ssid}\"");
            else if (last is not null && now is not null && last.Ssid != now.Ssid)
                said.Add($"Wi-Fi moved from \"{last.Ssid}\" to \"{now.Ssid}\" (access point {now.Bssid}, signal {now.Quality}%)");
            else if (last is not null && now is not null && last.Bssid != now.Bssid)
                said.Add($"Wi-Fi roamed to another access point of \"{now.Ssid}\" ({last.Bssid} -> {now.Bssid}, signal {last.Quality}% -> {now.Quality}%)");

            if (now is null) weak = false;
            else if (!weak && now.Quality < WifiWatch.WeakBelow)
            {
                weak = true;
                said.Add($"Wi-Fi signal weak: {now.Quality}% on \"{now.Ssid}\"");
            }
            else if (weak && now.Quality >= WifiWatch.WeakRecovered)
            {
                weak = false;
                said.Add($"Wi-Fi signal back to {now.Quality}% on \"{now.Ssid}\"");
            }

            last = now;
            return said;
        }
    }
}
