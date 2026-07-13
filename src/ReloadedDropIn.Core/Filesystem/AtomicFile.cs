namespace ReloadedDropIn.Core.Filesystem;

public static class AtomicFile
{
    /// <summary>
    /// Writes text via a temp file + rename so an interrupted write never leaves
    /// a truncated config behind (plan §19).
    /// </summary>
    public static void WriteAllText(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException($"path has no parent directory: {path}", nameof(path));
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.tmp-{Environment.ProcessId}");
        File.WriteAllText(tempPath, contents);
        File.Move(tempPath, path, overwrite: true);
    }
}
