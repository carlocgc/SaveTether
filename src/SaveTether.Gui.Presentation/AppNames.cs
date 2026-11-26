using SaveTether.Core.Definitions;
using SaveTether.Core.Operations;
using SaveTether.Core.Storage;

namespace SaveTether.Gui.Presentation;

/// <summary>The name an app is shown under on the Apps and Backups pages.</summary>
public static class AppNames
{
    /// <summary>
    /// The definition's own name, so "Atelier Umia" and not its id. The shared and built-in definitions are tried first
    /// and are all the status check read before; an app added from the imported set has no other definition, so
    /// only when those find nothing is the imported set read (once per run, off the UI thread). <c>Defined</c> says
    /// the app has a definition Add app can open, so a row can offer "Add" or "Find the app again" with the app's own
    /// folders and roles, not the form for a folder by hand. An imported definition counts only when it has the
    /// mapping's role (<paramref name="roleId"/>) and, when <paramref name="mappingTarget"/> is given, the same shared
    /// target: a mapping made by hand under the same app name is not one of its roles, and Add app would refuse a
    /// target that differs where the form for a folder by hand would reuse the mapping's own. With no definition, an app
    /// added by hand is shown under the name typed for it (<paramref name="storedName"/>, from <see cref="Stored"/>).
    /// </summary>
    public static (string Name, bool Defined) Resolve(
        DefinitionCatalog catalog, string appId, string? roleId = null, Func<string?>? mappingTarget = null, string? storedName = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        try
        {
            if (catalog.Find(appId, includeImported: false)?.Definition is { } own)
            {
                return (own.Name, true);
            }

            return catalog.Find(appId, includeImported: true)?.Definition is { } imported
                ? (imported.Name, roleId is null || imported.Roles.Any(r => r.Id == roleId && (mappingTarget is null || r.MappingTargetFor(appId) == mappingTarget())))
                : (storedName ?? appId, false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (storedName ?? appId, false);
        }
    }

    /// <summary>
    /// The names typed for apps added by hand, by app ID, read once from the shared mappings: the first mapping of the
    /// app that has one, in ID order. Empty when the mappings cannot be read; the apps are then shown by their IDs.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Stored(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        try
        {
            return workspace.Shared.ListMappings()
                .Where(m => m.Name is not null)
                .OrderBy(m => m.Id, StringComparer.Ordinal)
                .GroupBy(m => m.Id.Split('.', 2)[0], StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Name!, StringComparer.Ordinal);
        }
        catch (Exception e) when (e is StoreException or IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>();
        }
    }
}
