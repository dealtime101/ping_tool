using System.Text;

namespace PingTool
{
    // A file written whole or not at all. File.WriteAllText empties the file that is there BEFORE it writes the new content: a full disk, a
    // share that goes away or a crash in between leaves an empty or half written file, and the last good one is lost. Here the content goes
    // to a file of its own name in the SAME folder (so that the final move is a rename, not a copy), is flushed to the disk, and only then
    // replaces the target; whatever fails before that leaves the target as it was, and the temporary file is removed.
    internal static class AtomicFile
    {
        // move is File.Move with overwrite, a parameter so that a failure of the last step can be replayed in a test.
        public static void Write(string path, Action<Stream> write, Action<string, string>? move = null)
        {
            move ??= (from, to) => File.Move(from, to, overwrite: true);
            string full = Path.GetFullPath(path);
            string temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    write(stream);
                    stream.Flush(true);   // the bytes are on the disk before the name points at them
                }

                move(temp, full);
            }
            catch
            {
                try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                throw;
            }
        }

        // UTF-8 without a byte order mark, like the files written before.
        public static void WriteAllText(string path, string text, Action<string, string>? move = null) =>
            Write(path, stream =>
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
            }, move);
    }
}
