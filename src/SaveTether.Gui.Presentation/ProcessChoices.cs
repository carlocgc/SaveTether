using SaveTether.Core.Planning;

namespace SaveTether.Gui.Presentation;

/// <summary>
/// The process picker beside the hand form's "Process names" (desktop-app-spec.md, section 8): the names of what is
/// running, read by the same code as the running-app check, so a name picked here is one the check finds.
/// </summary>
public static class ProcessChoices
{
    /// <summary>Linux keeps only the first 15 characters of a process name in /proc/&lt;pid&gt;/comm.</summary>
    private const int LinuxCommLength = 15;

    /// <summary>The running processes' names, each once, sorted, without the repeats the check's sources give.</summary>
    public static IReadOnlyList<string> Running(IProcessLister lister)
    {
        ArgumentNullException.ThrowIfNull(lister);
        return From(lister.RunningProcessNames(), ignoreCase: OperatingSystem.IsWindows());
    }

    /// <summary>
    /// The names to offer, from what the check reads. On Linux each process gives its short name (cut to 15 characters)
    /// and its program file's name, and a Windows program under Wine or Proton gives a Windows path as that file name:
    /// those are reduced to one name each. Names with a '/' are kernel threads ("kworker/0:1") and are left out.
    /// </summary>
    public static IReadOnlyList<string> From(IEnumerable<string> running, bool ignoreCase)
    {
        ArgumentNullException.ThrowIfNull(running);
        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var names = new HashSet<string>(comparer);
        foreach (var raw in running)
        {
            var name = raw?.Trim() ?? string.Empty;
            if (name.Contains('/', StringComparison.Ordinal))
            {
                continue;
            }

            name = name[(name.LastIndexOf('\\') + 1)..].Trim();
            if (name.Length > 0)
            {
                names.Add(name);
            }
        }

        // A short name that is the cut-off start of a longer one is the same process.
        names.RemoveWhere(n => n.Length == LinuxCommLength
            && names.Any(other => other.Length > LinuxCommLength && other.StartsWith(n, comparison)));
        return names.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The picker's search: the names containing the text, ignoring case.</summary>
    public static IReadOnlyList<string> Filter(IReadOnlyList<string> names, string? text)
    {
        ArgumentNullException.ThrowIfNull(names);
        var typed = text?.Trim() ?? string.Empty;
        return typed.Length == 0 ? names : names.Where(n => n.Contains(typed, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>The field with a picked name added after those already there, unless it is there already.</summary>
    public static string Add(string? field, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var names = ManualAddFlow.SplitProcessNames(field).ToList();
        if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            names.Add(name);
        }

        return string.Join(", ", names);
    }
}
