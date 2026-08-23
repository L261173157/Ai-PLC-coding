namespace TiaMcp.Contract;

// =====================================================================
// Wire protocol between the net10 BridgeBackend and the net48 Openness worker.
//
// The worker is a separate .NET Framework 4.8 process (TIA V21 Openness is net48-only; a net10
// process cannot load it). The bridge spawns it and talks over its stdin/stdout, one JSON object
// per line, one request -> one response, strictly serial (a single TiaPortal is not thread-safe).
//
// Args and results ride as RAW JSON STRINGS (ArgsJson / ResultJson) so this contract stays free of
// any System.Text.Json type dependency -> it compiles unchanged on net10.0 and netstandard2.0.
// Each side serializes/deserializes the typed arg/result records below with System.Text.Json using
// the same options (JsonStringEnumConverter) so the round-trip is exact.
// =====================================================================

/// <summary>One request from bridge -> worker.</summary>
public sealed record RpcRequest(string Id, string Op, string? ArgsJson);

/// <summary>One response from worker -> bridge. Either Ok with a ResultJson payload, or !Ok with an error.</summary>
public sealed record RpcResponse(string Id, bool Ok, string? ResultJson, string? Error, string? ErrorType);

/// <summary>Operation names (single source of truth; match ITiaBackend methods 1:1).</summary>
public static class RpcOp
{
    public const string GetStatus = "GetStatus";
    public const string Connect = "Connect";
    public const string Disconnect = "Disconnect";
    public const string OpenProject = "OpenProject";
    public const string ListTargets = "ListTargets";
    public const string Compile = "Compile";
    public const string ListBlocks = "ListBlocks";
    public const string GetBlock = "GetBlock";
    public const string ReadBlockSource = "ReadBlockSource";
    public const string ExportBlock = "ExportBlock";
    public const string ListTags = "ListTags";
    public const string ImportBlock = "ImportBlock";
    public const string DeleteBlock = "DeleteBlock";
    public const string CreateTag = "CreateTag";
    public const string DeleteTag = "DeleteTag";
    public const string ListDeviceItems = "ListDeviceItems";
    public const string GetOnlineStatus = "GetOnlineStatus";
    public const string ConnectOnline = "ConnectOnline";
    public const string DisconnectOnline = "DisconnectOnline";
    public const string Download = "Download";
    public const string PlcRun = "PlcRun";
    public const string PlcStop = "PlcStop";

    // P5 — project lifecycle
    public const string GetProjectStatus = "GetProjectStatus";
    public const string SaveProject = "SaveProject";
    public const string SaveProjectAs = "SaveProjectAs";
    public const string CreateProject = "CreateProject";
    public const string ArchiveProject = "ArchiveProject";
    public const string CloseProject = "CloseProject";

    // P6 — hardware catalog / network device provisioning
    public const string SearchEquipmentCatalog = "SearchEquipmentCatalog";
    public const string AddNetworkDevice = "AddNetworkDevice";
    public const string ConfigureNetworkDevice = "ConfigureNetworkDevice";
    public const string ConfigureCpuMemory = "ConfigureCpuMemory";
    public const string AddModule = "AddModule";
    public const string ReadHardwareConfig = "ReadHardwareConfig";

    // P7 — data import / source generation / groups / library reuse
    public const string ImportUdt = "ImportUdt";
    public const string ImportTagTable = "ImportTagTable";
    public const string GenerateBlocksFromSource = "GenerateBlocksFromSource";
    public const string CreateGroup = "CreateGroup";
    public const string OpenLibrary = "OpenLibrary";
    public const string ListMasterCopies = "ListMasterCopies";
    public const string CreateBlockFromCopy = "CreateBlockFromCopy";

    // #3/#4 — structured interface read + cross references (single-path args -> PathArgs)
    public const string ReadInterface = "ReadInterface";
    public const string GetCrossReferences = "GetCrossReferences";

    // #5 — hardware deletes
    public const string DeleteDevice = "DeleteDevice";
    public const string DeleteModule = "DeleteModule";
    public const string DeleteSubnet = "DeleteSubnet";

    // #6 — enumerate UDTs / tag tables (single-path args -> PathArgs)
    public const string ListUdts = "ListUdts";
    public const string ListTagTables = "ListTagTables";

    // tag-table export (reverse of ImportTagTable)
    public const string ExportTagTable = "ExportTagTable";
}

// --- typed arg records (serialize to ArgsJson). Result types are the existing DTOs/strings/null. ---

public sealed record OpenProjectArgs(string SessionPath, OpenProjectRequest Request);

public sealed record CompileArgs(string ScopePath, CompileMode Mode);

public sealed record ListBlocksArgs(string ScopePath, BlockType? Filter, int Limit, int Offset);

public sealed record ListTagsArgs(string ScopePath, int Limit, int Offset);

public sealed record ExportBlockArgs(string BlockPath, ExportFormat Format, string? OutDir);

public sealed record ExportTagTableArgs(string TagTablePath, string? OutDir);

public sealed record ImportBlockArgs(string PlcPath, CreateBlockRequest Request);

public sealed record CreateTagArgs(string TagTablePath, CreateTagRequest Request);

public sealed record DownloadArgs(string DevicePath, CompileMode Scope);

// --- P5: project lifecycle args ---

public sealed record SaveProjectAsArgs(string ProjectPath, string TargetDirectory, string TargetName, bool Rebind);

public sealed record CreateProjectArgs(
    string SessionPath, string ProjectDirectory, string ProjectName, string? Author, string? Comment);

public sealed record ArchiveProjectArgs(
    string ProjectPath, string ArchiveDirectory, string ArchiveName, ArchiveMode Mode, bool SaveBeforeArchive);

public sealed record CloseProjectArgs(string ProjectPath, bool SaveBeforeClose);

// --- P6: hardware catalog / network device provisioning args ---

public sealed record SearchCatalogArgs(string ScopePath, string Query);

public sealed record AddNetworkDeviceArgs(
    string ProjectPath, string TypeIdentifier, string DeviceName, string DeviceItemName);

public sealed record ConfigureNetworkDeviceArgs(
    string ProjectPath, string DeviceName, string? IpAddress, string? SubnetMask,
    string? PnDeviceName, string? SubnetName, string? IoSystemName);

public sealed record ConfigureCpuMemoryArgs(
    string DevicePath, bool? EnableSystemMemory, long? SystemMemoryByte,
    bool? EnableClockMemory, long? ClockMemoryByte);

public sealed record AddModuleArgs(
    string ProjectPath, string DeviceName, string TypeIdentifier, int? Slot, string? ModuleName);

// --- P7: data import / source generation / groups / library reuse args ---

public sealed record ImportXmlArgs(string PlcPath, string SourceXml);

public sealed record GenerateBlocksArgs(string PlcPath, string SourceName, string SourceText);

public sealed record CreateGroupArgs(string PlcPath, string GroupKind, string GroupName);

public sealed record OpenLibraryArgs(string LibraryPath, bool ReadOnly);

public sealed record CreateBlockFromCopyArgs(string PlcPath, string LibraryName, string MasterCopyName);

/// <summary>Generic single-path argument (used by all ops whose only input is one path).</summary>
public sealed record PathArgs(string Path);

// --- #5: hardware delete args ---
public sealed record DeleteDeviceArgs(string ProjectPath, string DeviceName);

public sealed record DeleteModuleArgs(string ProjectPath, string DeviceName, string ModuleName);

public sealed record DeleteSubnetArgs(string ProjectPath, string SubnetName);
