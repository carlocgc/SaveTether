using SaveTether.Core.Planning;
using SaveTether.Gui.Presentation;

namespace SaveTether.Tests.Gui;

/// <summary>Section 8 of the desktop app spec: the process picker beside the hand form's "Process names".</summary>
public class ProcessChoicesTests
{
    private sealed class Listed(params string[] names) : IProcessLister
    {
        public IReadOnlyList<string> RunningProcessNames() => names;
    }

    [Fact]
    public void Linux_names_are_offered_once_each_without_kernel_threads_or_cut_off_repeats()
    {
        // As the check reads /proc: the short name, then the program file's name, per process.
        string[] running =
        [
            "plasmashell", "plasmashell",
            "dolphin-emu", "dolphin-emu",
            "PPSSPPSDL", "PPSSPPSDL",
            "kworker/0:1", "ksoftirqd/0",
            "steamwebhelper", "steamwebhelper",
            "SomeLongProgram", "SomeLongProgramName",
            "Game.exe", @"Z:\home\deck\Games\My Game\Game.exe",
            "  ", string.Empty,
        ];

        Assert.Equal(
            ["dolphin-emu", "Game.exe", "plasmashell", "PPSSPPSDL", "SomeLongProgramName", "steamwebhelper"],
            ProcessChoices.From(running, ignoreCase: false));
    }

    [Fact]
    public void Windows_names_differing_only_in_case_are_offered_once()
    {
        Assert.Equal(["Dolphin", "explorer"], ProcessChoices.From(["explorer", "Dolphin", "Explorer", "dolphin"], ignoreCase: true));
        Assert.NotEmpty(ProcessChoices.Running(new Listed("explorer")));
    }

    [Fact]
    public void Search_filters_and_a_picked_name_is_added_once()
    {
        IReadOnlyList<string> names = ["Dolphin", "dolphin-emu", "PPSSPPSDL"];
        Assert.Equal(["Dolphin", "dolphin-emu"], ProcessChoices.Filter(names, " DOLPH "));
        Assert.Equal(names, ProcessChoices.Filter(names, null));

        Assert.Equal("Dolphin", ProcessChoices.Add(null, "Dolphin"));
        Assert.Equal("Dolphin, dolphin-emu", ProcessChoices.Add("Dolphin", "dolphin-emu"));
        Assert.Equal("Dolphin, dolphin-emu", ProcessChoices.Add("Dolphin, dolphin-emu", "DOLPHIN"));
    }
}
