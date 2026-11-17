using SaveTether.Core.Planning;

namespace SaveTether.Tests.Planning;

/// <summary>
/// Stands in for the flatpak command for one app. The app's own filesystem permissions are
/// <see cref="Base"/>; user overrides are kept in <see cref="Overrides"/> and merged after them, as
/// "flatpak info --show-permissions" does. "--nofilesystem=X" is recorded as "!X", as Flatpak does.
/// </summary>
internal sealed class FakeFlatpak(string appId, string? baseFilesystems) : IFlatpak
{
    /// <summary>The app's own "filesystems=" entries; null makes every read fail, as when flatpak is missing.</summary>
    public string? Base { get; set; } = baseFilesystems;

    public List<string> Overrides { get; } = [];

    /// <summary>Makes "flatpak override" fail.</summary>
    public bool OverrideFails { get; set; }

    /// <summary>Every "flatpak override" call, as its options joined by spaces.</summary>
    public List<string> OverrideCalls { get; } = [];

    public List<string> Asked { get; } = [];

    public string? ShowPermissions(string id)
    {
        Asked.Add(id);
        return id == appId && Base is { } value ? Context(string.Join(";", [value.TrimEnd(';'), .. Overrides])) : null;
    }

    public string? ShowUserOverrides(string id) =>
        id == appId && Base is not null ? (Overrides.Count == 0 ? string.Empty : Context(string.Join(";", Overrides))) : null;

    public bool OverrideForUser(string id, IReadOnlyList<string> options)
    {
        OverrideCalls.Add(string.Join(" ", options));
        if (OverrideFails || id != appId)
        {
            return false;
        }

        foreach (var option in options)
        {
            if (option.StartsWith("--filesystem=", StringComparison.Ordinal))
            {
                Overrides.Add(option["--filesystem=".Length..]);
            }
            else if (option.StartsWith("--nofilesystem=", StringComparison.Ordinal))
            {
                Overrides.Add("!" + option["--nofilesystem=".Length..]);
            }
        }

        return true;
    }

    private static string Context(string filesystems) => $"[Context]\nshared=network;\nfilesystems={filesystems};\n";
}
