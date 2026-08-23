namespace TiaMcp.Contract;

public enum BlockType
{
    OB,
    FB,
    FC,
    DB,
    UDT,
}

public enum TiaBackendKind
{
    Fake,
    Openness,
}

public enum CompileMode
{
    Software,
    Hardware,
    All,
}

/// <summary>Project archive mode (maps 1:1 to Siemens <c>ProjectArchivationMode</c> by name).</summary>
public enum ArchiveMode
{
    None,
    DiscardRestorableData,
    Compressed,
    DiscardRestorableDataAndCompressed,
}

public enum ExportFormat
{
    SclSource,
    Xml,
}

public enum TargetKind
{
    Plc,
    Hmi,
}

public enum CompileSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Server-wide mutation permission. The effective default is <see cref="ReadWrite"/>
/// (Program.cs falls back to it when --mode is unset); pass --mode ReadOnly to lock down.
/// </summary>
public enum AccessMode
{
    ReadOnly,
    ReadWrite,
    Unrestricted,
}

/// <summary>Outcome category for a mutation tool call.</summary>
public enum MutationStatus
{
    /// <summary>The mutation was performed.</summary>
    Applied,

    /// <summary>Blocked by the access mode (e.g. write while ReadOnly).</summary>
    Denied,

    /// <summary>Destructive op awaiting confirm=true before it will run.</summary>
    AwaitingConfirmation,

    /// <summary>Attempted but the backend raised an error (e.g. name conflict).</summary>
    Failed,
}

public static class BlockTypeParser
{
    public static BlockType? TryParse(string? text) => EnumText.TryParse<BlockType>(text);
}

/// <summary>Case-insensitive enum parser; returns null on empty/unknown so tool params stay optional.</summary>
public static class EnumText
{
    public static T? TryParse<T>(string? text) where T : struct, Enum =>
        string.IsNullOrWhiteSpace(text)
            ? null
            : Enum.TryParse<T>(text, ignoreCase: true, out var v) ? v : null;
}
