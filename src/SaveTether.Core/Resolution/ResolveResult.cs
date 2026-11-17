namespace SaveTether.Core.Resolution;

/// <summary>Why a path could not be resolved on this device.</summary>
public enum ResolveProblem
{
    /// <summary>The text is not a valid path template: an unknown token, a bad argument, a ".." part.</summary>
    Invalid,

    /// <summary>The token or install does not exist on this operating system, such as {flatpak:...} on Windows.</summary>
    NotOnThisOs,

    /// <summary>The path needs a value set for this device, such as {install_dir}, and none is set.</summary>
    NeedsValue,

    /// <summary>Something the token is found from is not there: no SD card mounted, no EmuDeck settings file.</summary>
    Missing,

    /// <summary>More than one candidate was found and the resolver will not guess between them.</summary>
    Ambiguous,

    /// <summary>A form the format allows that this build cannot resolve yet.</summary>
    NotYetSupported,
}

/// <summary>An absolute path, or the reason there is none. Never both.</summary>
public sealed record ResolveResult
{
    private ResolveResult(string? path, ResolveProblem? problem, string? message)
    {
        Path = path;
        Problem = problem;
        Message = message;
    }

    /// <summary>The absolute path on this device, in its native form. Null when unresolved.</summary>
    public string? Path { get; }

    public ResolveProblem? Problem { get; }

    /// <summary>What went wrong, written for the owner. Null when resolved.</summary>
    public string? Message { get; }

    public bool IsResolved => Path is not null;

    public static ResolveResult Ok(string path) => new(path, null, null);

    public static ResolveResult Fail(ResolveProblem problem, string message) => new(null, problem, message);

    public override string ToString() => IsResolved ? Path! : $"{Problem}: {Message}";
}
