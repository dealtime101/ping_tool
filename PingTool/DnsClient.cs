using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PingTool
{
    // What a dns:// target may ask for: the resolvers to ask (IP literals), the record type, and whether the resolvers must agree.
    internal sealed record DnsExpect(IReadOnlyList<IPAddress> Servers, string Type, bool MustMatch, bool WatchChange)
    {
        public static readonly string[] Types = { "A", "AAAA", "CNAME", "MX", "NS", "TXT" };
        public const int MaxServers = 4;

        internal static ushort Code(string type) => type switch { "A" => 1, "NS" => 2, "CNAME" => 5, "MX" => 15, "TXT" => 16, _ => (ushort)28 };
    }

    // One question to one resolver over UDP, for the dns:// options #server= and #type=. The system resolver cannot be told which server to
    // ask, and hides what it answered; this one asks the server named and reads the records back. A reply cut short (TC bit) is read as far as
    // it goes: the name servers of a CDN or a mail host fit in 512 bytes, and the point is to compare, not to list everything.
    internal static class DnsClient
    {
        internal sealed record Reply(int Rcode, IReadOnlyList<string> Records);

        internal static int Port = 53;   // only a test changes it

        public static async Task<Reply> QueryAsync(string name, string type, IPAddress server, CancellationToken token)
        {
            byte[] id = new byte[2];
            Random.Shared.NextBytes(id);
            byte[] query = BuildQuery(id, name, DnsExpect.Code(type));

            using var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            await socket.ConnectAsync(server, Port, token);   // connected: replies from anyone else are dropped by the system
            await socket.SendAsync(query, token);

            var buffer = new byte[1232];
            while (true)
            {
                int n = await socket.ReceiveAsync(buffer, token);
                if (n >= 12 && buffer[0] == id[0] && buffer[1] == id[1]) return Parse(buffer, n);   // a stray datagram with another id is ignored
            }
        }

        internal static byte[] BuildQuery(byte[] id, string name, ushort type)
        {
            var packet = new List<byte> { id[0], id[1], 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };   // recursion desired, one question
            foreach (string label in new IdnMapping().GetAscii(name.TrimEnd('.')).Split('.'))
            {
                byte[] bytes = Encoding.ASCII.GetBytes(label);
                packet.Add((byte)bytes.Length);
                packet.AddRange(bytes);
            }

            packet.AddRange(new byte[] { 0, (byte)(type >> 8), (byte)type, 0, 1 });   // end of name, type, class IN
            return packet.ToArray();
        }

        internal static Reply Parse(byte[] p, int n)
        {
            int rcode = p[3] & 0x0F, questions = p[4] << 8 | p[5], answers = p[6] << 8 | p[7];
            int at = 12;
            for (int i = 0; i < questions; i++) at = SkipName(p, at, n) + 4;

            var records = new List<string>();
            for (int i = 0; i < answers && at + 10 <= n; i++)
            {
                at = SkipName(p, at, n);
                if (at + 10 > n) break;
                int type = p[at] << 8 | p[at + 1], length = p[at + 8] << 8 | p[at + 9];
                int data = at + 10;
                at = data + length;
                if (at > n) break;

                switch (type)
                {
                    case 1 when length == 4: records.Add(new IPAddress(p.AsSpan(data, 4)).ToString()); break;
                    case 28 when length == 16: records.Add(new IPAddress(p.AsSpan(data, 16)).ToString()); break;
                    case 2 or 5: records.Add(ReadName(p, data, n)); break;
                    case 15 when length > 2: records.Add((p[data] << 8 | p[data + 1]).ToString(CultureInfo.InvariantCulture) + " " + ReadName(p, data + 2, n)); break;
                    case 16:
                        var text = new StringBuilder();
                        for (int s = data; s < data + length && s < n; s += 1 + p[s]) text.Append(Encoding.UTF8.GetString(p, s + 1, Math.Min(p[s], n - s - 1)));
                        records.Add(text.ToString());
                        break;
                }
            }

            return new Reply(rcode, records);
        }

        private static int SkipName(byte[] p, int at, int n)
        {
            while (at < n)
            {
                int len = p[at];
                if (len == 0) return at + 1;
                if ((len & 0xC0) == 0xC0) return at + 2;   // a pointer ends the name
                at += 1 + len;
            }

            return n;
        }

        private static string ReadName(byte[] p, int at, int n)
        {
            var labels = new List<string>();
            for (int hops = 0; at < n && hops < 64; hops++)
            {
                int len = p[at];
                if (len == 0) break;
                if ((len & 0xC0) == 0xC0) { if (at + 1 >= n) break; at = (len & 0x3F) << 8 | p[at + 1]; continue; }
                if (at + 1 + len > n) break;
                labels.Add(Encoding.ASCII.GetString(p, at + 1, len));
                at += 1 + len;
            }

            return string.Join('.', labels);
        }
    }
}
