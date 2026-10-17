using System.ComponentModel;
using System.Diagnostics;

namespace PingTool
{
    internal static class FileOpener
    {
        // What the box says after a file was written: where it is, and the offer to open it - nothing else tells the user the
        // file exists (the report is for a provider or a colleague: it is usually looked at at once).
        // savedKey = the key of the sentence for what was written ("ui.saved.report", "ui.saved.comparison"): a sentence of its own for each,
        // since the agreement of the words with the thing differs from one language to another.
        public static string SavedMessage(string savedKey, string path) => Loc.T(savedKey, path);

        // Writing succeeded: say where, offer to open with the program Windows associates with the file.
        public static void OfferToOpen(IWin32Window owner, string savedKey, string path)
        {
            if (MessageBox.Show(owner, SavedMessage(savedKey, path), "PingTool", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;

            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) when (OpenFailure.Is(ex))
            {
                MessageBox.Show(owner, Loc.T("ui.openfail", ex.Message, path), "PingTool", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
