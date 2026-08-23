namespace TiaMcp.Contract;

public sealed record BlockInfo(
    string Name,
    BlockType Type,
    int Number,
    string Language,
    string? Comment,
    string Path,
    string GroupPath = "");

/// <summary>CPU System and Clock memory configuration (S7-1500). System memory byte provides
/// FirstScan (bit0) / AlwaysTRUE (bit2) / AlwaysFALSE (bit3); Clock memory byte provides 10/5/2.5/2Hz
/// clock pulses. Enabled=false or Byte=0 means the feature is off.</summary>
public sealed record CpuMemoryConfig(
    string DevicePath,
    bool SystemMemoryEnabled,
    long SystemMemoryByte,
    bool ClockMemoryEnabled,
    long ClockMemoryByte);

public sealed record TiaStatus(
    string Backend,
    string TiaVersion,
    string AccessMode,
    bool TiaAvailable,
    int OpenSessions);

/// <summary>A connected TIA Portal session. <c>Path</c> is the session root for building further paths.</summary>
public sealed record SessionInfo(
    string SessionId,
    string Mode,
    string Path,
    IReadOnlyList<string> Projects);

public sealed record ConnectRequest(string Mode);

public sealed record ListBlocksResult(
    string ScopePath,
    int Total,
    int Offset,
    int Limit,
    IReadOnlyList<BlockInfo> Blocks);

// --- P1: projects ---
public sealed record OpenProjectRequest(string Path, bool Visible);

public sealed record ProjectInfo(string Name, string Path, string Author, string TiaVersion);

// --- P5: project lifecycle (status / save / saveAs / create / archive / close) ---

/// <summary>Metadata snapshot of an open TIA project (nullable fields = not readable / no project).</summary>
public sealed record ProjectStatus(
    bool IsOpen,
    string? Name,
    string? Path,
    string? Version,
    string? Author,
    bool? IsModified,
    DateTime? CreationTime,
    DateTime? LastModified,
    string? LastModifiedBy,
    long? Size);

/// <summary>Result of a project lifecycle mutation (operation name + new/updated project status).</summary>
public sealed record ProjectLifecycleResult(
    string Operation,
    string? ProjectPath,
    ProjectStatus Project);

public sealed record TargetInfo(string Name, TargetKind Kind, string? TypeName, string Path);

// --- P1: compile ---
public sealed record CompileDiagnostic(
    CompileSeverity Severity,
    string? Code,
    string Message,
    string? Path,
    string? Block);

public sealed record CompileResult(
    CompileMode Mode,
    bool Success,
    int Errors,
    int Warnings,
    IReadOnlyList<CompileDiagnostic> Diagnostics,
    long DurationMs);

// --- P1: block detail / source / export ---
public sealed record BlockSource(string BlockPath, string Language, string Source);

public sealed record ExportResult(string BlockPath, ExportFormat Format, string FilePath, int Bytes);

// --- P2: tags ---
public sealed record TagInfo(string Name, string? Address, string DataType, string? Comment, string Path);

public sealed record ListTagsResult(
    string TagTablePath,
    int Total,
    int Offset,
    int Limit,
    IReadOnlyList<TagInfo> Tags);

public sealed record CreateBlockRequest(string PlcPath, string Name, BlockType Type, string Source, string Language);

public sealed record CreateTagRequest(string TagTablePath, string Name, string Address, string DataType, string? Comment);

// --- P2: mutation outcome (used by every write tool) ---
public sealed record MutationResult(
    MutationStatus Status,
    string Operation,
    string Message,
    string? Plan,
    string? ConfirmHint)
{
    public static MutationResult Applied(string op, string msg) =>
        new(MutationStatus.Applied, op, msg, null, null);

    public static MutationResult Denied(string op, string msg) =>
        new(MutationStatus.Denied, op, msg, null, null);

    public static MutationResult Awaiting(string op, string plan, string hint) =>
        new(MutationStatus.AwaitingConfirmation, op, "Confirmation required.", plan, hint);

    public static MutationResult Failed(string op, string msg) =>
        new(MutationStatus.Failed, op, msg, null, null);
}

// --- P4: device items (hardware visibility) ---
public sealed record DeviceItemInfo(string Name, string? TypeIdentifier, int Slot, string? TypeName, string Path);

// --- P6: hardware catalog / network device provisioning ---

/// <summary>One match from the TIA hardware equipment catalog.</summary>
public sealed record CatalogEntry(
    string? TypeName,
    string? ArticleNumber,
    string? Version,
    string TypeIdentifier,
    string? CatalogPath,
    string? Description);

/// <summary>Result of creating a device from a catalog typeIdentifier.</summary>
public sealed record AddDeviceResult(
    string DeviceName,
    string RootItemName,
    string TypeIdentifier,
    IReadOnlyList<string> Warnings);

/// <summary>Result of plugging a module into a device's rack slot.</summary>
public sealed record AddModuleResult(
    string DeviceName,
    string ModuleName,
    string TypeIdentifier,
    int Slot,
    string? Path);

/// <summary>Result of configuring a device's PROFINET network settings.</summary>
public sealed record ConfigureNetworkDeviceResult(
    string DeviceName,
    IReadOnlyDictionary<string, string> AppliedSettings,
    IReadOnlyDictionary<string, string> SkippedSettings,
    IReadOnlyList<string> Messages);

/// <summary>Snapshot of a project's hardware (devices + subnets/IO systems).</summary>
public sealed record HardwareConfig(
    IReadOnlyList<HwDevice> Devices,
    IReadOnlyList<HwSubnet> Subnets);

public sealed record HwDevice(string Name, string? TypeIdentifier, IReadOnlyList<HwDeviceItem> Items);

public sealed record HwDeviceItem(
    string Name, string? TypeIdentifier, int PositionNumber, string? Address,
    IReadOnlyList<HwDeviceItem> Items);

public sealed record HwSubnet(string Name, string? Type, IReadOnlyList<string> Nodes, IReadOnlyList<HwIoSystem> IoSystems);

public sealed record HwIoSystem(string Name, string? IoController, IReadOnlyList<string> ConnectedDevices);

// --- #3: structured interface (block / UDT member tree) ---
public sealed record InterfaceMember(
    string Name, string Datatype, string? StartValue, string? Comment, IReadOnlyList<InterfaceMember> Members);
public sealed record InterfaceSection(string Name, IReadOnlyList<InterfaceMember> Members);
public sealed record InterfaceInfo(string Path, string Name, string BlockType, IReadOnlyList<InterfaceSection> Sections);

// --- #6: enumerate PLC data types (UDTs) / tag tables (read-only) ---
/// <summary>One PLC data type (UDT). <c>GroupPath</c> is the slash-joined user-group trail under
/// the type folder (empty for a root-level type).</summary>
public sealed record UdtInfo(string Name, string GroupPath, string Path);
public sealed record ListUdtsResult(string ScopePath, int Total, IReadOnlyList<UdtInfo> Udts);

/// <summary>One PLC tag table. <c>TagCount</c> is the number of tags it holds; <c>GroupPath</c> is the
/// slash-joined user-group trail under the tag-table folder (empty for a root-level table).</summary>
public sealed record TagTableInfo(string Name, int TagCount, string GroupPath, string Path);
public sealed record ListTagTablesResult(string ScopePath, int Total, IReadOnlyList<TagTableInfo> TagTables);

// --- #4: cross references (where-used / uses) ---
public sealed record XRefLocation(string ReferenceType, string Access, string? Address, string? Location);
public sealed record XRefEntry(
    string Name, string? Path, string? TypeName, string? Address, IReadOnlyList<XRefLocation> Locations);
public sealed record CrossRefResult(string Path, int Count, IReadOnlyList<XRefEntry> References);

// --- P7: data import (UDT / tag-table XML), source generation, groups ---

/// <summary>Result of an XML import or a source-based generation. <c>Names</c> lists the objects that
/// actually appeared (computed by diffing the target collection before/after), so it is accurate even
/// when the input XML/source defines several objects or differs from a caller-supplied name.</summary>
public sealed record ImportResult(string ScopePath, IReadOnlyList<string> Names, string Message);

// --- P7: library / master copy reuse ---

/// <summary>A global library opened in the current Portal session.</summary>
public sealed record LibraryInfo(string Name, string Path, bool ReadOnly, int MasterCopyCount);

/// <summary>One master copy in an opened global library. <c>FolderPath</c> is the slash-joined folder
/// trail inside the library (empty for a root-level copy).</summary>
public sealed record MasterCopyInfo(string Name, string FolderPath, string? TypeName);

// --- P4: online (real-PLC interaction; simulated on Fake) ---
public sealed record OnlineStatus(string DevicePath, bool Online, string PlcState, string? Detail);

/// <summary>Structured error a read-only tool RETURNS when the backend raises (instead of throwing).
/// Thrown tool exceptions are masked by the MCP client as a generic "An error occurred invoking …",
/// hiding the real cause; returning this object (mirroring how write tools use MutationResult) lets the
/// actual message reach the agent. A read-only tool result is therefore either its typed DTO or one of
/// these — agents should check for the <c>Error</c> field.</summary>
public sealed record ToolError(string Message, string? ErrorType);
