using System.Text;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;
using SaveTether.Core.Operations;
using SaveTether.Core.Storage;

namespace SaveTether.Gui.Presentation;

/// <summary>The three steps of first-run setup (desktop-app-spec.md, section 6).</summary>
public enum SetupStep
{
    SyncFolder,
    DeviceName,
    Backups,
}

/// <summary>What step 1 found at the folder given (section 6.1's table).</summary>
public enum SyncFolderFinding
{
    /// <summary>The path is empty or not absolute.</summary>
    NotAPath,

    /// <summary>Nothing is there. On a drive or share that is not connected, say.</summary>
    Missing,

    /// <summary>The folder is there without SaveTether's folder: only the first device creates it.</summary>
    NotSetUp,

    /// <summary>SaveTether's folder is there, with the devices already set up.</summary>
    AlreadySetUp,
}

/// <summary>Step 1's result, with the sentence the page shows.</summary>
public sealed record SyncFolderCheck(SyncFolderFinding Finding, string Path, IReadOnlyList<string> Devices, string Sentence)
{
    /// <summary>True when Next may go on without more from the owner.</summary>
    public bool CanContinue => Finding == SyncFolderFinding.AlreadySetUp;

    /// <summary>True when the only way on is "This is my first device".</summary>
    public bool OffersFirstDevice => Finding == SyncFolderFinding.NotSetUp;
}

/// <summary>A device already set up in the sync folder, as step 2 lists it.</summary>
/// <param name="BackupRoot">Where that device keeps its backups, from its file.</param>
public sealed record ExistingDevice(string Name, string BackupRoot);

/// <summary>How setup ended: done, or refused at a step, with the core's reason. Nothing is written when refused.</summary>
public sealed record SetupOutcome(InitResult? Result, SetupStep? FailedAt, string? Error)
{
    public bool Succeeded => Result is not null;
}

/// <summary>
/// First-run setup as the window drives it: each step checks what the owner gave, and nothing is written
/// until <see cref="Finish"/>, which makes the one <see cref="Workspace.Init"/> call (section 6), under the
/// operation lock, since a CLI may run at the same moment.
/// </summary>
public sealed class SetupFlow(LocalStore local, string defaultBackupRoot, string hostName)
{
    public const string NameRule = "Lowercase letters, digits, '-' and '_', starting with a letter or digit; up to 40 characters.";

    public const string FirstDeviceWarning =
        "Only do this on the first device. If SaveTether is set up on another device, wait until the sync tool has delivered " +
        "the SaveTether folder here instead; setting it up twice creates two separate setups.";

    private SyncFolderCheck? _syncFolder;

    /// <summary>The checked sync folder, once step 1 has passed.</summary>
    public SyncFolderCheck? SyncFolder => _syncFolder is { } check && (check.CanContinue || CreateRoot) ? check : null;

    /// <summary>True when the owner confirmed this is the first device, so <see cref="Finish"/> creates SaveTether's folder.</summary>
    public bool CreateRoot { get; private set; }

    /// <summary>The name chosen in step 2, and whether it takes over an existing device's file.</summary>
    public string? DeviceName { get; private set; }

    public bool TakeOver { get; private set; }

    /// <summary>The backup location step 3 starts with: the device file's when taking over, otherwise the default.</summary>
    public string SuggestedBackupRoot =>
        TakeOver && ExistingDevices().FirstOrDefault(d => d.Name == DeviceName) is { } device ? device.BackupRoot : defaultBackupRoot;

    /// <summary>
    /// A sentence for a backup location that does not exist and will not be created, as the core treats one
    /// taken from a device file: its drive may not be connected. Null otherwise.
    /// </summary>
    public string? BackupRootNote(string backupRoot) =>
        TakeOver && !PathRules.AreSame(backupRoot, defaultBackupRoot) && Path.IsPathFullyQualified(backupRoot) && !Directory.Exists(backupRoot)
            ? $"'{backupRoot}' does not exist. Its drive may not be connected; connect it, or choose another location."
            : null;

    /// <summary>Step 1: what is at the folder. Reads only.</summary>
    public SyncFolderCheck CheckSyncFolder(string typed)
    {
        CreateRoot = false;
        DeviceName = null;
        TakeOver = false;
        var path = ExpandHome(typed?.Trim() ?? string.Empty);
        if (path.Length == 0 || !Path.IsPathFullyQualified(path))
        {
            return _syncFolder = new SyncFolderCheck(SyncFolderFinding.NotAPath, path, [], "Give the full path of the sync folder, or choose it.");
        }

        path = PathRules.Normalize(Path.GetFullPath(path));
        if (SafeFs.Probe(PathRules.ResolveAll(path)) != EntryKind.Directory)
        {
            return _syncFolder = new SyncFolderCheck(
                SyncFolderFinding.Missing, path, [], $"{path} does not exist. If it is on a drive or a network share, connect it.");
        }

        var shared = new SharedStore(path);
        if (!shared.IsInitialized)
        {
            return _syncFolder = new SyncFolderCheck(SyncFolderFinding.NotSetUp, path, [], "This sync folder is not set up for SaveTether yet.");
        }

        var devices = Workspace.ExistingDevices(path);
        var on = devices.Count == 0 ? "no device yet" : string.Join(", ", devices);
        return _syncFolder = new SyncFolderCheck(SyncFolderFinding.AlreadySetUp, path, devices, $"This sync folder is already set up for SaveTether, on: {on}.");
    }

    /// <summary>Step 1, after the "first device" confirmation.</summary>
    public void ConfirmFirstDevice()
    {
        if (_syncFolder is not { OffersFirstDevice: true })
        {
            throw new InvalidOperationException("Only a sync folder without SaveTether's folder can be set up as the first device.");
        }

        CreateRoot = true;
    }

    /// <summary>Step 2's list: the devices set up in the sync folder, with their backup locations.</summary>
    public IReadOnlyList<ExistingDevice> ExistingDevices()
    {
        if (SyncFolder is not { Finding: SyncFolderFinding.AlreadySetUp } check)
        {
            return [];
        }

        var shared = new SharedStore(check.Path);
        return check.Devices
            .Select(name => shared.TryReadDevice(name) is { } file ? new ExistingDevice(name, file.BackupRoot) : null)
            .OfType<ExistingDevice>()
            .ToList();
    }

    /// <summary>A name from the machine's host name, lowercased, with characters outside a-z, 0-9, '-' and '_' dropped.</summary>
    public string SuggestedName
    {
        get
        {
            var kept = new StringBuilder();
            foreach (var c in hostName.ToLowerInvariant())
            {
                if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_')
                {
                    kept.Append(c);
                }
            }

            var name = kept.ToString().TrimStart('-', '_');
            name = name.Length > 40 ? name[..40] : name;
            return Identifiers.IsValidDeviceName(name) ? name : string.Empty;
        }
    }

    /// <summary>What is wrong with a new name as typed, or null when it can be used.</summary>
    public string? NameProblem(string name)
    {
        if (!Identifiers.IsValidDeviceName(name))
        {
            return string.IsNullOrEmpty(name) ? "Give this device a name." : $"That name cannot be used. {NameRule}";
        }

        return ExistingDevices().Any(d => d.Name == name)
            ? $"A device named '{name}' is already set up. Take it over above if this is that device, or choose another name."
            : null;
    }

    /// <summary>Step 2 with a new name.</summary>
    public void ChooseName(string name)
    {
        RequireSyncFolder();
        if (NameProblem(name) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        DeviceName = name;
        TakeOver = false;
    }

    /// <summary>Step 2, taking over a listed device, after its confirmation.</summary>
    public void ChooseTakeOver(string name)
    {
        RequireSyncFolder();
        if (ExistingDevices().All(d => d.Name != name))
        {
            throw new InvalidOperationException($"No device named '{name}' is set up in this sync folder.");
        }

        DeviceName = name;
        TakeOver = true;
    }

    /// <summary>The take-over confirmation (5.6).</summary>
    public static string TakeOverWarning(string name) =>
        $"Only take over '{name}' if this is that device, set up again. Two machines with one name would apply each other's folders.";

    /// <summary>
    /// Step 3's Set up: the one <see cref="Workspace.Init"/> call, under the operation lock. A refusal says
    /// which step it belongs to, and nothing has been written.
    /// </summary>
    public SetupOutcome Finish(string backupRoot)
    {
        if (SyncFolder is not { } folder || DeviceName is not { } name)
        {
            throw new InvalidOperationException("Setup is finished only after the sync folder and the device name.");
        }

        var root = ExpandHome(backupRoot.Trim());
        if (root.Length == 0 || !Path.IsPathFullyQualified(root))
        {
            return new SetupOutcome(null, SetupStep.Backups, "Give the full path of the backup location, or choose it.");
        }

        // Unchanged, the location is left to the core, as the CLI does without --backup-root: then a taken-over
        // device's location that is not there is not created, since its drive may only be disconnected.
        root = PathRules.Normalize(Path.GetFullPath(root));
        var given = PathRules.AreSame(root, SuggestedBackupRoot) ? null : root;
        try
        {
            using var held = OperationLock.Acquire(local, "gui", "setup");
            var result = Workspace.Init(local, new InitRequest(folder.Path, name, CreateRoot, TakeOver, given, defaultBackupRoot));
            return new SetupOutcome(result, null, null);
        }
        catch (StoreException e)
        {
            var step = e.Problem switch
            {
                StoreProblem.NotInitialized or StoreProblem.MappingsApplied => SetupStep.SyncFolder,
                StoreProblem.NameInUse => SetupStep.DeviceName,
                _ => SetupStep.Backups,
            };
            return new SetupOutcome(null, step, e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new SetupOutcome(null, SetupStep.Backups, e.Message);
        }
    }

    private void RequireSyncFolder()
    {
        if (SyncFolder is null)
        {
            throw new InvalidOperationException("The device name comes after a sync folder that is set up, or confirmed as the first device's.");
        }
    }

    /// <summary>A leading <c>~</c> as the home folder, as in a terminal, for a typed path on Linux.</summary>
    private static string ExpandHome(string path) =>
        !OperatingSystem.IsWindows() && (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..]
            : path;
}
