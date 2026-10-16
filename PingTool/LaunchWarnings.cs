namespace PingTool
{
    // What a launch has to say: a settings file that could not be read, addresses left out, no gateway found. With the window up each is a
    // box (once the window is shown); started minimized (the window never shows, so no box does) all of them go in one balloon, so that a
    // monitoring that runs with an incomplete list or the default settings is not silent about it.
    internal static class LaunchWarnings
    {
        public const int MaxBalloonChars = 250;   // a balloon holds 255 characters; beyond that the system cuts or refuses the text

        // One text for the balloon: the warnings one under the other, the whole cut at MaxBalloonChars with a pointer to the window.
        public static string Balloon(IReadOnlyList<string> warnings)
        {
            const string More = "... (open the window for the rest)";
            string text = string.Join("\n", warnings.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim().Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ')));
            return text.Length <= MaxBalloonChars ? text : text[..(MaxBalloonChars - More.Length)].TrimEnd() + More;
        }
    }
}
