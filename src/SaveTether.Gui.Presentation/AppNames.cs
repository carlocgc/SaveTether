using SaveTether.Core.Definitions;

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
    /// target that differs where the form for a folder by hand would reuse the mapping's own.
    /// </summary>
    public static (string Name, bool Defined) Resolve(DefinitionCatalog catalog, string appId, string? roleId = null, Func<string?>? mappingTarget = null)
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
                : (appId, false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (appId, false);
        }
    }
}
