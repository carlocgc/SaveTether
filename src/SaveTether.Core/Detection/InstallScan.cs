using System.Collections.Concurrent;
using SaveTether.Core.Definitions;
using SaveTether.Core.Resolution;

namespace SaveTether.Core.Detection;

/// <summary>An app found on this device: the install that is here, its save folder, and when that folder last changed.</summary>
/// <param name="NewestWrite">The newest file write found a few levels inside the folder (UTC); null when it holds no file.</param>
public sealed record ScanHit(string AppId, string InstallId, string SaveFolder, DateTime? NewestWrite);

/// <summary>
/// A read-only look at every definition's installs on this device (phase 3.5, T4). An app is found
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
        CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(definitions);
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
                            foreach (var hit in Scan(definition, resolver, environment, valuesFor?.Invoke(definition.Id) ?? DeviceValues.None, token))
                            {
                                if (best is null || (hit.NewestWrite ?? DateTime.MinValue) > (best.NewestWrite ?? DateTime.MinValue))
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
        CancellationToken cancel = default)
    {
        foreach (var install in definition.Installs.Where(i => i.Os.Contains(environment.Os)))
        {
            cancel.ThrowIfCancellationRequested();
            if (!resolver.Detect(install, values).IsResolved)
            {
                continue;
            }

            ScanHit? best = null;
            foreach (var role in definition.Roles.Where(r => r.Class == RoleClass.Save))
            {
                var folder = resolver.ResolveRole(install, role, values);
                if (!folder.IsResolved || !Directory.Exists(folder.Path))
                {
                    continue;
                }

                var hit = new ScanHit(definition.Id, install.Id, folder.Path!, NewestWrite(folder.Path!, cancel));
                if (best is null || (hit.NewestWrite ?? DateTime.MinValue) > (best.NewestWrite ?? DateTime.MinValue))
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
