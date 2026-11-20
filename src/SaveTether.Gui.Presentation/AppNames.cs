using SaveTether.Core.Definitions;

namespace SaveTether.Gui.Presentation;

/// <summary>The name an app is shown under on the Apps and Backups pages.</summary>
public static class AppNames
{
    /// <summary>
    /// The definition's own name, so "Atelier Umia" and not its id. The shared and built-in definitions are tried first
    /// and are all the status check read before; an app added from the imported set has no other definition, so
    /// only when those find nothing is the imported set read (once per run, off the UI thread). <c>Defined</c> is true
    /// only for the first, as before: it says whether the app has a definition of its own to open again.
    /// </summary>
    public static (string Name, bool Defined) Resolve(DefinitionCatalog catalog, string appId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        try
        {
            if (catalog.Find(appId, includeImported: false)?.Definition is { } own)
            {
                return (own.Name, true);
            }

            return (catalog.Find(appId, includeImported: true)?.Definition?.Name ?? appId, false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (appId, false);
        }
    }
}
