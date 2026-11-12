namespace SaveTether.Tests.FileSystem;

/// <summary>
/// Builds a tree holding every kind of link SaveTether must never follow, each one pointing
/// at the sandbox's canary (or at the tree itself, for the cycle).
/// </summary>
internal static class LinkyTree
{
    /// <summary>Names of the links at the top of the tree that exist on every platform.</summary>
    public static readonly string[] DefaultLinks = ["link-to-canary", "broken", "loop"];

    public static string Build(Sandbox sandbox, string name = "tree")
    {
        var tree = sandbox.Dir(name);
        sandbox.FileAt("plain", name, "file.txt");
        sandbox.FileAt("deep", name, "sub", "a", "b", "c", "deep.txt");

        // A directory link at the top, and one nested several levels down.
        Sandbox.Link(Path.Combine(tree, "link-to-canary"), sandbox.Canary);
        Sandbox.Link(sandbox.At(name, "sub", "a", "b", "c", "deep-link"), sandbox.Canary);

        // A broken link: its target existed when it was made and is then removed.
        var gone = sandbox.Dir(name + "-gone");
        Sandbox.Link(Path.Combine(tree, "broken"), gone);
        Directory.Delete(gone);

        // A cycle: a link inside the tree pointing back at the tree.
        Sandbox.Link(Path.Combine(tree, "loop"), tree);

        // Symbolic links, where the platform lets this user create them.
        Sandbox.TrySymlink(Path.Combine(tree, "sym-dir"), sandbox.Canary, directory: true);
        Sandbox.TrySymlink(Path.Combine(tree, "sym-file"), sandbox.CanaryFile, directory: false);

        var readOnly = sandbox.FileAt("locked", name, "readonly.txt");
        File.SetAttributes(readOnly, File.GetAttributes(readOnly) | FileAttributes.ReadOnly);

        return tree;
    }
}
