namespace PingTool
{
    // THE rule for "the same target": scheme and host (or whole address, for a ping, tcp:// or dns:// target) ignore the case,
    // the user information, the path and the query of an http(s) URL do not: https://site/Status and https://site/status can be two
    // resources (PIN461.89, .153, .180), and a password is not its lower-case self (PIN507.118).
    internal sealed class TargetKey : IEqualityComparer<string>
    {
        public static readonly TargetKey Comparer = new();

        // Where the host of an http(s) address ends: built once (the key is computed for every comparison of two addresses), not a new
        // array at each call.
        private static readonly System.Buffers.SearchValues<char> HostEnd = System.Buffers.SearchValues.Create("/?#");

        public static string Of(string? address)
        {
            string text = (address ?? "").Trim();
            int sep = text.IndexOf("://", StringComparison.Ordinal);
            if (sep < 0) return text.ToLowerInvariant();

            string scheme = text[..sep].ToLowerInvariant();
            if (scheme is not ("http" or "https")) return text.ToLowerInvariant();

            string rest = text[(sep + 3)..];
            int end = rest.AsSpan().IndexOfAny(HostEnd);
            string authority = end < 0 ? rest : rest[..end], tail = end < 0 ? "" : rest[end..];
            // The authority is [user[:password]@]host[:port]: only the host (and the port, which has no letters worth the name) ignores the
            // case. A user name or a password is case-sensitive, and https://user:Secret@site/ is not https://user:secret@site/. The last "@"
            // ends the user information (a password may hold an unencoded one; the path and the query, after it, never count).
            int at = authority.LastIndexOf('@');
            string userInfo = at < 0 ? "" : authority[..(at + 1)], host = at < 0 ? authority : authority[(at + 1)..];
            return scheme + "://" + userInfo + host.ToLowerInvariant() + tail;
        }

        public bool Equals(string? a, string? b) => string.Equals(Of(a), Of(b), StringComparison.Ordinal);

        public int GetHashCode(string address) => Of(address).GetHashCode();

        public static bool Same(string? a, string? b) => Comparer.Equals(a, b);
    }
}
