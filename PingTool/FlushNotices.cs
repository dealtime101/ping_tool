namespace PingTool
{
    // What was already said about the automatic log, and what the next flush has to say: the state of the telling, kept by the window.
    //   Warned      a failure of the writing was said and the file has not been written since (one balloon per failure, not one per tick)
    //   Reported    how many dropped pings the last balloon about drops counted
    //   DropWarned  the drops were said during the failure that is going on (said again with the final figures when the folder works)
    internal readonly record struct FlushState(bool Warned, int Reported, bool DropWarned);

    internal static class FlushNotices
    {
        // The balloons for one flush: its outcome (written or not) and what the log says. The window shows them in order, and a balloon
        // that is followed at once by another is replaced by it, before it can be read: the telling is made so that one flush gives
        // ONE balloon about one thing.
        public static (FlushState State, List<string> Balloons) After(FlushState state, bool written, bool closing, string? lastError, string? dropNote, int dropped)
        {
            var balloons = new List<string>();
            bool warned = state.Warned, dropWarned = state.DropWarned;
            int reported = state.Reported;

            if (written) warned = false;
            else if (!warned && !closing)
            {
                warned = true;
                balloons.Add(Loc.T("log.notwritten", lastError ?? "") + (dropNote is { } lost ? " " + lost : ""));
                // The drops are in this balloon, with the cause: a second one made of the note alone would replace it at once, and the
                // cause with it. They are counted as said, for the failure that is going on.
                if (dropNote is not null) { reported = dropped; dropWarned = true; }
            }

            // Pings that were thrown away because the folder stayed unwritable too long, never silently: said when it first happens,
            // and again with the final figures when the folder works again (not at every tick of a long failure).
            if (dropped > reported && !closing && (written || !dropWarned))
            {
                reported = dropped;
                dropWarned = !written;
                balloons.Add(dropNote!);
            }

            if (written) dropWarned = false;
            return (new FlushState(warned, reported, dropWarned), balloons);
        }
    }
}
