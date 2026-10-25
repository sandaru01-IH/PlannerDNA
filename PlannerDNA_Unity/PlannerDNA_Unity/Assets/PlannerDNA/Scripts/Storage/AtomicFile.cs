using System.IO;
using System.Text;

namespace PlannerDNA.Storage
{
    /// <summary>
    /// Writes a file so that a crash mid-write never leaves a half-written target:
    /// data goes to a temporary sibling, is flushed to disk, then swapped into place.
    /// </summary>
    public static class AtomicFile
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static void WriteAllText(string path, string contents)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            string temp = path + ".tmp";
            byte[] bytes = Utf8NoBom.GetBytes(contents);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }

            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        /// <summary>Appends one line and flushes it to disk (for append-only logs).</summary>
        public static void AppendLine(string path, string line)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            byte[] bytes = Utf8NoBom.GetBytes(line.Replace("\n", " ").Replace("\r", " ") + "\n");
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }
    }
}
