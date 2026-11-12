using SaveTether.Core.FileSystem;

namespace SaveTether.Tests.FileSystem;

/// <summary>
/// A temporary area for one test: a <see cref="Work"/> folder to build trees in, and a
/// <see cref="Canary"/> folder beside it that no operation on the work area may ever change.
/// </summary>
internal sealed class Sandbox : IDisposable
{
    private const string CanaryText = "canary: this file must never change";

    private readonly string _base;

    public Sandbox()
    {
        _base = Path.Combine(Path.GetTempPath(), "savetether-tests", Guid.NewGuid().ToString("N"));
        Work = Path.Combine(_base, "work");
        Canary = Path.Combine(_base, "canary");
        Directory.CreateDirectory(Work);
        Directory.CreateDirectory(Path.Combine(Canary, "nested"));
        File.WriteAllText(CanaryFile, CanaryText);
        File.WriteAllText(Path.Combine(Canary, "nested", "keep2.txt"), CanaryText);
    }

    public string Work { get; }

    public string Canary { get; }

    public string CanaryFile => Path.Combine(Canary, "keep.txt");

    /// <summary>An absolute path below the work folder.</summary>
    public string At(params string[] parts) => Path.Combine([Work, .. parts]);

    /// <summary>Creates a folder below the work folder and returns its path.</summary>
    public string Dir(params string[] parts)
    {
        var path = At(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Creates a file (and its folders) below the work folder and returns its path.</summary>
    public string FileAt(string content, params string[] parts)
    {
        var path = At(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>The platform's default directory link: a junction on Windows, a symlink elsewhere.</summary>
    public static void Link(string link, string target) => new SafeFs().CreateDirectoryLink(link, target);

    /// <summary>
    /// Tries to create a symbolic link. On Windows this needs admin rights or Developer Mode;
    /// returns false when the platform refuses, so the caller can leave that case out.
    /// </summary>
    public static bool TrySymlink(string link, string target, bool directory)
    {
        try
        {
            if (directory)
            {
                Directory.CreateSymbolicLink(link, target);
            }
            else
            {
                File.CreateSymbolicLink(link, target);
            }

            return true;
        }
        catch (Exception e) when (OperatingSystem.IsWindows() && e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Fails the test if anything in the canary folder was changed, added or removed.</summary>
    public void AssertCanaryIntact()
    {
        Assert.Equal(CanaryText, File.ReadAllText(CanaryFile));
        Assert.Equal(CanaryText, File.ReadAllText(Path.Combine(Canary, "nested", "keep2.txt")));
        var entries = SafeFs.EnumerateTree(Canary).Select(e => e.RelativePath).ToArray();
        Assert.Equal(["keep.txt", "nested", "nested/keep2.txt"], entries);
    }

    public void Dispose()
    {
        try
        {
            new SafeFs().DeleteTree(_base);
        }
        catch (IOException)
        {
            // Leftovers in the temp folder must not fail a test that otherwise passed.
        }
    }
}
