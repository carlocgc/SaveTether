using SaveTether.Core.Planning;

namespace SaveTether.Gui.Presentation;

/// <summary>Reading and planning in the window never asks which programs are running: only the operations that change a folder do.</summary>
internal sealed class NoProcesses : IProcessLister
{
    public static readonly NoProcesses Instance = new();

    public IReadOnlyList<string> RunningProcessNames() => [];
}
