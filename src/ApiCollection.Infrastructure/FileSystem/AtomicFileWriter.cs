namespace ApiCollection.Infrastructure.FileSystem;

/// <summary>
/// Writes several files so that a failure while writing leaves the existing files untouched: every file is first written to a
/// temporary neighbour, and only when all of them were written are they moved into place (in the order given).
/// Put the file others depend on last: the baseline snapshot refers to the collection by hash, so a crash between the two moves
/// leaves a baseline that no longer matches the collection, which the sync gate detects and blocks, rather than a silent mismatch.
/// </summary>
public static class AtomicFileWriter
{
    public static void WriteAll(IReadOnlyList<(string Path, string Content)> files)
    {
        var pending = new List<(string Temp, string Target)>();
        try
        {
            foreach (var (path, content) in files)
            {
                var target = Path.GetFullPath(path);
                var parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                var temp = $"{target}.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(temp, content);
                pending.Add((temp, target));
            }

            foreach (var (temp, target) in pending.ToList())
            {
                File.Move(temp, target, overwrite: true);
                pending.Remove((temp, target));
            }
        }
        finally
        {
            foreach (var (temp, _) in pending)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Best effort: a leftover *.tmp is harmless and ignored by version control.
                }
            }
        }
    }
}
