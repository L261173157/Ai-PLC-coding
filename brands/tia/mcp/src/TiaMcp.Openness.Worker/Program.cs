using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TiaMcp.Contract;

namespace TiaMcp.Openness.Worker;

/// <summary>
/// stdin/stdout line-delimited JSON-RPC loop. Reads one <see cref="RpcRequest"/> per line, dispatches
/// it to the <see cref="OpennessEngine"/>, writes one <see cref="RpcResponse"/> per line. Strictly
/// serial (single TiaPortal). <b>stdout carries ONLY the protocol</b> — all diagnostics go to stderr.
/// </summary>
internal static class Program
{
    // SAME options as the net10 BridgeBackend (enums as strings) so JSON round-trips into the shared
    // Contract types on both sides.
    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    private static async Task<int> Main()
    {
        // Force UTF-8 on both ends of the pipe so non-ASCII content (SCL/tag comments) survives.
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);

        // Tee stderr to a per-process log file. The server forwards worker stderr to its OWN stderr but
        // nothing persists it, so crash-time diagnostics (esp. the [tia] notification/confirmation events
        // right before a Portal crash) were being lost — see the 2026-06-23 compile-crash investigation.
        SetupStderrLog();

        // MUST register before any Siemens type is touched (i.e. before `new OpennessEngine()`, whose
        // fields reference TiaPortal). The Siemens.Engineering.* API DLLs are Copy-Local=False and not
        // in the GAC on this install, so we load them from the TIA install's PublicAPI\V21\net48 dir.
        // Loading them from inside the TIA tree is what keeps OpennessLocationProvider happy.
        RegisterSiemensAssemblyResolver();

        var engine = new OpennessEngine();

        string? line;
        while ((line = await Console.In.ReadLineAsync().ConfigureAwait(false)) != null)
        {
            // Tolerate a stray UTF-8 BOM some clients prepend to the first line.
            line = line.TrimStart('﻿').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            RpcResponse resp;
            try
            {
                resp = await HandleAsync(engine, line).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[worker] fatal dispatch error: " + ex);
                resp = new RpcResponse("?", Ok: false, ResultJson: null, Error: ex.Message, ErrorType: ex.GetType().FullName);
            }

            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(resp, Json)).ConfigureAwait(false);
            await Console.Out.FlushAsync().ConfigureAwait(false);
        }

        // stdin closed -> server is done with us. Dispose the TiaPortal so no headless instance lingers.
        engine.Dispose();
        return 0;
    }

    private static async Task<RpcResponse> HandleAsync(OpennessEngine engine, string line)
    {
        var req = JsonSerializer.Deserialize<RpcRequest>(line, Json)
                  ?? throw new InvalidOperationException("Empty request line.");

        Console.Error.WriteLine("[worker] >>> " + req.Op + " #" + req.Id);
        try
        {
            var resultJson = await DispatchAsync(engine, req).ConfigureAwait(false);
            Console.Error.WriteLine("[worker] <<< " + req.Op + " #" + req.Id);
            return new RpcResponse(req.Id, Ok: true, resultJson, Error: null, ErrorType: null);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[worker] op '" + req.Op + "' failed: " + ex);
            return new RpcResponse(req.Id, Ok: false, ResultJson: null, Error: ex.Message, ErrorType: ex.GetType().FullName);
        }
    }

    private static async Task<string?> DispatchAsync(OpennessEngine e, RpcRequest req)
    {
        var ct = CancellationToken.None;
        switch (req.Op)
        {
            case RpcOp.GetStatus:
                return J(await e.GetStatusAsync(ct).ConfigureAwait(false));

            case RpcOp.Connect:
                return J(await e.ConnectAsync(Arg<ConnectRequest>(req), ct).ConfigureAwait(false));

            case RpcOp.Disconnect:
                await e.DisconnectAsync(ct).ConfigureAwait(false);
                return null;

            case RpcOp.OpenProject:
            {
                var a = Arg<OpenProjectArgs>(req);
                return J(await e.OpenProjectAsync(a.SessionPath, a.Request, ct).ConfigureAwait(false));
            }

            case RpcOp.ListTargets:
                return J(await e.ListTargetsAsync(Arg<PathArgs>(req).Path, ct).ConfigureAwait(false));

            case RpcOp.Compile:
            {
                var a = Arg<CompileArgs>(req);
                return J(await e.CompileAsync(a.ScopePath, a.Mode, ct).ConfigureAwait(false));
            }

            case RpcOp.ListBlocks:
            {
                var a = Arg<ListBlocksArgs>(req);
                return J(await e.ListBlocksAsync(a.ScopePath, a.Filter, a.Limit, a.Offset, ct).ConfigureAwait(false));
            }

            case RpcOp.GetBlock:
                return J(await e.GetBlockAsync(Arg<PathArgs>(req).Path, ct).ConfigureAwait(false));

            case RpcOp.ReadBlockSource:
                return J(await e.ReadBlockSourceAsync(Arg<PathArgs>(req).Path, ct).ConfigureAwait(false));

            case RpcOp.ExportBlock:
            {
                var a = Arg<ExportBlockArgs>(req);
                return J(await e.ExportBlockAsync(a.BlockPath, a.Format, a.OutDir, ct).ConfigureAwait(false));
            }

            case RpcOp.ListTags:
            {
                var a = Arg<ListTagsArgs>(req);
                return J(await e.ListTagsAsync(a.ScopePath, a.Limit, a.Offset, ct).ConfigureAwait(false));
            }

            case RpcOp.ImportBlock:
            {
                var a = Arg<ImportBlockArgs>(req);
                return J(await e.ImportBlockAsync(a.PlcPath, a.Request, ct).ConfigureAwait(false));
            }

            case RpcOp.DeleteBlock:
                await e.DeleteBlockAsync(Arg<PathArgs>(req).Path, ct).ConfigureAwait(false);
                return null;

            case RpcOp.CreateTag:
            {
                var a = Arg<CreateTagArgs>(req);
                return J(await e.CreateTagAsync(a.TagTablePath, a.Request, ct).ConfigureAwait(false));
            }

            case RpcOp.DeleteTag:
                await e.DeleteTagAsync(Arg<PathArgs>(req).Path, ct).ConfigureAwait(false);
                return null;

            case RpcOp.ListDeviceItems:
                return J(await e.ListDeviceItemsAsync(Arg<PathArgs>(req).Path, ct).ConfigureAwait(false));

            case RpcOp.ListUdts:
                return J(await e.ListUdtsAsync(Arg<PathArgs>(req).Path, ct).ConfigureAwait(false));

            case RpcOp.ListTagTables:
                return J(await e.ListTagTablesAsync(Arg<PathArgs>(req).Path, ct).ConfigureAwait(false));

            case RpcOp.ExportTagTable:
            {
                var a = Arg<ExportTagTableArgs>(req);
                return J(await e.ExportTagTableAsync(a.TagTablePath, a.OutDir, ct).ConfigureAwait(false));
            }

            case RpcOp.ReadInterface:
                return J(await e.ReadInterfaceAsync(Arg<PathArgs>(req).Path, ct).ConfigureAwait(false));

            case RpcOp.GetCrossReferences:
                return J(await e.GetCrossReferencesAsync(Arg<PathArgs>(req).Path, ct).ConfigureAwait(false));

            case RpcOp.DeleteDevice:
            {
                var a = Arg<DeleteDeviceArgs>(req);
                return J(await e.DeleteDeviceAsync(a.ProjectPath, a.DeviceName, ct).ConfigureAwait(false));
            }

            case RpcOp.DeleteModule:
            {
                var a = Arg<DeleteModuleArgs>(req);
                return J(await e.DeleteModuleAsync(a.ProjectPath, a.DeviceName, a.ModuleName, ct).ConfigureAwait(false));
            }

            case RpcOp.DeleteSubnet:
            {
                var a = Arg<DeleteSubnetArgs>(req);
                return J(await e.DeleteSubnetAsync(a.ProjectPath, a.SubnetName, ct).ConfigureAwait(false));
            }

            case RpcOp.GetOnlineStatus:
                return J(await e.GetOnlineStatusAsync(Arg<PathArgs>(req).Path, ct).ConfigureAwait(false));

            case RpcOp.ConnectOnline:
                await e.ConnectOnlineAsync(Arg<PathArgs>(req).Path, ct).ConfigureAwait(false);
                return null;

            case RpcOp.DisconnectOnline:
                await e.DisconnectOnlineAsync(Arg<PathArgs>(req).Path, ct).ConfigureAwait(false);
                return null;

            case RpcOp.Download:
            {
                var a = Arg<DownloadArgs>(req);
                await e.DownloadAsync(a.DevicePath, a.Scope, ct).ConfigureAwait(false);
                return null;
            }

            case RpcOp.PlcRun:
                await e.PlcRunAsync(Arg<PathArgs>(req).Path, ct).ConfigureAwait(false);
                return null;

            case RpcOp.PlcStop:
                await e.PlcStopAsync(Arg<PathArgs>(req).Path, ct).ConfigureAwait(false);
                return null;

            case RpcOp.GetProjectStatus:
                return J(await e.GetProjectStatusAsync(Arg<PathArgs>(req).Path, ct).ConfigureAwait(false));

            case RpcOp.SaveProject:
                return J(await e.SaveProjectAsync(Arg<PathArgs>(req).Path, ct).ConfigureAwait(false));

            case RpcOp.SaveProjectAs:
            {
                var a = Arg<SaveProjectAsArgs>(req);
                return J(await e.SaveProjectAsAsync(a.ProjectPath, a.TargetDirectory, a.TargetName, a.Rebind, ct).ConfigureAwait(false));
            }

            case RpcOp.CreateProject:
            {
                var a = Arg<CreateProjectArgs>(req);
                return J(await e.CreateProjectAsync(a.SessionPath, a.ProjectDirectory, a.ProjectName, a.Author, a.Comment, ct).ConfigureAwait(false));
            }

            case RpcOp.ArchiveProject:
            {
                var a = Arg<ArchiveProjectArgs>(req);
                return J(await e.ArchiveProjectAsync(a.ProjectPath, a.ArchiveDirectory, a.ArchiveName, a.Mode, a.SaveBeforeArchive, ct).ConfigureAwait(false));
            }

            case RpcOp.CloseProject:
            {
                var a = Arg<CloseProjectArgs>(req);
                return J(await e.CloseProjectAsync(a.ProjectPath, a.SaveBeforeClose, ct).ConfigureAwait(false));
            }

            case RpcOp.SearchEquipmentCatalog:
            {
                var a = Arg<SearchCatalogArgs>(req);
                return J(await e.SearchEquipmentCatalogAsync(a.ScopePath, a.Query, ct).ConfigureAwait(false));
            }

            case RpcOp.AddNetworkDevice:
            {
                var a = Arg<AddNetworkDeviceArgs>(req);
                return J(await e.AddNetworkDeviceAsync(a.ProjectPath, a.TypeIdentifier, a.DeviceName, a.DeviceItemName, ct).ConfigureAwait(false));
            }

            case RpcOp.ConfigureNetworkDevice:
            {
                var a = Arg<ConfigureNetworkDeviceArgs>(req);
                return J(await e.ConfigureNetworkDeviceAsync(a.ProjectPath, a.DeviceName, a.IpAddress, a.SubnetMask, a.PnDeviceName, a.SubnetName, a.IoSystemName, ct).ConfigureAwait(false));
            }

            case RpcOp.ConfigureCpuMemory:
            {
                var a = Arg<ConfigureCpuMemoryArgs>(req);
                return J(await e.ConfigureCpuMemoryAsync(a.DevicePath, a.EnableSystemMemory, a.SystemMemoryByte, a.EnableClockMemory, a.ClockMemoryByte, ct).ConfigureAwait(false));
            }

            case RpcOp.AddModule:
            {
                var a = Arg<AddModuleArgs>(req);
                return J(await e.AddModuleAsync(a.ProjectPath, a.DeviceName, a.TypeIdentifier, a.Slot, a.ModuleName, ct).ConfigureAwait(false));
            }

            case RpcOp.ReadHardwareConfig:
                return J(await e.ReadHardwareConfigAsync(Arg<PathArgs>(req).Path, ct).ConfigureAwait(false));

            case RpcOp.ImportUdt:
            {
                var a = Arg<ImportXmlArgs>(req);
                return J(await e.ImportUdtAsync(a.PlcPath, a.SourceXml, ct).ConfigureAwait(false));
            }

            case RpcOp.ImportTagTable:
            {
                var a = Arg<ImportXmlArgs>(req);
                return J(await e.ImportTagTableAsync(a.PlcPath, a.SourceXml, ct).ConfigureAwait(false));
            }

            case RpcOp.GenerateBlocksFromSource:
            {
                var a = Arg<GenerateBlocksArgs>(req);
                return J(await e.GenerateBlocksFromSourceAsync(a.PlcPath, a.SourceName, a.SourceText, ct).ConfigureAwait(false));
            }

            case RpcOp.CreateGroup:
            {
                var a = Arg<CreateGroupArgs>(req);
                return J(await e.CreateGroupAsync(a.PlcPath, a.GroupKind, a.GroupName, ct).ConfigureAwait(false));
            }

            case RpcOp.OpenLibrary:
            {
                var a = Arg<OpenLibraryArgs>(req);
                return J(await e.OpenLibraryAsync(a.LibraryPath, a.ReadOnly, ct).ConfigureAwait(false));
            }

            case RpcOp.ListMasterCopies:
                return J(await e.ListMasterCopiesAsync(Arg<PathArgs>(req).Path, ct).ConfigureAwait(false));

            case RpcOp.CreateBlockFromCopy:
            {
                var a = Arg<CreateBlockFromCopyArgs>(req);
                return J(await e.CreateBlockFromCopyAsync(a.PlcPath, a.LibraryName, a.MasterCopyName, ct).ConfigureAwait(false));
            }

            default:
                throw new InvalidOperationException("Unknown op: " + req.Op);
        }
    }

    private static T Arg<T>(RpcRequest req)
    {
        if (string.IsNullOrEmpty(req.ArgsJson))
        {
            throw new InvalidOperationException("Missing args for op '" + req.Op + "'.");
        }

        return JsonSerializer.Deserialize<T>(req.ArgsJson!, Json)
               ?? throw new InvalidOperationException("Bad args for op '" + req.Op + "'.");
    }

    /// <summary>Serialize a result object to the ResultJson payload (null for void results).</summary>
    private static string? J(object? value) =>
        value is null ? null : JsonSerializer.Serialize(value, value.GetType(), Json);

    /// <summary>
    /// Redirect <see cref="Console.Error"/> through a tee that also appends to a per-process log file
    /// under <c>%TEMP%\tiamcp-worker-logs</c>. Each worker (re)spawn gets its own pid-stamped file so
    /// concurrent/old workers never clobber each other. Best-effort: a failure here falls back to plain
    /// stderr and never breaks the worker.
    /// </summary>
    private static void SetupStderrLog()
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "tiamcp-worker-logs");
            Directory.CreateDirectory(dir);
            var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            var file = Path.Combine(dir, "worker-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + pid + ".log");
            var sw = new StreamWriter(file, append: true) { AutoFlush = true };
            Console.SetError(new TeeTextWriter(Console.Error, sw));
            Console.Error.WriteLine("[worker] stderr log: " + file);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[worker] could not open stderr log file (continuing on plain stderr): " + ex.Message);
        }
    }

    /// <summary>
    /// Writes everything to the original stderr AND to a log file (timestamped + flushed per line). TIA
    /// raises Notification/Confirmation events on its own threads, so writes are serialized under a lock.
    /// </summary>
    private sealed class TeeTextWriter : TextWriter
    {
        private readonly TextWriter _console;
        private readonly StreamWriter _file;
        private readonly object _gate = new object();

        public TeeTextWriter(TextWriter console, StreamWriter file)
        {
            _console = console;
            _file = file;
        }

        public override Encoding Encoding => _console.Encoding;

        public override void WriteLine(string value)
        {
            lock (_gate)
            {
                _console.WriteLine(value);
                _file.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + value);
            }
        }

        public override void Write(string value)
        {
            lock (_gate)
            {
                _console.Write(value);
                _file.Write(value);
            }
        }

        public override void Write(char value)
        {
            lock (_gate)
            {
                _console.Write(value);
                _file.Write(value);
            }
        }

        public override void Flush()
        {
            lock (_gate)
            {
                _console.Flush();
                _file.Flush();
            }
        }
    }

    /// <summary>
    /// Load the Siemens.Engineering.* API DLLs from the TIA install (Copy-Local=False, not in the GAC
    /// here). Loading them from inside the TIA tree is mandatory: Siemens' OpennessLocationProvider
    /// inspects the loaded assembly's location and refuses ("ensure Copy Local is set to false") if it
    /// was copied elsewhere. Once Base loads from here, the provider resolves the runtime impl
    /// (Contract etc., under Portal V21\Bin\PublicAPI) on its own. Override the install via
    /// TIA_MCP_INSTALL_DIR; it must match the path the worker csproj compiled against.
    /// </summary>
    private static void RegisterSiemensAssemblyResolver()
    {
        var install = ResolveTiaInstallDir();
        var apiDir = Path.Combine(install, "PublicAPI", "V21", "net48");
        Console.Error.WriteLine("[worker] TIA install: " + install);

        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            var shortName = new AssemblyName(args.Name).Name;
            if (shortName is null || !shortName.StartsWith("Siemens.Engineering", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var dll = Path.Combine(apiDir, shortName + ".dll");
            return File.Exists(dll) ? Assembly.LoadFrom(dll) : null;
        };
    }

    /// <summary>
    /// Locate the local TIA Portal V21 install so we can load Siemens.Engineering.* from inside the
    /// TIA tree (required by OpennessLocationProvider). Resolution order: explicit
    /// <c>TIA_MCP_INSTALL_DIR</c> env override, then the conventional Program Files locations on the
    /// system drive and D:. Throws a clear error listing what was checked if nothing is valid, instead
    /// of silently picking a wrong default — a wrong path surfaces later as an opaque security exception.
    /// </summary>
    private static string ResolveTiaInstallDir()
    {
        var checkedPaths = new List<string>();

        var env = Environment.GetEnvironmentVariable("TIA_MCP_INSTALL_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            checkedPaths.Add(env + " (TIA_MCP_INSTALL_DIR)");
            if (IsValidTiaInstall(env))
            {
                return env;
            }
        }

        foreach (var candidate in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Siemens", "Automation", "Portal V21"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Siemens", "Automation", "Portal V21"),
            @"C:\Program Files\Siemens\Automation\Portal V21",
            @"D:\Program Files\Siemens\Automation\Portal V21",
        })
        {
            if (string.IsNullOrEmpty(candidate))
            {
                continue;
            }

            checkedPaths.Add(candidate);
            if (IsValidTiaInstall(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "TIA Portal V21 was not found. Install TIA Portal V21 (with Openness enabled) on this " +
            "machine, or set the TIA_MCP_INSTALL_DIR environment variable to the install directory " +
            "(it must contain 'PublicAPI\\V21\\net48\\Siemens.Engineering.Base.dll'). " +
            "Checked: " + string.Join("; ", checkedPaths));
    }

    private static bool IsValidTiaInstall(string dir) =>
        !string.IsNullOrWhiteSpace(dir)
        && File.Exists(Path.Combine(dir, "PublicAPI", "V21", "net48", "Siemens.Engineering.Base.dll"));
}
