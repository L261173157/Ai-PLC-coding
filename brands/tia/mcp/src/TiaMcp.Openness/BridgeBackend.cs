using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TiaMcp.Contract;
using TiaMcp.SimaticML;
using TiaMcp.SimaticML.Generator;

namespace TiaMcp.Openness;

/// <summary>
/// <see cref="ITiaBackend"/> that bridges to the net48 Openness worker. <c>--backend openness</c>
/// selects this. Each call is forwarded to the worker as one JSON-RPC round-trip over its
/// stdin/stdout; the <see cref="SemaphoreSlim"/> serializes them because the worker hosts a single
/// (non-thread-safe) <c>TiaPortal</c>.
/// <para>
/// The worker exe path resolves from <c>--workerPath</c> / <c>TIA_MCP_WORKER</c>, else a dev-layout
/// default near the server build output.
/// </para>
/// </summary>
public sealed class BridgeBackend : ITiaBackend, ITiaCodeBackend, IDisposable
{
    public TiaBackendKind Kind => TiaBackendKind.Openness;

    public string TiaVersion => "V21";

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    // Single TiaPortal in the worker: at most one call in flight at a time.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly WorkerChannel _channel;

    public BridgeBackend(string? workerPath = null)
    {
        _channel = new WorkerChannel(ResolveWorkerPath(workerPath));
    }

    // ----- status (never throws; degrades if the worker can't be reached) -----

    public async Task<TiaStatus> GetStatusAsync(CancellationToken ct)
    {
        try
        {
            return await CallAsync<TiaStatus>(RpcOp.GetStatus, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[bridge] status unavailable: " + ex.Message);
            return new TiaStatus("Openness(bridge)", TiaVersion, "ReadWrite", TiaAvailable: false, OpenSessions: 0);
        }
    }

    public Task<SessionInfo> ConnectAsync(ConnectRequest request, CancellationToken ct) =>
        CallAsync<SessionInfo>(RpcOp.Connect, request, ct);

    public Task DisconnectAsync(CancellationToken ct) =>
        CallVoidAsync(RpcOp.Disconnect, null, ct);

    public Task<ProjectInfo> OpenProjectAsync(string sessionPath, OpenProjectRequest request, CancellationToken ct) =>
        CallAsync<ProjectInfo>(RpcOp.OpenProject, new OpenProjectArgs(sessionPath, request), ct);

    public Task<IReadOnlyList<TargetInfo>> ListTargetsAsync(string projectPath, CancellationToken ct) =>
        CallAsync<IReadOnlyList<TargetInfo>>(RpcOp.ListTargets, new PathArgs(projectPath), ct);

    public Task<CompileResult> CompileAsync(string scopePath, CompileMode mode, CancellationToken ct) =>
        CallAsync<CompileResult>(RpcOp.Compile, new CompileArgs(scopePath, mode), ct);

    public Task<ListBlocksResult> ListBlocksAsync(string scopePath, BlockType? filter, int limit, int offset, CancellationToken ct) =>
        CallAsync<ListBlocksResult>(RpcOp.ListBlocks, new ListBlocksArgs(scopePath, filter, limit, offset), ct);

    public Task<BlockInfo> GetBlockAsync(string blockPath, CancellationToken ct) =>
        CallAsync<BlockInfo>(RpcOp.GetBlock, new PathArgs(blockPath), ct);

    public Task<BlockSource> ReadBlockSourceAsync(string blockPath, CancellationToken ct) =>
        CallAsync<BlockSource>(RpcOp.ReadBlockSource, new PathArgs(blockPath), ct);

    public Task<ExportResult> ExportBlockAsync(string blockPath, ExportFormat format, string? outDir, CancellationToken ct) =>
        CallAsync<ExportResult>(RpcOp.ExportBlock, new ExportBlockArgs(blockPath, format, outDir), ct);

    public Task<ListTagsResult> ListTagsAsync(string scopePath, int limit, int offset, CancellationToken ct) =>
        CallAsync<ListTagsResult>(RpcOp.ListTags, new ListTagsArgs(scopePath, limit, offset), ct);

    public Task<string> ImportBlockAsync(string plcPath, CreateBlockRequest request, CancellationToken ct) =>
        CallAsync<string>(RpcOp.ImportBlock, new ImportBlockArgs(plcPath, request), ct);

    public Task DeleteBlockAsync(string blockPath, CancellationToken ct) =>
        CallVoidAsync(RpcOp.DeleteBlock, new PathArgs(blockPath), ct);

    public Task<string> CreateTagAsync(string tagTablePath, CreateTagRequest request, CancellationToken ct) =>
        CallAsync<string>(RpcOp.CreateTag, new CreateTagArgs(tagTablePath, request), ct);

    public Task DeleteTagAsync(string tagPath, CancellationToken ct) =>
        CallVoidAsync(RpcOp.DeleteTag, new PathArgs(tagPath), ct);

    public Task<IReadOnlyList<DeviceItemInfo>> ListDeviceItemsAsync(string devicePath, CancellationToken ct) =>
        CallAsync<IReadOnlyList<DeviceItemInfo>>(RpcOp.ListDeviceItems, new PathArgs(devicePath), ct);

    public Task<ListUdtsResult> ListUdtsAsync(string scopePath, CancellationToken ct) =>
        CallAsync<ListUdtsResult>(RpcOp.ListUdts, new PathArgs(scopePath), ct);

    public Task<ListTagTablesResult> ListTagTablesAsync(string scopePath, CancellationToken ct) =>
        CallAsync<ListTagTablesResult>(RpcOp.ListTagTables, new PathArgs(scopePath), ct);

    public Task<ExportResult> ExportTagTableAsync(string tagTablePath, string? outDir, CancellationToken ct) =>
        CallAsync<ExportResult>(RpcOp.ExportTagTable, new ExportTagTableArgs(tagTablePath, outDir), ct);

    public Task<InterfaceInfo> ReadInterfaceAsync(string path, CancellationToken ct) =>
        CallAsync<InterfaceInfo>(RpcOp.ReadInterface, new PathArgs(path), ct);

    public async Task<BlockCode> ReadBlockCodeAsync(string blockPath, ReadCodeOptions options, CancellationToken ct)
    {
        // Reuse the existing ReadBlockSource RPC (keeps its checksum-recovery path); parse net10-side.
        // No new RpcOp, no net48 worker change — the parser is pure XDocument work.
        var src = await CallAsync<BlockSource>(RpcOp.ReadBlockSource, new PathArgs(blockPath), ct).ConfigureAwait(false);
        return SimaticMlCodeReader.Read(src.Source, blockPath, options);
    }

    public async Task<string> WriteBlockCodeAsync(string plcPath, CodeBlockSpec spec, CancellationToken ct)
    {
        // Deterministic net10-side generation, then the existing ImportBlock RPC — which keeps the
        // ImportOptions.Override (idempotent) semantics and block-group resolution in the worker.
        var xml = LadSpecGenerator.Generate(spec);
        var request = new CreateBlockRequest(
            plcPath, spec.Name, BlockTypeParser.TryParse(spec.BlockType) ?? BlockType.FB, xml, spec.Language ?? "LAD");
        return await CallAsync<string>(RpcOp.ImportBlock, new ImportBlockArgs(plcPath, request), ct).ConfigureAwait(false);
    }

    public Task<string> GenerateBlockCodeAsync(CodeBlockSpec spec, CancellationToken ct) =>
        Task.FromResult(LadSpecGenerator.Generate(spec));

    public Task<CrossRefResult> GetCrossReferencesAsync(string path, CancellationToken ct) =>
        CallAsync<CrossRefResult>(RpcOp.GetCrossReferences, new PathArgs(path), ct);

    public Task<string> DeleteDeviceAsync(string projectPath, string deviceName, CancellationToken ct) =>
        CallAsync<string>(RpcOp.DeleteDevice, new DeleteDeviceArgs(projectPath, deviceName), ct);

    public Task<string> DeleteModuleAsync(string projectPath, string deviceName, string moduleName, CancellationToken ct) =>
        CallAsync<string>(RpcOp.DeleteModule, new DeleteModuleArgs(projectPath, deviceName, moduleName), ct);

    public Task<string> DeleteSubnetAsync(string projectPath, string subnetName, CancellationToken ct) =>
        CallAsync<string>(RpcOp.DeleteSubnet, new DeleteSubnetArgs(projectPath, subnetName), ct);

    public Task<OnlineStatus> GetOnlineStatusAsync(string devicePath, CancellationToken ct) =>
        CallAsync<OnlineStatus>(RpcOp.GetOnlineStatus, new PathArgs(devicePath), ct);

    public Task ConnectOnlineAsync(string devicePath, CancellationToken ct) =>
        CallVoidAsync(RpcOp.ConnectOnline, new PathArgs(devicePath), ct);

    public Task DisconnectOnlineAsync(string devicePath, CancellationToken ct) =>
        CallVoidAsync(RpcOp.DisconnectOnline, new PathArgs(devicePath), ct);

    public Task DownloadAsync(string devicePath, CompileMode scope, CancellationToken ct) =>
        CallVoidAsync(RpcOp.Download, new DownloadArgs(devicePath, scope), ct);

    public Task PlcRunAsync(string devicePath, CancellationToken ct) =>
        CallVoidAsync(RpcOp.PlcRun, new PathArgs(devicePath), ct);

    public Task PlcStopAsync(string devicePath, CancellationToken ct) =>
        CallVoidAsync(RpcOp.PlcStop, new PathArgs(devicePath), ct);

    // ----- P5: project lifecycle -----

    public Task<ProjectStatus> GetProjectStatusAsync(string projectPath, CancellationToken ct) =>
        CallAsync<ProjectStatus>(RpcOp.GetProjectStatus, new PathArgs(projectPath), ct);

    public Task<ProjectLifecycleResult> SaveProjectAsync(string projectPath, CancellationToken ct) =>
        CallAsync<ProjectLifecycleResult>(RpcOp.SaveProject, new PathArgs(projectPath), ct);

    public Task<ProjectLifecycleResult> SaveProjectAsAsync(
        string projectPath, string targetDirectory, string targetName, bool rebind, CancellationToken ct) =>
        CallAsync<ProjectLifecycleResult>(
            RpcOp.SaveProjectAs, new SaveProjectAsArgs(projectPath, targetDirectory, targetName, rebind), ct);

    public Task<ProjectLifecycleResult> CreateProjectAsync(
        string sessionPath, string projectDirectory, string projectName, string? author, string? comment, CancellationToken ct) =>
        CallAsync<ProjectLifecycleResult>(
            RpcOp.CreateProject, new CreateProjectArgs(sessionPath, projectDirectory, projectName, author, comment), ct);

    public Task<ProjectLifecycleResult> ArchiveProjectAsync(
        string projectPath, string archiveDirectory, string archiveName, ArchiveMode mode, bool saveBeforeArchive, CancellationToken ct) =>
        CallAsync<ProjectLifecycleResult>(
            RpcOp.ArchiveProject, new ArchiveProjectArgs(projectPath, archiveDirectory, archiveName, mode, saveBeforeArchive), ct);

    public Task<ProjectLifecycleResult> CloseProjectAsync(string projectPath, bool saveBeforeClose, CancellationToken ct) =>
        CallAsync<ProjectLifecycleResult>(RpcOp.CloseProject, new CloseProjectArgs(projectPath, saveBeforeClose), ct);

    // ----- P6: hardware catalog / network device provisioning -----

    public Task<IReadOnlyList<CatalogEntry>> SearchEquipmentCatalogAsync(string scopePath, string query, CancellationToken ct) =>
        CallAsync<IReadOnlyList<CatalogEntry>>(RpcOp.SearchEquipmentCatalog, new SearchCatalogArgs(scopePath, query), ct);

    public Task<AddDeviceResult> AddNetworkDeviceAsync(
        string projectPath, string typeIdentifier, string deviceName, string deviceItemName, CancellationToken ct) =>
        CallAsync<AddDeviceResult>(
            RpcOp.AddNetworkDevice, new AddNetworkDeviceArgs(projectPath, typeIdentifier, deviceName, deviceItemName), ct);

    public Task<ConfigureNetworkDeviceResult> ConfigureNetworkDeviceAsync(
        string projectPath, string deviceName, string? ipAddress, string? subnetMask,
        string? pnDeviceName, string? subnetName, string? ioSystemName, CancellationToken ct) =>
        CallAsync<ConfigureNetworkDeviceResult>(
            RpcOp.ConfigureNetworkDevice,
            new ConfigureNetworkDeviceArgs(projectPath, deviceName, ipAddress, subnetMask, pnDeviceName, subnetName, ioSystemName), ct);

    public Task<CpuMemoryConfig> ConfigureCpuMemoryAsync(
        string devicePath, bool? enableSystemMemory, long? systemMemoryByte,
        bool? enableClockMemory, long? clockMemoryByte, CancellationToken ct) =>
        CallAsync<CpuMemoryConfig>(
            RpcOp.ConfigureCpuMemory,
            new ConfigureCpuMemoryArgs(devicePath, enableSystemMemory, systemMemoryByte, enableClockMemory, clockMemoryByte), ct);

    public Task<AddModuleResult> AddModuleAsync(
        string projectPath, string deviceName, string typeIdentifier, int? slot, string? moduleName, CancellationToken ct) =>
        CallAsync<AddModuleResult>(
            RpcOp.AddModule, new AddModuleArgs(projectPath, deviceName, typeIdentifier, slot, moduleName), ct);

    public Task<HardwareConfig> ReadHardwareConfigAsync(string projectPath, CancellationToken ct) =>
        CallAsync<HardwareConfig>(RpcOp.ReadHardwareConfig, new PathArgs(projectPath), ct);

    // ----- P7: data import / source generation / groups / library reuse -----

    public Task<ImportResult> ImportUdtAsync(string plcPath, string sourceXml, CancellationToken ct) =>
        CallAsync<ImportResult>(RpcOp.ImportUdt, new ImportXmlArgs(plcPath, sourceXml), ct);

    public Task<ImportResult> ImportTagTableAsync(string plcPath, string sourceXml, CancellationToken ct) =>
        CallAsync<ImportResult>(RpcOp.ImportTagTable, new ImportXmlArgs(plcPath, sourceXml), ct);

    public Task<ImportResult> GenerateBlocksFromSourceAsync(
        string plcPath, string sourceName, string sourceText, CancellationToken ct) =>
        CallAsync<ImportResult>(
            RpcOp.GenerateBlocksFromSource, new GenerateBlocksArgs(plcPath, sourceName, sourceText), ct);

    public Task<string> CreateGroupAsync(string plcPath, string groupKind, string groupName, CancellationToken ct) =>
        CallAsync<string>(RpcOp.CreateGroup, new CreateGroupArgs(plcPath, groupKind, groupName), ct);

    public Task<LibraryInfo> OpenLibraryAsync(string libraryPath, bool readOnly, CancellationToken ct) =>
        CallAsync<LibraryInfo>(RpcOp.OpenLibrary, new OpenLibraryArgs(libraryPath, readOnly), ct);

    public Task<IReadOnlyList<MasterCopyInfo>> ListMasterCopiesAsync(string libraryName, CancellationToken ct) =>
        CallAsync<IReadOnlyList<MasterCopyInfo>>(RpcOp.ListMasterCopies, new PathArgs(libraryName), ct);

    public Task<string> CreateBlockFromCopyAsync(
        string plcPath, string libraryName, string masterCopyName, CancellationToken ct) =>
        CallAsync<string>(
            RpcOp.CreateBlockFromCopy, new CreateBlockFromCopyArgs(plcPath, libraryName, masterCopyName), ct);

    public void Dispose() => _channel.Dispose();

    // ----- forwarding helpers -----

    private async Task<T> CallAsync<T>(string op, object? args, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var resp = await _channel.SendAsync(new RpcRequest(NewId(), op, SerializeArgs(args)), ct).ConfigureAwait(false);
            RequireOk(resp);
            return Deserialize<T>(resp.ResultJson);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CallVoidAsync(string op, object? args, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var resp = await _channel.SendAsync(new RpcRequest(NewId(), op, SerializeArgs(args)), ct).ConfigureAwait(false);
            RequireOk(resp);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void RequireOk(RpcResponse resp)
    {
        if (!resp.Ok)
        {
            var type = string.IsNullOrEmpty(resp.ErrorType) ? "" : " (" + resp.ErrorType + ")";
            throw new InvalidOperationException("Openness worker error" + type + ": " + (resp.Error ?? "unknown"));
        }
    }

    private static string? SerializeArgs(object? args) =>
        args is null ? null : JsonSerializer.Serialize(args, args.GetType(), Json);

    private static T Deserialize<T>(string? json) =>
        json is null
            ? throw new InvalidOperationException("Worker returned no result payload.")
            : JsonSerializer.Deserialize<T>(json, Json)
              ?? throw new InvalidOperationException("Worker returned a null " + typeof(T).Name + ".");

    private static int _idCounter;

    private static string NewId() => Interlocked.Increment(ref _idCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Find the worker exe: explicit path first, else a dev-layout default near the server.</summary>
    private static string ResolveWorkerPath(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        // 1. Published/bundled layout: the worker ships next to this server exe in openness-worker/
        //    (the BundleOpennessWorker MSBuild target drops it there on publish). AppContext.BaseDirectory
        //    is the exe folder for a published app.
        var bundled = Path.Combine(AppContext.BaseDirectory, "openness-worker", "TiaMcp.Openness.Worker.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        // 2. Dev layout: .../src/TiaMcp.Server/bin/<cfg>/net10.0/. Up four = .../src/.
        var srcDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        const string rel = "TiaMcp.Openness.Worker/bin/{0}/net48/TiaMcp.Openness.Worker.exe";
        foreach (var cfg in new[] { "Debug", "Release" })
        {
            var candidate = Path.Combine(srcDir, string.Format(System.Globalization.CultureInfo.InvariantCulture, rel, cfg));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Best guess (CallAsync will throw a clear FileNotFound if it isn't there).
        return Path.Combine(srcDir, string.Format(System.Globalization.CultureInfo.InvariantCulture, rel, "Debug"));
    }
}
