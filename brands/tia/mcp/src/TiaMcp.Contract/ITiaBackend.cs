namespace TiaMcp.Contract;

/// <summary>
/// The single seam between the MCP tool layer and any concrete TIA driver. Every method is
/// <b>path-addressed</b>. The backend holds no policy: it performs mechanics and throws on
/// not-found / conflict. Mode gating, destructive-confirm and auditing live in the
/// <c>TiaMcp.Server</c> AccessGuard, which wraps these calls.
/// Implementations: <c>TiaMcp.Fake.FakeBackend</c> (offline) and
/// <c>TiaMcp.Openness.BridgeBackend</c> (real TIA V21; spawns the net48 worker).
/// </summary>
public interface ITiaBackend
{
    TiaBackendKind Kind { get; }

    /// <summary>Display version of the TIA target, e.g. "V21" or "Fake-0.1".</summary>
    string TiaVersion { get; }

    /// <summary>Report server + backend status. Always safe to call (never throws).</summary>
    Task<TiaStatus> GetStatusAsync(CancellationToken ct);

    /// <summary>Launch / attach a TIA Portal session.</summary>
    Task<SessionInfo> ConnectAsync(ConnectRequest request, CancellationToken ct);

    /// <summary>Drop the TIA Portal session: close any open projects and release the (headless) Portal
    /// process, freeing its memory without killing the worker. The next <see cref="ConnectAsync"/>
    /// re-spawns a fresh Portal. Always safe to call (no-op if no session is open).</summary>
    Task DisconnectAsync(CancellationToken ct);

    // --- P1: projects ---
    Task<ProjectInfo> OpenProjectAsync(string sessionPath, OpenProjectRequest request, CancellationToken ct);
    Task<IReadOnlyList<TargetInfo>> ListTargetsAsync(string projectPath, CancellationToken ct);
    Task<CompileResult> CompileAsync(string scopePath, CompileMode mode, CancellationToken ct);

    // --- P5: project lifecycle (status / save / saveAs / create / archive / close) ---
    Task<ProjectStatus> GetProjectStatusAsync(string projectPath, CancellationToken ct);
    Task<ProjectLifecycleResult> SaveProjectAsync(string projectPath, CancellationToken ct);
    Task<ProjectLifecycleResult> SaveProjectAsAsync(string projectPath, string targetDirectory, string targetName, bool rebind, CancellationToken ct);
    Task<ProjectLifecycleResult> CreateProjectAsync(string sessionPath, string projectDirectory, string projectName, string? author, string? comment, CancellationToken ct);
    Task<ProjectLifecycleResult> ArchiveProjectAsync(string projectPath, string archiveDirectory, string archiveName, ArchiveMode mode, bool saveBeforeArchive, CancellationToken ct);
    Task<ProjectLifecycleResult> CloseProjectAsync(string projectPath, bool saveBeforeClose, CancellationToken ct);

    // --- P1: blocks (read) ---
    Task<ListBlocksResult> ListBlocksAsync(string scopePath, BlockType? filter, int limit, int offset, CancellationToken ct);
    Task<BlockInfo> GetBlockAsync(string blockPath, CancellationToken ct);
    Task<BlockSource> ReadBlockSourceAsync(string blockPath, CancellationToken ct);
    Task<ExportResult> ExportBlockAsync(string blockPath, ExportFormat format, string? outDir, CancellationToken ct);

    // --- P2: tags (read + write mechanics) ---
    Task<ListTagsResult> ListTagsAsync(string scopePath, int limit, int offset, CancellationToken ct);

    /// <summary>Add a block from source. Returns the new block path. Throws on name conflict.</summary>
    Task<string> ImportBlockAsync(string plcPath, CreateBlockRequest request, CancellationToken ct);

    /// <summary>Delete a block. Throws if not found.</summary>
    Task DeleteBlockAsync(string blockPath, CancellationToken ct);

    /// <summary>Add a tag. Returns the new tag path. Throws on name conflict.</summary>
    Task<string> CreateTagAsync(string tagTablePath, CreateTagRequest request, CancellationToken ct);

    /// <summary>Delete a tag. Throws if not found.</summary>
    Task DeleteTagAsync(string tagPath, CancellationToken ct);

    // --- #6: enumerate PLC data types (UDTs) / tag tables (read) ---
    /// <summary>List the PLC data types (UDTs) under a scope, recursing type user-groups.</summary>
    Task<ListUdtsResult> ListUdtsAsync(string scopePath, CancellationToken ct);

    /// <summary>List the PLC tag tables under a scope (with tag counts), recursing tag-table user-groups.</summary>
    Task<ListTagTablesResult> ListTagTablesAsync(string scopePath, CancellationToken ct);

    /// <summary>Export a PLC tag table (with its tags) to a SimaticML XML file — the reverse of
    /// <see cref="ImportTagTableAsync"/>. Returns the absolute file path written.</summary>
    Task<ExportResult> ExportTagTableAsync(string tagTablePath, string? outDir, CancellationToken ct);

    // --- #3: structured interface read (block or UDT member tree) ---
    /// <summary>Read a block's or UDT's interface as a structured member tree (sections -> members,
    /// nested for structs), instead of raw SimaticML XML. Path is a block path; a UDT resolves by name.</summary>
    Task<InterfaceInfo> ReadInterfaceAsync(string path, CancellationToken ct);

    // --- #4: cross references (where-used / uses) ---
    /// <summary>Cross-references for the object at <paramref name="path"/> (a block): what it uses and
    /// where it is used, with reference type (Uses/UsedBy/…) and access (Read/Write/Call/…).</summary>
    Task<CrossRefResult> GetCrossReferencesAsync(string path, CancellationToken ct);

    // --- #5: hardware deletes (symmetric with add device/module + subnet) ---
    /// <summary>Delete a device (station). Throws if not found.</summary>
    Task<string> DeleteDeviceAsync(string projectPath, string deviceName, CancellationToken ct);

    /// <summary>Delete a plugged module (device item) from a device. Throws if not found / not deletable.</summary>
    Task<string> DeleteModuleAsync(string projectPath, string deviceName, string moduleName, CancellationToken ct);

    /// <summary>Delete a subnet. Throws if not found.</summary>
    Task<string> DeleteSubnetAsync(string projectPath, string subnetName, CancellationToken ct);

    // --- P4: device items (hardware visibility; read) ---
    Task<IReadOnlyList<DeviceItemInfo>> ListDeviceItemsAsync(string devicePath, CancellationToken ct);

    // --- P6: hardware catalog / network device provisioning ---
    /// <summary>Search the TIA hardware equipment catalog; returns matching typeIdentifiers.</summary>
    Task<IReadOnlyList<CatalogEntry>> SearchEquipmentCatalogAsync(string scopePath, string query, CancellationToken ct);

    /// <summary>Create a device from a catalog typeIdentifier. Returns the new device info.</summary>
    Task<AddDeviceResult> AddNetworkDeviceAsync(
        string projectPath, string typeIdentifier, string deviceName, string deviceItemName, CancellationToken ct);

    /// <summary>Plug a module (signal/communication) into a device's rack slot. Null slot = next free slot (≥2).</summary>
    Task<AddModuleResult> AddModuleAsync(
        string projectPath, string deviceName, string typeIdentifier, int? slot, string? moduleName, CancellationToken ct);

    /// <summary>Configure a device's PROFINET settings (IP/subnet/pnDeviceName/subnet/ioSystem).</summary>
    Task<ConfigureNetworkDeviceResult> ConfigureNetworkDeviceAsync(
        string projectPath, string deviceName, string? ipAddress, string? subnetMask,
        string? pnDeviceName, string? subnetName, string? ioSystemName, CancellationToken ct);

    /// <summary>Configure (or read) the CPU's System and Clock memory byte. Null params = read-only.</summary>
    Task<CpuMemoryConfig> ConfigureCpuMemoryAsync(
        string devicePath, bool? enableSystemMemory, long? systemMemoryByte,
        bool? enableClockMemory, long? clockMemoryByte, CancellationToken ct);

    /// <summary>Read the project's hardware configuration (devices + subnets/IO systems).</summary>
    Task<HardwareConfig> ReadHardwareConfigAsync(string projectPath, CancellationToken ct);

    // --- P7: data import (UDT / tag-table XML), source generation, groups ---

    /// <summary>Import one or more UDTs (PLC data types) from SimaticML XML into a PLC.</summary>
    Task<ImportResult> ImportUdtAsync(string plcPath, string sourceXml, CancellationToken ct);

    /// <summary>Import a tag table (and its tags) from SimaticML XML into a PLC.</summary>
    Task<ImportResult> ImportTagTableAsync(string plcPath, string sourceXml, CancellationToken ct);

    /// <summary>Generate program blocks from external source text (e.g. SCL/AWL) via the
    /// ExternalSources path. Returns the blocks that were created.</summary>
    Task<ImportResult> GenerateBlocksFromSourceAsync(
        string plcPath, string sourceName, string sourceText, CancellationToken ct);

    /// <summary>Create an organizing group/folder under a PLC. <paramref name="groupKind"/> is one of
    /// <c>block</c> | <c>type</c> | <c>tagtable</c>. Returns the new group path.</summary>
    Task<string> CreateGroupAsync(string plcPath, string groupKind, string groupName, CancellationToken ct);

    // --- P7: library / master copy reuse ---

    /// <summary>Open a global library (.al21) in the current Portal session for master-copy reuse.</summary>
    Task<LibraryInfo> OpenLibraryAsync(string libraryPath, bool readOnly, CancellationToken ct);

    /// <summary>List the master copies in an opened global library (by library name).</summary>
    Task<IReadOnlyList<MasterCopyInfo>> ListMasterCopiesAsync(string libraryName, CancellationToken ct);

    /// <summary>Instantiate a library master copy as a new block in a PLC. Returns the new block path.</summary>
    Task<string> CreateBlockFromCopyAsync(
        string plcPath, string libraryName, string masterCopyName, CancellationToken ct);

    // --- P4: online (real-PLC interaction; simulated on Fake, needs hardware on Openness) ---
    Task<OnlineStatus> GetOnlineStatusAsync(string devicePath, CancellationToken ct);

    /// <summary>Establish the online connection to a PLC. Throws on error.</summary>
    Task ConnectOnlineAsync(string devicePath, CancellationToken ct);

    /// <summary>Drop the online connection. Throws on error.</summary>
    Task DisconnectOnlineAsync(string devicePath, CancellationToken ct);

    /// <summary>Download hardware/software to the PLC. Throws on error.</summary>
    Task DownloadAsync(string devicePath, CompileMode scope, CancellationToken ct);

    /// <summary>Set the PLC to RUN. Throws on error.</summary>
    Task PlcRunAsync(string devicePath, CancellationToken ct);

    /// <summary>Set the PLC to STOP. Throws on error.</summary>
    Task PlcStopAsync(string devicePath, CancellationToken ct);
}
