using System.Net.Sockets;
using System.Text;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Storage;
using SaveTether.Gui.Presentation;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Gui;

/// <summary>Section 2 of the desktop app spec: one copy at a time, and the log.</summary>
public class ProcessAndLogTests
{
    [Fact]
    public void Only_one_copy_holds_the_claim_and_a_second_asks_it_to_show_the_window()
    {
        using var sandbox = new Sandbox();
        var folder = sandbox.Dir("local");
        using var shown = new SemaphoreSlim(0);

        using (var first = SingleInstance.TryClaim(folder, () => shown.Release()))
        {
            Assert.NotNull(first);
            Assert.Null(SingleInstance.TryClaim(folder, () => { }));

            Assert.True(SingleInstance.SignalExisting(folder));
            Assert.True(shown.Wait(TimeSpan.FromSeconds(10)), "the first copy was not asked to show its window");
        }

        // Released: nobody answers, and the next copy can claim it.
        Assert.False(SingleInstance.SignalExisting(folder));
        using var next = SingleInstance.TryClaim(folder, () => { });
        Assert.NotNull(next);
    }

    [Fact]
    public void A_socket_left_by_a_killed_copy_is_cleared_but_nothing_else_is()
    {
        using var sandbox = new Sandbox();
        var folder = sandbox.Dir("local");
        var socketPath = SingleInstance.SocketPath(folder);

        // A copy killed while listening leaves its socket's file behind. .NET removes the file when a socket
        // is closed, so the stale one is kept open here to stand in for it.
        var killed = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            killed.Bind(new UnixDomainSocketEndPoint(socketPath));
            Assert.NotEqual(EntryKind.Missing, SafeFs.Probe(socketPath));
            using (var claimed = SingleInstance.TryClaim(folder, () => { }))
            {
                Assert.NotNull(claimed);
            }
        }
        finally
        {
            killed.Dispose();
        }

        Assert.Equal(EntryKind.Missing, SafeFs.Probe(socketPath));

        // Something with content where the socket goes is never removed.
        File.WriteAllText(socketPath, "not a socket");
        Assert.Throws<SafeFsException>(() => SingleInstance.TryClaim(folder, () => { }));
        Assert.Equal("not a socket", File.ReadAllText(socketPath));
        Assert.Throws<SafeFsException>(() => new SafeFs().DeleteSocket(sandbox.Dir("a-folder")));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void A_local_folder_with_a_long_path_puts_the_socket_elsewhere()
    {
        var longFolder = Path.Combine(Path.GetTempPath(), new string('x', 120));
        var path = SingleInstance.SocketPath(longFolder);
        Assert.True(path.Length <= 108, path);
        Assert.Equal(path, SingleInstance.SocketPath(longFolder));
        Assert.NotEqual(path, SingleInstance.SocketPath(longFolder + "y"));
    }

    [Fact]
    public void Another_process_holding_the_operation_lock_is_found_without_taking_it()
    {
        using var sandbox = new Sandbox();
        var local = new LocalStore(sandbox.Dir("local"));
        Assert.Null(OperationLock.HeldByAnotherProcess(local));

        // This process's own lock is not another's.
        using (OperationLock.Acquire(local, "gui", "apply"))
        {
            Assert.Null(OperationLock.HeldByAnotherProcess(local));
        }

        // A holder file naming a running process that started before it was written.
        var other = new LockHolder
        {
            Schema = LockHolder.CurrentSchema, ProcessId = OtherRunningProcess(), FrontEnd = "cli", Command = "apply",
            StartedUtc = DateTimeOffset.UtcNow,
        };
        WriteHolder(local, other);
        Assert.Equal(other.ProcessId, OperationLock.HeldByAnotherProcess(local)?.ProcessId);

        // The same process ID written before that process started: a reused ID, so not the holder.
        WriteHolder(local, other with { StartedUtc = DateTimeOffset.UnixEpoch });
        Assert.Null(OperationLock.HeldByAnotherProcess(local));
    }

    [Fact]
    public void The_log_keeps_about_the_last_megabyte_from_the_start_of_a_line()
    {
        using var sandbox = new Sandbox();
        var log = new GuiLog(sandbox.At("local"));
        var line = new string('a', 1000);
        for (var i = 0; i < 1100; i++)
        {
            log.Write($"{i} {line}");
        }

        Assert.NotEqual(0xEF, File.ReadAllBytes(log.Path)[0]);
        var text = File.ReadAllText(log.Path, Encoding.UTF8);
        Assert.True(text.Length <= GuiLog.MaxBytes, text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("1099 ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n0 ", "\n" + text, StringComparison.Ordinal);
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\dZ \d+ a+$", text.Split('\n')[0]);
    }

    private static void WriteHolder(LocalStore local, LockHolder holder) =>
        File.WriteAllText(
            Path.Combine(local.Folder, OperationLock.HolderFileName),
            $$"""{"schema": 1, "process_id": {{holder.ProcessId}}, "front_end": "{{holder.FrontEnd}}", "command": "{{holder.Command}}", "started_utc": "{{holder.StartedUtc:O}}"}""");

    /// <summary>
    /// A long-running process other than this one: the oldest one this user can see. It must answer both
    /// questions the core asks, its start time and whether it has exited: a protected Windows process such as
    /// audiodg gives its start time but refuses HasExited, and the core then counts it as the holder.
    /// </summary>
    private static int OtherRunningProcess()
    {
        var candidates = System.Diagnostics.Process.GetProcesses()
            .Where(p => p.Id != Environment.ProcessId)
            .Select(p =>
            {
                try
                {
                    return (p.Id, Start: p.HasExited ? null : (DateTime?)p.StartTime);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    return (p.Id, Start: null);
                }
                finally
                {
                    p.Dispose();
                }
            })
            .Where(p => p.Start is not null)
            .OrderBy(p => p.Start)
            .ToList();
        return candidates[0].Id;
    }
}
