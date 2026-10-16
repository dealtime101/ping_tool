namespace PingTool
{
    // What is said when a resource that the program embeds is not found: the name that was asked for AND the names that the build has, which
    // is what shows at once whether the name is mistyped, or the file was never embedded, or the build gave it another name (the SDK
    // prefixes the root namespace to a resource unless it has a LogicalName).
    internal static class ResourceNote
    {
        public static string Missing(string wanted, IEnumerable<string> available)
        {
            var names = available.OrderBy(n => n, StringComparer.Ordinal).ToList();
            string have = names.Count == 0 ? "none" : string.Join(", ", names.Select(n => "\"" + n + "\""));
            return $"The embedded resource \"{wanted}\" was not found, so the generic icon is shown. The resources of this build: {have}.";
        }
    }
}
