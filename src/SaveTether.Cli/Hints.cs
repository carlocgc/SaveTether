using System.Text.Json;
using System.Text.Json.Serialization;
using SaveTether.Core.Model;
using SaveTether.Core.Storage;

namespace SaveTether.Cli;

/// <summary>
/// The command line's words for the core's next steps: the command or flags to type. The core says
/// what is wrong and what can be done next without naming any command (desktop-app-spec.md,
/// section 13), and the desktop app words the same steps as buttons.
/// </summary>
internal static class Hints
{
    /// <summary>The note's text followed by the command line's hint for each next step.</summary>
    public static string Compose(Note note)
    {
        ArgumentNullException.ThrowIfNull(note);
        return note.Next.Count == 0 ? note.Text : string.Join(" ", [note.Text, .. note.Next.Select(For)]);
    }

    public static IReadOnlyList<string> Compose(IReadOnlyList<Note> notes) => [.. notes.Select(Compose)];

    public static string? ComposeOrNull(Note? note) => note is null ? null : Compose(note);

    /// <summary>
    /// An exception's message for the owner. A store refusal carries next steps, worded here; any
    /// other exception's message is shown as it is.
    /// </summary>
    public static string ErrorText(Exception e) =>
        e is StoreException store ? Compose(new Note(store.Message, store.Next)) : e.Message;

    /// <summary>One sentence: what to type for this step.</summary>
    public static string For(NextStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var id = step.MappingId ?? "<id>";
        return step.Kind switch
        {
            NextStepKind.Recover => "Run 'savetether recover'.",
            NextStepKind.Revert => $"Run 'savetether revert {id}' first.",
            NextStepKind.Apply => $"Run 'savetether apply {id}'.",
            NextStepKind.SetSaveFolder => $"Set one with 'savetether map add {id} --source <path>'.",
            NextStepKind.AddMapping => $"Add it with 'savetether map add {id} --target <app>/<save folder> --source <path>'.",
            NextStepKind.GiveTarget => $"Give it with --target <app>/<save folder>, for example --target {AppOf(id)}/saves.",
            NextStepKind.RemoveMapping => $"Use '{id}', or remove it with 'savetether map remove {id}' (after 'savetether revert {id}' if it is applied).",
            NextStepKind.ChangeEntryDepth => $"Change it with 'savetether map add {id} --source <path> --entry-depth <n>'.",
            NextStepKind.SetUp => "Run 'savetether init --sync-root <path>' first.",
            NextStepKind.SetUpAgain => "Run 'savetether init' again.",
            NextStepKind.CreateRoot => "On the first device, add --create-root.",
            NextStepKind.ChooseBackupLocation => "Or run init again with --backup-root <path>.",
            NextStepKind.TakeOverName => "If this machine should have the name, run 'savetether init' again with --take-over; otherwise choose another name.",
            NextStepKind.ChooseInstall => "Choose one with --install <id>.",
            NextStepKind.NameRoles => "Name the roles with --role <id>.",
            NextStepKind.GiveLocation => "Give it with --install-dir, --sd-card or --wine-prefix.",
            NextStepKind.ChooseLocation => "Give the one to use with --sd-card or --wine-prefix.",
            NextStepKind.GiveNewLocation => "Give the new location with --install-dir, --sd-card or --wine-prefix.",
            NextStepKind.FindAppAgain => $"Run 'savetether app add' with its new location, or 'savetether apply {id}'.",
            NextStepKind.ChooseSetup => ChooseSetup(step.Choices ?? []),
            NextStepKind.UseAllowedChoice => $"Use {OneOf(step.Choices ?? [])}.",
            NextStepKind.ApplyWithoutChoice => "Run apply without a choice to just link.",
            NextStepKind.ConfirmReplace => "Add --confirm-replace to go ahead.",
            NextStepKind.ReseedFromHere => $"Then run 'savetether apply {id} --copy --assume-synced' to make them the shared copy again.",
            NextStepKind.MakeThisCopyWin => $"On the device that should win, run 'savetether apply {id} --replace-shared', or restore a backup.",
            NextStepKind.GrantSandbox => "Or add --grant-sandbox to let SaveTether allow it (only that folder, for this user).",
            NextStepKind.ListBackups => "Run 'savetether backup list' to see what there is.",
            NextStepKind.RestoreSharedBackup => $"Put it back with 'savetether backup restore {step.Value} --confirm-replace'.",
            _ => throw new ArgumentOutOfRangeException(nameof(step), step.Kind, "No command-line hint for this next step."),
        };
    }

    /// <summary>The command-line flag for a choice.</summary>
    public static string FlagFor(SetupChoice choice) => choice switch
    {
        SetupChoice.Copy => "--copy",
        SetupChoice.NoCopy => "--no-copy",
        SetupChoice.AddMissing => "--add-missing",
        _ => "--replace-shared --confirm-replace",
    };

    private static string ChooseSetup(IReadOnlyList<SetupChoice> choices) =>
        choices.Contains(SetupChoice.Copy)
            ? "Choose whether they become the shared copy (--copy) or stay only in the backup (--no-copy)."
            : "Choose: " + OneOf(choices, c => $"{FlagFor(c)} ({Explain(c)})") + ".";

    private static string Explain(SetupChoice choice) => choice switch
    {
        SetupChoice.Copy => "this device's saves become the shared copy",
        SetupChoice.NoCopy => "use the shared saves; this device's stay in the backup",
        SetupChoice.AddMissing => "also add what the shared copy lacks; nothing is overwritten",
        _ => "this device's saves replace the shared copy in the sync folder",
    };

    /// <summary>"a or b", or "a, b, or c".</summary>
    private static string OneOf(IReadOnlyList<SetupChoice> choices, Func<SetupChoice, string>? word = null)
    {
        var words = choices.Select(word ?? FlagFor).ToList();
        return words.Count switch
        {
            0 => "one of the choices listed",
            1 => words[0],
            2 => $"{words[0]} or {words[1]}",
            _ => $"{string.Join(", ", words.Take(words.Count - 1))}, or {words[^1]}",
        };
    }

    private static string AppOf(string mappingId) =>
        mappingId.Contains('.', StringComparison.Ordinal) ? Identifiers.AppOf(mappingId) : "<app>";
}

/// <summary>
/// Writes a <see cref="Note"/> in <c>--json</c> output as one string, its text with the command
/// line's hints, as the messages read before notes carried next steps.
/// </summary>
internal sealed class NoteJsonConverter : JsonConverter<Note>
{
    public override Note Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, Note value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(Hints.Compose(value));
    }
}
