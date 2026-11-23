using SaveTether.Core.Startup;

namespace SaveTether.Tests.Cli;

/// <summary>Processes that "uninstall" sees running the installed app: none unless a test starts one.</summary>
public sealed class RunningProgramsStub : IRunningPrograms
{
    private readonly object _gate = new();
    private readonly List<int> _running = [];

    public void Start(int id)
    {
        lock (_gate)
        {
            _running.Add(id);
        }
    }

    public void StopAll()
    {
        lock (_gate)
        {
            _running.Clear();
        }
    }

    public IReadOnlyList<int> Running(string program)
    {
        lock (_gate)
        {
            return [.. _running];
        }
    }
}
