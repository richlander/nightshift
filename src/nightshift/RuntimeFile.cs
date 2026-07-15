namespace Nightshift;

/// <summary>Writes a per-user runtime file at 0600 so lease ids are never briefly world-readable.</summary>
internal static class RuntimeFile
{
    public static void WriteRestricted(string path, string content)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, content);
            return;
        }

        using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }
}
