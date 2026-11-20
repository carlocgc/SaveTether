using System.Collections.Concurrent;
using SaveTether.Core.Definitions;
using SaveTether.Core.Resolution;

namespace SaveTether.Core.Detection;

/// <summary>An app found on this device: the install that is here, its save folder, and when that folder last changed.</summary>
/// <param name="NewestWrite">The newest file write found a few levels inside the folder (UTC); null when it holds no file.</param>
/// <param name="Installed">Whether the install's detect rule matches. False only in a deep scan, for saves left by an app that is gone.</param>
public sealed record ScanHit(string AppId, string InstallId, string SaveFolder, DateTime? NewestWrite, bool Installed = true)
{
    /// <summary>Whether this hit is a better row for its app than <paramref name="other"/>: a detected install first, then the newer write.</summary>
    public bool IsBetterThan(ScanHit? other) =>
        other is null
        || (Installed != other.Installed ? Installed : (NewestWrite ?? DateTime.MinValue) > (other.NewestWrite ?? DateTime.MinValue));
}

/// <summary>What a scan looks for.</summary>
/// <param name="RoleClasses">Which role classes count; null means <see cref="RoleClass.Save"/> only.</param>
/// <param name="RequireInstall">True (the default) lists an app only when an install is detected; false lists any folder of a wanted role that exists.</param>
public sealed record ScanOptions(IReadOnlySet<RoleClass>? RoleClasses = null, bool RequireInstall = true)
{
    /// <summary>The automatic scan: installed apps, save folders.</summary>
    public static ScanOptions Default { get; } = new();

    /// <summary>The deep scan: any wanted folder that exists, installed or not.</summary>
    public static ScanOptions Deep(IReadOnlySet<RoleClass>? roleClasses = null) => new(roleClasses, RequireInstall: false);

    internal bool Wants(RoleClass roleClass) => RoleClasses is null ? roleClass == RoleClass.Save : RoleClasses.Contains(roleClass);
}

/// <summary>
/// A read-only look at every definition's installs on this device (phase 3.5, T4). By default an app is found
/// when one of its installs for this OS is detected by the app's own folder and a folder of a
/// <see cref="RoleClass.Save"/> role exists. Nothing is created, changed or followed through a link.
/// </summary>
public static class InstallScan
{
    /// <summary>How many apps are looked at at the same time.</summary>
    public const int Parallelism = 4;

    /// <summary>How many levels inside a save folder are looked at for the newest file.</summary>
    public const int MaxDepth = 3;

    /// <summary>The most entries looked at inside one save folder.</summary>
    public const int MaxEntries = 2000;

    /// <summary>
    /// Scans the definitions and yields each hit as it is found, in no fixed order. Stops quietly
    /// when <paramref name="cancel"/> is cancelled. Steam's library files are read once.
    /// </summary>
    /// <param name="valuesFor">The values set for this device per app (install folder, Wine prefix); none when null.</param>
    public static IEnumerable<ScanHit> Run(
        IEnumerable<AppDefinition> definitions,
        ResolverEnvironment environment,
        Func<string, DeviceValues>? valuesFor = null,
        CancellationToken cancel = default) =>
        Run(definitions, environment, valuesFor, ScanOptions.Default, cancel);

    /// <summary>As <see cref="Run(IEnumerable{AppDefinition}, ResolverEnvironment, Func{string, DeviceValues}?, CancellationToken)"/>, with the scan's options (for example <see cref="ScanOptions.Deep"/>).</summary>
    public static IEnumerable<ScanHit> Run(
        IEnumerable<AppDefinition> definitions,
        ResolverEnvironment environment,
        Func<string, DeviceValues>? valuesFor,
        ScanOptions options,
        CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        var resolver = new PathResolver(environment, cacheSteamLibraries: true);
        // Linked to the caller's token so that a consumer that stops early also stops the producer.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var token = stop.Token;
        var hits = new BlockingCollection<ScanHit>();
        var worker = Task.Run(
            () =>
            {
                try
                {
                    Parallel.ForEach(
                        definitions,
                        new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = token },
                        definition =>
                        {
                            // One hit per app: its newest-written install, chosen before it is published.
                            ScanHit? best = null;
                            foreach (var hit in Scan(definition, resolver, environment, valuesFor?.Invoke(definition.Id) ?? DeviceValues.None, options, token))
                            {
                                if (hit.IsBetterThan(best))
                                {
                                    best = hit;
                                }
                            }

                            if (best is not null)
                            {
                                hits.Add(best, CancellationToken.None);
                            }
                        });
                }
                catch (OperationCanceledException)
                {
                    // Cancelled: the hits so far are the answer.
                }
                finally
                {
                    hits.CompleteAdding();
                }
            },
            CancellationToken.None);

        try
        {
            foreach (var hit in hits.GetConsumingEnumerable(CancellationToken.None))
            {
                yield return hit;
            }
        }
        finally
        {
            // Also runs when the caller stops early: stop the producer and wait for it before the collection goes.
            stop.Cancel();
            worker.GetAwaiter().GetResult();
            hits.Dispose();
        }
    }

    /// <summary>The hits of one app: at most one per install.</summary>
    public static IEnumerable<ScanHit> Scan(
        AppDefinition definition,
        PathResolver resolver,
        ResolverEnvironment environment,
        DeviceValues values,
        CancellationToken cancel = default) =>
        Scan(definition, resolver, environment, values, ScanOptions.Default, cancel);

    /// <summary>As the overload without options, with the scan's options.</summary>
    public static IEnumerable<ScanHit> Scan(
        AppDefinition definition,
        PathResolver resolver,
        ResolverEnvironment environment,
        DeviceValues values,
        ScanOptions options,
        CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var install in definition.Installs.Where(i => i.Os.Contains(environment.Os)))
        {
            cancel.ThrowIfCancellationRequested();
            var installed = resolver.Detect(install, values).IsResolved;
            // An imported definition detects a game by its own save folder, which stays after the game is gone. When
            // the game has a Steam ID and this device has Steam, the game must be in a Steam library too.
            if (installed && definition.Tier == DefinitionTier.Imported && definition.StoreIds?.Steam is { } steamId
                && resolver.IsSteamInstalled(steamId) == false)
            {
                installed = false;
            }

            if (!installed && options.RequireInstall)
            {
                continue;
            }

            ScanHit? best = null;
            foreach (var role in definition.Roles.Where(r => options.Wants(r.Class)))
            {
                var folder = resolver.ResolveRole(install, role, values);
                if (!folder.IsResolved || !Directory.Exists(folder.Path))
                {
                    continue;
                }

                // A link anywhere from the install's own folder down to the save folder would show another place's files.
                var baseFolder = resolver.ResolveBase(install.Base, values);
                var linked = IsLinkedWithin(baseFolder.IsResolved ? baseFolder.Path : null, folder.Path!);
                var hit = new ScanHit(definition.Id, install.Id, folder.Path!, linked ? null : NewestWrite(folder.Path!, cancel), installed);
                if (hit.IsBetterThan(best))
                {
                    best = hit;
                }
            }

            if (best is not null)
            {
                yield return best;
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="folder"/>, or a folder above it down to <paramref name="top"/> (the install's own folder),
    /// is a link. Folders above <paramref name="top"/> are not looked at: a linked home or library drive is the owner's own layout.
    /// </summary>
    internal static bool IsLinkedWithin(string? top, string folder)
    {
        try
        {
            var current = new DirectoryInfo(Path.GetFullPath(folder));
            var stop = top is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(top));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var underTop = stop is not null && current.FullName.StartsWith(stop + Path.DirectorySeparatorChar, comparison);
            while (current is not null)
            {
                if (current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return true;
                }

                if (!underTop || string.Equals(Path.TrimEndingDirectorySeparator(current.FullName), stop, comparison))
                {
                    return false;
                }

                current = current.Parent;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }

        return false;
    }

    /// <summary>The newest file write within <see cref="MaxDepth"/> levels, looking at no more than <see cref="MaxEntries"/> entries.</summary>
    public static DateTime? NewestWrite(string folder, CancellationToken cancel = default)
    {
        DateTime? newest = null;
        var seen = 0;
        try
        {
            // A save folder that is itself a link would show its target's files: look at nothing.
            if (new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return null;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var level = new List<string> { folder };
        for (var depth = 0; depth < MaxDepth && level.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var directory in level)
            {
                // Lazy: the cap and cancellation are checked before the enumerator advances, so a huge folder is never read whole.
                try
                {
                    using var entries = new DirectoryInfo(directory).EnumerateFileSystemInfos().GetEnumerator();
                    while (true)
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (seen >= MaxEntries || !entries.MoveNext())
                        {
                            break;
                        }

                        seen++;
                        var entry = entries.Current;
                        if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            continue;
                        }

                        if (entry is DirectoryInfo)
                        {
                            next.Add(entry.FullName);
                        }
                        else if (newest is null || entry.LastWriteTimeUtc > newest)
                        {
                            newest = entry.LastWriteTimeUtc;
                        }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Unreadable folder: what was seen so far counts.
                }

                if (seen >= MaxEntries)
                {
                    return newest;
                }
            }

            level = next;
        }

        return newest;
    }
}
