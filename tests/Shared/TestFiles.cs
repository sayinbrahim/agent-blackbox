using System.Text.Json.Nodes;

namespace Blackbox.Tests.Shared;

/// <summary>Temp project directories and fixture loading for tests. Linked into both test projects.</summary>
public sealed class TempProject : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("bbx-test-").FullName;

    public string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }
}

public static class Fixtures
{
    public static string Dir { get; } = FindDir();

    /// <summary>Loads a fixture and rewrites the Windows paths in it (C:\repo\...) to point at <paramref name="root"/>, so tests run on any OS.</summary>
    public static string Load(string name, string root)
    {
        var text = File.ReadAllText(System.IO.Path.Combine(Dir, name));
        // In JSON text a path separator is an escaped backslash pair; turn it into '/'.
        text = text.Replace(@"\\", "/");
        return text.Replace("C:/repo", JsonValue.Create(root.Replace('\\', '/'))!.ToJsonString().Trim('"'));
    }

    public static JsonObject LoadObject(string name, string root) => (JsonObject)JsonNode.Parse(Load(name, root))!;

    private static string FindDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "fixtures", "hook-events");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new DirectoryNotFoundException("fixtures/hook-events not found");
    }
}
