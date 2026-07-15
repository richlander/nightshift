namespace Nightshift.Tests;

using Nightshift.Config;
using Xunit;

/// <summary>
/// The tolerant config reader behind the socket precedence: a valid file yields its <c>socket</c> setting,
/// while a missing, empty, partial, or malformed file yields <c>null</c> so a broken config never wedges
/// resolution. Reads an explicit path to stay free of process-wide environment state.
/// </summary>
public sealed class ConfigFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ns-config-{Guid.NewGuid():N}");

    public ConfigFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort temp cleanup.
        }
    }

    [Fact]
    public void Load_ReadsSocketFromValidFile()
    {
        string path = Write("""{ "socket": "/from-config.sock" }""");

        NightshiftConfig? config = ConfigFile.Load(path);

        Assert.NotNull(config);
        Assert.Equal("/from-config.sock", config.Socket);
    }

    [Fact]
    public void Load_ToleratesCommentsAndTrailingCommas()
    {
        string path = Write(
            """
            {
                // pin the coordination socket
                "socket": "/commented.sock",
            }
            """);

        Assert.Equal("/commented.sock", ConfigFile.Load(path)?.Socket);
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
        => Assert.Null(ConfigFile.Load(Path.Combine(_dir, "does-not-exist")));

    [Fact]
    public void Load_EmptyFile_ReturnsNull()
        => Assert.Null(ConfigFile.Load(Write("   ")));

    [Fact]
    public void Load_PartialFile_ParsesWithNullSocket()
    {
        // A file without the socket key is valid — resolution simply falls through to the next source.
        NightshiftConfig? config = ConfigFile.Load(Write("""{ "context": "prod" }"""));

        Assert.NotNull(config);
        Assert.Null(config.Socket);
    }

    [Fact]
    public void Load_MalformedJson_ReturnsNull()
        => Assert.Null(ConfigFile.Load(Write("{ not json")));

    private string Write(string contents)
    {
        string path = Path.Combine(_dir, "config");
        File.WriteAllText(path, contents);
        return path;
    }
}
