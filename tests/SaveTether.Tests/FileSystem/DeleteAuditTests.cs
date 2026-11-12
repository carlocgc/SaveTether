using System.Text.RegularExpressions;

namespace SaveTether.Tests.FileSystem;

/// <summary>
/// Keeps design 5.3, invariant 1 true: every call that can delete, move or create a link lives
/// in src/SaveTether.Core/FileSystem/, where each one is preceded by a look at the entry.
/// </summary>
public partial class DeleteAuditTests
{
    private static readonly string AllowedFolder = Path.Combine("src", "SaveTether.Core", "FileSystem");

    [GeneratedRegex(
        @"\b(Directory|File)\s*\.\s*(Delete|Move|Replace|CreateSymbolicLink)\b" +
        @"|\.\s*(Delete|MoveTo|CreateAsSymbolicLink)\s*\(" +
        @"|\bFileSystem\s*\.\s*(DeleteDirectory|DeleteFile|MoveDirectory|MoveFile)\b" +
        @"|\b(RemoveDirectory|DeleteFileW?|MoveFileExW?|unlink|rmdir)\s*\(")]
    private static partial Regex BannedCall();

    [Fact]
    public void Destructive_filesystem_calls_appear_only_in_the_safe_filesystem_layer()
    {
        var repo = FindRepositoryRoot();
        var sources = Directory.EnumerateFiles(Path.Combine(repo, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .ToList();
        Assert.NotEmpty(sources);

        var offences = new List<string>();
        foreach (var file in sources)
        {
            var relative = Path.GetRelativePath(repo, file);
            if (relative.StartsWith(AllowedFolder + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (BannedCall().IsMatch(lines[i]))
                {
                    offences.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(
            offences.Count == 0,
            "Deleting, moving and link creation must go through SafeFs. Found:\n" + string.Join("\n", offences));
    }

    [Theory]
    [InlineData("Directory.Delete(path, true);")]
    [InlineData("File.Delete(path);")]
    [InlineData("Directory.Move(a, b);")]
    [InlineData("File.Move(a, b);")]
    [InlineData("File.Replace(a, b, null);")]
    [InlineData("Directory.CreateSymbolicLink(a, b);")]
    [InlineData("new DirectoryInfo(path).Delete(true);")]
    [InlineData("info.MoveTo(other);")]
    [InlineData("info.CreateAsSymbolicLink(target);")]
    public void The_audit_pattern_catches_destructive_calls(string line)
    {
        Assert.Matches(BannedCall(), line);
    }

    [Theory]
    [InlineData("var deleted = items.Where(i => i.IsDeleted);")]
    [InlineData("fs.DeleteTree(path);")]
    [InlineData("fs.RemoveLink(path);")]
    [InlineData("File.ReadAllText(path);")]
    public void The_audit_pattern_allows_safe_calls(string line)
    {
        Assert.DoesNotMatch(BannedCall(), line);
    }

    private static bool IsBuildOutput(string file)
    {
        var separator = Path.DirectorySeparatorChar;
        return file.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
            || file.Contains($"{separator}bin{separator}", StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SaveTether.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find SaveTether.slnx above the test folder.");
    }
}
