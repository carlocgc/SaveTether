using System.Text.Json.Serialization;

namespace SaveTether.Core.Backup;

/// <summary>Why a backup was taken.</summary>
public enum BackupReason
{
    [JsonStringEnumMemberName("pre-apply")]
    PreApply,

    [JsonStringEnumMemberName("pre-revert")]
    PreRevert,

    [JsonStringEnumMemberName("baseline")]
    Baseline,

    [JsonStringEnumMemberName("snapshot")]
    Snapshot,

    [JsonStringEnumMemberName("pre-restore")]
    PreRestore,
}

/// <summary>One file in a backup.</summary>
/// <param name="Path">Path below the backed-up folder, with forward slashes.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="Sha256">Lowercase hex SHA-256 of the source file's content.</param>
public sealed record BackupFile(string Path, long Size, string Sha256);

/// <summary>
/// A link that was inside the backed-up folder. The link is recorded here, not copied, and its
/// target is never read. A restore can recreate it from this record.
/// </summary>
/// <param name="Path">Path below the backed-up folder, with forward slashes.</param>
/// <param name="Kind">"symlink" or "junction".</param>
/// <param name="Target">The target exactly as the link stored it.</param>
public sealed record BackupLink(string Path, string Kind, string Target);

/// <summary>
/// The contents of manifest.json, written beside a backup's data folder. It lists everything
/// needed to check the backup and to restore it by hand.
/// </summary>
public sealed record BackupManifest(
    int Schema,
    string Id,
    string MappingId,
    DateTimeOffset CreatedUtc,
    BackupReason Reason,
    string SourcePath,
    IReadOnlyList<string> Directories,
    IReadOnlyList<BackupFile> Files,
    IReadOnlyList<BackupLink> Links)
{
    public const int CurrentSchema = 1;
}

/// <summary>A backup as shown in a listing.</summary>
/// <param name="Id">"mapping-id/timestamp". Pass it to <see cref="BackupStore.Verify"/>.</param>
/// <param name="Location">The backup's folder. Its "data" subfolder is a plain copy of the source.</param>
public sealed record BackupInfo(
    string Id,
    string MappingId,
    DateTimeOffset CreatedUtc,
    BackupReason Reason,
    string SourcePath,
    int FileCount,
    long TotalBytes,
    string Location);

/// <summary>The result of checking a backup against its manifest.</summary>
public sealed record BackupVerification(IReadOnlyList<string> Problems)
{
    public bool IsValid => Problems.Count == 0;
}

/// <summary>A backup could not be taken, found or trusted.</summary>
public sealed class BackupException : IOException
{
    public BackupException()
    {
    }

    public BackupException(string message)
        : base(message)
    {
    }

    public BackupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(BackupManifest))]
internal sealed partial class BackupJsonContext : JsonSerializerContext;
