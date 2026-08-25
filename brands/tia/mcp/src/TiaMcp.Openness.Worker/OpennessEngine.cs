using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text;
using Microsoft.Win32;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.CrossReference;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Library;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using TiaMcp.Contract;
// Siemens.Engineering.SW.Blocks also defines a BlockType enum; alias the unqualified name to our
// Contract enum so method signatures match ITiaBackend and the worker builds.
using BlockType = TiaMcp.Contract.BlockType;
// Disambiguate from our TiaMcp.Contract.CatalogEntry DTO (same simple name).
using CatalogEntrySdk = Siemens.Engineering.HW.HardwareCatalog.CatalogEntry;

namespace TiaMcp.Openness.Worker;

/// <summary>
/// Real TIA Portal Openness engine (target TIA <b>V21</b>). Hosts a single <c>TiaPortal</c> and serves
/// the path-addressed <see cref="ITiaBackend"/> operations. The worker reads ONE request line at a
/// time, so access is inherently serial — a single <c>TiaPortal</c> is not thread-safe.
/// <para>
/// API is verified against Siemens's own XML docs + github.com/siemens/tia-portal-openness-code-snippets
/// (see docs/P3-openness-notes.md). Real download is wired via <c>DownloadProvider</c> and needs a
/// reachable PLC plus a configured online connection. V21 Openness exposes no PLC online-state query
/// and no RUN/STOP API, so <see cref="GetOnlineStatusAsync"/> surfaces that limitation and
/// connect/disconnect/run/stop throw <see cref="NotSupportedException"/>. This class contains NO
/// conditional compile: the worker project always links the Siemens SDK.
/// </para>
/// </summary>
public sealed class OpennessEngine : ITiaBackend, IDisposable
{
    public TiaBackendKind Kind => TiaBackendKind.Openness;

    public string TiaVersion => "V21";

    private const string SessionId = "s-openness";
    private static readonly string SessionRoot = "session:" + SessionId;

    private TiaPortal? _portal;
    private readonly Dictionary<string, Project> _openProjects =
        new Dictionary<string, Project>(StringComparer.OrdinalIgnoreCase);
    // Global libraries opened this session (by library name), for master-copy reuse.
    private readonly Dictionary<string, UserGlobalLibrary> _openLibraries =
        new Dictionary<string, UserGlobalLibrary>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Dispose the TiaPortal when the worker's stdin closes. Without this, the headless Portal process
    /// can outlive the worker (zombie). Idempotent + never throws (called from Program on shutdown).
    /// </summary>
    public void Dispose()
    {
        var portal = _portal;
        _portal = null;
        _openLibraries.Clear();
        try
        {
            portal?.Dispose();
        }
        catch
        {
            // ignore — best-effort cleanup on shutdown
        }
    }

    // ============================== session / project ==============================

    public Task<TiaStatus> GetStatusAsync(CancellationToken ct)
    {
        // TiaAvailable means "TIA V21 Openness is INSTALLED on this machine" — a stable fact probed from
        // the registry, independent of whether a session is currently open. The old behavior returned
        // IsPortalAlive() here, which is false before tia_connect (_portal is null), so tia_status
        // falsely reported "unavailable" on a fully-installed machine and agents aborted prematurely.
        // OpenSessions still reflects live sessions (0 before connect or after the portal dies).
        IsPortalAlive(); // clears stale refs + cached projects if the handle has died
        return Task.FromResult(new TiaStatus(
            Backend: "Openness",
            TiaVersion: TiaVersion,
            AccessMode: "ReadWrite",
            TiaAvailable: _tiaInstalled,
            OpenSessions: _portal is null ? 0 : _openProjects.Count));
    }

    /// <summary>Probe once at construction whether TIA V21 Openness is registered on this machine
    /// (the net48 PublicAPI key Siemens's own discovery uses). Cached — install state doesn't change
    /// mid-process. Drives the <see cref="TiaStatus.TiaAvailable"/> flag.</summary>
    private static readonly bool _tiaInstalled = ProbeTiaInstalled();

    private static bool ProbeTiaInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Siemens\Automation\Openness\21.0\PublicAPI\21.0.0.0\net48");
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    public Task<SessionInfo> ConnectAsync(ConnectRequest request, CancellationToken ct)
    {
        EnsurePortal(request);
        return Task.FromResult(new SessionInfo(
            SessionId, request.Mode, SessionRoot, _openProjects.Keys.ToArray()));
    }

    /// <summary>Release the TIA Portal session: close any open projects, drop cached state, and dispose
    /// the (headless) Portal so its ~2 GB process exits — without killing the worker. The worker stays
    /// alive for the next <see cref="ConnectAsync"/>, which re-spawns a fresh Portal via EnsurePortal.
    /// Gives agents a clean "release memory mid-session" instead of OS-level process kills. Idempotent.</summary>
    public Task DisconnectAsync(CancellationToken ct)
    {
        var portal = _portal;
        _portal = null;
        _openProjects.Clear();
        _openLibraries.Clear();
        try { portal?.Dispose(); }
        catch { /* best-effort — a half-dead portal still frees on Dispose */ }
        return Task.CompletedTask;
    }

    public Task<ProjectInfo> OpenProjectAsync(string sessionPath, OpenProjectRequest request, CancellationToken ct)
    {
        EnsurePortal(new ConnectRequest(request.Visible ? "interactive" : "headless"));

        Project project;
        if (Path.GetExtension(request.Path).StartsWith(".ap", StringComparison.OrdinalIgnoreCase))
        {
            // Open takes a FileInfo of the .ap1x project FILE (not a directory). It rejects relative
            // paths, so normalize against the worker's CWD (an agent may pass a relative path);
            // pre-check existence so a typo yields our error, not TIA's vague Open() failure.
            var fullPath = Path.GetFullPath(request.Path);
            if (!File.Exists(fullPath))
            {
                throw NotFound("project file", fullPath);
            }
            project = _portal!.Projects.Open(new FileInfo(fullPath));
        }
        else
        {
            // Bare project NAME: match a project already open in this Portal (the attach case)
            // instead of treating it as a filesystem path and failing with a misleading
            // 'file not found'.
            project = _portal!.Projects.FirstOrDefault(p =>
                          string.Equals(p.Name, request.Path, StringComparison.OrdinalIgnoreCase))
                      ?? throw new FileNotFoundException(
                          "No project named '" + request.Path + "' is open in this Portal, and no .ap1x " +
                          "path was given. Pass the full path to the .ap21 file to open a project, or the " +
                          "name of one already open.");
        }

        _openProjects[project.Name] = project;
        return Task.FromResult(new ProjectInfo(
            project.Name, SessionRoot + "/project:" + project.Name, "TiaMcp", TiaVersion));
    }

    // ============================== project lifecycle (P5) ==============================

    public Task<ProjectStatus> GetProjectStatusAsync(string projectPath, CancellationToken ct)
    {
        var project = ResolveProject(projectPath) ?? throw NotFound("project", projectPath);
        return Task.FromResult(ReadProjectStatus(project));
    }

    public Task<ProjectLifecycleResult> SaveProjectAsync(string projectPath, CancellationToken ct)
    {
        var project = ResolveProject(projectPath) ?? throw NotFound("project", projectPath);
        project.Save();
        return Task.FromResult(LifecycleResult("save_project", project));
    }

    public Task<ProjectLifecycleResult> SaveProjectAsAsync(
        string projectPath, string targetDirectory, string targetName, bool rebind, CancellationToken ct)
    {
        var project = ResolveProject(projectPath) ?? throw NotFound("project", projectPath);
        var copyDir = Path.Combine(targetDirectory, targetName);
        // Commit any pending changes first: a freshly-created or dirty project can make SaveAs block
        // indefinitely in headless mode (TIA waits on an internal save/reorganize step that never
        // resolves). Saving first gives SaveAs a clean, settled project to copy.
        project.Save();
        project.SaveAs(new DirectoryInfo(copyDir));

        // The copied project file lives somewhere under copyDir; locate the .ap1x file.
        var copiedPath = Directory.Exists(copyDir)
            ? Directory.GetFiles(copyDir, "*.ap??", SearchOption.AllDirectories).FirstOrDefault()
            : null;

        if (rebind)
        {
            if (string.IsNullOrWhiteSpace(copiedPath))
            {
                throw new InvalidOperationException(
                    "Could not locate a copied TIA project file under '" + copyDir + "'.");
            }

            _openProjects.Remove(project.Name);
            project.Close();
            var reopened = _portal!.Projects.Open(new FileInfo(copiedPath!));
            _openProjects[reopened.Name] = reopened;
            return Task.FromResult(LifecycleResult("save_project_as", reopened));
        }

        // SaveAs MOVES the project object: its Path now points at the copy. Keeping the stale
        // handle registered under the old name makes every later op (save/archive/status) act on
        // the CLONE while reporting the old name, and holds the copy's directory lock open so a
        // second Portal cannot open it ("already been opened by user …"). Drop the handle and
        // close it — the documented clean route is to explicitly open source/copy afterwards.
        _openProjects.Remove(project.Name);
        project.Close();
        var result = LifecycleResult("save_project_as", project);
        return Task.FromResult(result with { ProjectPath = copiedPath ?? copyDir });
    }

    public Task<ProjectLifecycleResult> CreateProjectAsync(
        string sessionPath, string projectDirectory, string projectName,
        string? author, string? comment, CancellationToken ct)
    {
        EnsurePortal(new ConnectRequest("interactive"));
        if (_portal is null)
        {
            throw new InvalidOperationException("No TIA Portal session is connected.");
        }

        Project project;
        if (string.IsNullOrWhiteSpace(author) && string.IsNullOrWhiteSpace(comment))
        {
            project = _portal.Projects.Create(new DirectoryInfo(projectDirectory), projectName);
        }
        else
        {
            // Projects.Create has no author/comment overload; go through IEngineeringComposition.Create
            // with a keyword list (same trick Czarnak/tia-portal-mcp uses).
            var createParams = new List<KeyValuePair<string, object>>
            {
                new("TargetDirectory", (object)new DirectoryInfo(projectDirectory)),
                new("Name", projectName),
            };
            if (!string.IsNullOrWhiteSpace(author))
            {
                createParams.Add(new("Author", author!));
            }
            if (!string.IsNullOrWhiteSpace(comment))
            {
                createParams.Add(new("Comment", comment!));
            }

            project = ((IEngineeringComposition)_portal.Projects).Create(typeof(Project), createParams) as Project
                      ?? throw new InvalidOperationException(
                          "TIA Portal did not return a project after creating '" + projectName + "'.");
        }

        _openProjects[project.Name] = project;
        return Task.FromResult(LifecycleResult("create_project", project));
    }

    public Task<ProjectLifecycleResult> ArchiveProjectAsync(
        string projectPath, string archiveDirectory, string archiveName,
        ArchiveMode mode, bool saveBeforeArchive, CancellationToken ct)
    {
        var project = ResolveProject(projectPath) ?? throw NotFound("project", projectPath);
        if (saveBeforeArchive)
        {
            project.Save();
        }
        // Live-verified 2026-08-23: TIA's Archive writes the file under EXACTLY this name — no
        // extension is appended — so a bare name lands as an extension-less file. Pin .zap1x.
        if (!archiveName.EndsWith(".zap1x", StringComparison.OrdinalIgnoreCase))
        {
            archiveName += ".zap1x";
        }
        project.Archive(new DirectoryInfo(archiveDirectory), archiveName, MapArchiveMode(mode));
        var result = LifecycleResult("archive_project", project);
        return Task.FromResult(result with
        {
            ProjectPath = Path.Combine(Path.GetFullPath(archiveDirectory), archiveName),
        });
    }

    public Task<ProjectLifecycleResult> CloseProjectAsync(string projectPath, bool saveBeforeClose, CancellationToken ct)
    {
        var name = PathSegment(projectPath, "project");

        ProjectStatus status;
        try
        {
            var project = ResolveProject(projectPath) ?? throw NotFound("project", projectPath);
            status = ReadProjectStatus(project);
            if (saveBeforeClose)
            {
                project.Save();
            }
            project.Close();
        }
        catch (EngineeringObjectDisposedException)
        {
            // The project object is already disposed — V21's Project.SaveAs closes it, or it was
            // closed elsewhere. From the caller's view the project IS closed, so report success
            // instead of surfacing the internal ObjectDisposed error.
            if (!string.IsNullOrEmpty(name))
            {
                _openProjects.Remove(name!);
            }
            return Task.FromResult(new ProjectLifecycleResult(
                "close_project", null,
                new ProjectStatus(false, name, null, null, null, null, null, null, null, null)));
        }

        if (!string.IsNullOrEmpty(name))
        {
            _openProjects.Remove(name!);
        }
        return Task.FromResult(new ProjectLifecycleResult(
            "close_project", status.Path, status with { IsOpen = false }));
    }

    private ProjectLifecycleResult LifecycleResult(string operation, Project project)
    {
        var s = ReadProjectStatus(project);
        return new ProjectLifecycleResult(operation, s.Path, s);
    }

    private static ProjectStatus ReadProjectStatus(Project project) => new(
        IsOpen: true,
        Name: SafeString(() => project.Name),
        Path: SafeString(() => project.Path?.FullName),
        Version: SafeString(() => project.Version),
        Author: SafeString(() => project.Author),
        IsModified: SafeValue(() => project.IsModified),
        CreationTime: SafeValue(() => project.CreationTime),
        LastModified: SafeValue(() => project.LastModified),
        LastModifiedBy: SafeString(() => project.LastModifiedBy),
        Size: SafeValue(() => project.Size));

    private static ProjectArchivationMode MapArchiveMode(ArchiveMode mode) => mode switch
    {
        ArchiveMode.DiscardRestorableData => ProjectArchivationMode.DiscardRestorableData,
        ArchiveMode.Compressed => ProjectArchivationMode.Compressed,
        ArchiveMode.DiscardRestorableDataAndCompressed => ProjectArchivationMode.DiscardRestorableDataAndCompressed,
        _ => ProjectArchivationMode.None,
    };

    private static string? SafeString(Func<string?> read)
    {
        try { return read(); }
        catch (EngineeringException) { return null; }
    }

    private static T? SafeValue<T>(Func<T> read) where T : struct
    {
        try { return read(); }
        catch (EngineeringException) { return null; }
    }

    public Task<IReadOnlyList<TargetInfo>> ListTargetsAsync(string projectPath, CancellationToken ct)
    {
        var project = ResolveProject(projectPath) ?? throw NotFound("project", projectPath);
        // Scan EVERY device (top-level stations + ungrouped/grouped slaves), not just project.Devices:
        // real projects often file even the PLC/HMI stations under UngroupedDevicesGroup / DeviceGroups,
        // where a project.Devices-only scan finds nothing. Classify by the device's own software; pure
        // IO slaves / drives (no PLC or HMI software) are skipped so they are not mislabeled as HMI.
        var prefix = SessionRoot + "/project:" + project.Name + "/device:";
        var targets = new List<TargetInfo>();
        foreach (var d in EnumerateAllDevices(project))
        {
            var kind = GetPlcSoftware(d) is not null
                ? TargetKind.Plc
                : IsHmiDevice(d) ? TargetKind.Hmi : (TargetKind?)null;
            if (kind is null)
            {
                continue;
            }
            targets.Add(new TargetInfo(d.Name, kind.Value, d.TypeIdentifier, prefix + d.Name));
        }
        return Task.FromResult<IReadOnlyList<TargetInfo>>(targets);
    }

    public Task<CompileResult> CompileAsync(string scopePath, CompileMode mode, CancellationToken ct)
    {
        var software = ResolvePlcSoftware(scopePath) ?? throw NotFound("PLC", scopePath);
        // Compile the PLC via the ICompilable service on its block group.
        var compiler = software.BlockGroup.GetService<ICompilable>()
                       ?? throw NotFound("compilable target", scopePath);

        var timer = Stopwatch.StartNew();
        var result = RunCompile(compiler);
        timer.Stop();

        // Siemens compile results are a TREE (PLC -> per-block -> per-network/issue); the real message
        // text lives in the leaf nodes, so flatten recursively — a top-level-only read drops every actual
        // error/warning and returns blank messages (the whole point of error transparency).
        var diags = new List<CompileDiagnostic>();
        CollectDiagnostics(result.Messages, scopePath, diags);

        return Task.FromResult(new CompileResult(
            mode,
            // 0 errors is a successful compile; warnings are normal and must NOT read as failure (the
            // compiler's State is Warning whenever any warning exists, even with zero errors).
            Success: result.ErrorCount == 0,
            result.ErrorCount,
            result.WarningCount,
            diags,
            timer.ElapsedMilliseconds));
    }

    /// <summary>Depth-first flatten of the compiler result message tree. Emits a diagnostic for every node
    /// that carries text (Description); the Path on each node locates the offending block/network.</summary>
    private static void CollectDiagnostics(
        CompilerResultMessageComposition messages, string scopePath, List<CompileDiagnostic> into)
    {
        foreach (CompilerResultMessage m in messages)
        {
            var desc = m.Description ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(desc))
            {
                into.Add(new CompileDiagnostic(MapSeverity(m.State), null, desc, m.Path ?? scopePath, null));
            }

            CollectDiagnostics(m.Messages, scopePath, into);
        }
    }

    // ============================== blocks: read ==============================

    public Task<ListBlocksResult> ListBlocksAsync(
        string scopePath, BlockType? filter, int limit, int offset, CancellationToken ct)
    {
        var software = ResolvePlcSoftware(scopePath) ?? throw NotFound("PLC", scopePath);
        // Real projects organise blocks into user groups (BlockGroup.Groups), so a top-level-only scan
        // (software.BlockGroup.Blocks) misses everything filed under a group. Recurse the whole tree.
        var blocks = EnumerateBlocks(software.BlockGroup);
        if (filter is not null)
        {
            blocks = blocks.Where(b => BlockTypeOf(b) == filter.Value);
        }

        var all = blocks.ToArray();
        var page = all
            .Skip(Math.Max(0, offset))
            .Take(limit <= 0 ? 50 : Math.Min(limit, 500))
            .Select(b => ToBlockInfo(b, scopePath))
            .ToArray();
        return Task.FromResult(new ListBlocksResult(
            scopePath, all.Length, Math.Max(0, offset), page.Length, page));
    }

    public Task<BlockInfo> GetBlockAsync(string blockPath, CancellationToken ct)
    {
        var block = ResolveBlock(blockPath) ?? throw NotFound("block", blockPath);
        return Task.FromResult(ToBlockInfo(block, ParentPath(blockPath)));
    }

    public Task<BlockSource> ReadBlockSourceAsync(string blockPath, CancellationToken ct)
    {
        var software = ResolvePlcSoftware(blockPath) ?? throw NotFound("PLC", blockPath);
        var block = ResolveBlock(blockPath) ?? throw NotFound("block", blockPath);

        // PlcBlock.Export writes SimaticML XML (there is no direct SCL-text getter). It can fail two ways:
        //  - EngineeringTargetInvocationException ("Inconsistent blocks/UDT cannot be exported"): a compile
        //    reconciles block/UDT versions, then retry.
        //  - a transient IOException when the previous temp-file handle lingers — ExportToText reuses one
        //    fixed file name per block, and a stale handle makes the overwrite throw. We saw exactly this
        //    on OB exports (FC exports to the same worker state succeeded).
        // So: try once with the shared name; on ANY failure, compile + retry with a fresh unique file.
        string xml;
        try
        {
            xml = ExportToText(block, unique: false);
        }
        catch (Exception)
        {
            // Reconcile inconsistent block/UDT versions, then retry the export. Route through RunCompile
            // so a portal-killing NonRecoverableException here drops the dead handle + reports clearly too.
            if (software.BlockGroup.GetService<ICompilable>() is { } recompiler)
            {
                RunCompile(recompiler);
            }
            xml = ExportToText(block, unique: true); // rethrows if it still genuinely fails
        }

        return Task.FromResult(new BlockSource(blockPath, "XML/SimaticML", xml));
    }

    public Task<ExportResult> ExportBlockAsync(
        string blockPath, ExportFormat format, string? outDir, CancellationToken ct)
    {
        // Resolve a block first; if none has this name, fall back to a UDT (PlcType) of the same name
        // (block and type names are each program-wide unique in TIA, so this stays unambiguous). This
        // lets tia_block_export dump a UDT's raw SimaticML — there is no other export path for UDTs.
        var block = ResolveBlock(blockPath);
        var type = block is null ? ResolveType(blockPath) : null;
        if (block is null && type is null) throw NotFound("block or type", blockPath);

        var dir = string.IsNullOrWhiteSpace(outDir)
            ? Path.Combine(Path.GetTempPath(), "tiamcp-export")
            : Path.GetFullPath(outDir); // worker CWD is its own bin folder; TIA Export rejects relative paths
        Directory.CreateDirectory(dir);
        // Siemens' Export/GenerateSource both REFUSE to overwrite an existing file (they throw rather
        // than replace). Clear any stale file from a previous run so re-export is idempotent.
        var name = block?.Name ?? type!.Name;
        var ext = format == ExportFormat.SclSource ? ".scl" : ".xml";
        var file = Path.Combine(dir, name + ext);
        if (File.Exists(file)) File.Delete(file);

        // UDTs have no SCL-text form; they only export as SimaticML XML (via PlcType.Export).
        if (type is not null)
        {
            if (format == ExportFormat.SclSource)
                throw new ArgumentException("UDTs cannot be exported as SCL source; use format=Xml.");
            file = ExportToFileWithRecovery(blockPath, file, f => type.Export(f, ExportOptions.WithDefaults));
        }
        else if (format == ExportFormat.SclSource)
        {
            // SCL-text export is a one-way generation via the external-source group (not block.Export,
            // which only writes SimaticML XML). Official docs: GenerateSource only supports SCL/STL
            // blocks (plus DBs / PLC data types) — anything else (LAD/FBD/GRAPH) must go via format=Xml,
            // the only channel that covers every language. Guard up front so the agent gets a clear
            // error instead of a deep Siemens exception.
            if (block!.ProgrammingLanguage is not (ProgrammingLanguage.SCL or ProgrammingLanguage.STL))
                throw new ArgumentException(
                    $"Block '{block.Name}' is {block.ProgrammingLanguage}; SclSource export supports SCL/STL only " +
                    "(official docs: ExternalSources text export is SCL/STL-only). Use format=Xml — SimaticML XML " +
                    "round-trips every language, and tia_block_read_code renders LAD/GRAPH bodies compactly.");
            var software = ResolvePlcSoftware(blockPath) ?? throw NotFound("PLC", blockPath);
            software.ExternalSourceGroup.GenerateSource(new[] { block! }, new FileInfo(file), GenerateOptions.None);
        }
        else
        {
            file = ExportToFileWithRecovery(blockPath, file, f => block!.Export(f, ExportOptions.WithDefaults));
        }
        var bytes = (int)new FileInfo(file).Length;
        return Task.FromResult(new ExportResult(blockPath, format, file, bytes));
    }

    /// <summary>Export a block/type to a file, recovering from a checksum-inconsistent object the same
    /// way <see cref="ReadBlockSourceAsync"/> does: on failure, compile the PLC to reconcile block/UDT
    /// versions, then retry with a fresh unique file name (a stale handle on the original name can also
    /// make the overwrite throw). Returns the path actually written (the retry uses a unique suffix).
    /// Without this, tia_block_export throws EngineeringTargetInvocationException on inconsistent blocks
    /// while tia_block_read_source (which has the same recovery) succeeds.</summary>
    private string ExportToFileWithRecovery(string scopePath, string file, Action<FileInfo> export)
    {
        try
        {
            export(new FileInfo(file));
            return file;
        }
        catch (Exception)
        {
            if (ResolvePlcSoftware(scopePath)?.BlockGroup.GetService<ICompilable>() is { } recompiler)
            {
                RunCompile(recompiler);
            }
            if (File.Exists(file)) File.Delete(file);
            var dir = Path.GetDirectoryName(file) ?? "";
            var baseName = Path.GetFileNameWithoutExtension(file);
            var ext = Path.GetExtension(file);
            var retry = Path.Combine(dir, baseName + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ext);
            export(new FileInfo(retry)); // rethrows if it still genuinely fails
            return retry;
        }
    }

    // ============================== blocks: write ==============================

    public Task<string> ImportBlockAsync(string plcPath, CreateBlockRequest request, CancellationToken ct)
    {
        var software = ResolvePlcSoftware(plcPath) ?? throw NotFound("PLC", plcPath);
        // Openness imports SimaticML XML (not raw SCL). request.Source is either inline XML or a
        // path to an existing .xml file on disk (lets agents import exported blocks without inlining).
        var file = ResolveSourceFile(request.Source);
        if (file is null)
        {
            file = Path.Combine(Path.GetTempPath(), request.Name + ".xml");
            File.WriteAllText(file, request.Source);
        }
        // Target a block subgroup when the path carries a `blockgroup:NAME` segment; else the root
        // block folder.
        var target = ResolveBlockGroup(plcPath, software);
        target.Blocks.Import(new FileInfo(file), ImportOptions.Override);
        return Task.FromResult(plcPath + "/block:" + request.Name);
    }

    public Task DeleteBlockAsync(string blockPath, CancellationToken ct)
    {
        var block = ResolveBlock(blockPath);
        if (block is not null)
        {
            block.Delete();
            return Task.CompletedTask;
        }

        // Fall back to a UDT (PlcType) of the same name, mirroring ExportBlockAsync/ReadInterfaceAsync.
        var type = ResolveType(blockPath) ?? throw NotFound("block or type", blockPath);
        type.Delete();
        return Task.CompletedTask;
    }

    // ============================== tags (TagTableGroup; verified API) ==============================

    public Task<ListTagsResult> ListTagsAsync(string scopePath, int limit, int offset, CancellationToken ct)
    {
        var software = ResolvePlcSoftware(scopePath) ?? throw NotFound("PLC", scopePath);
        var plcPath = PlcPathFor(scopePath);

        // Honor a `tagtable:NAME` scope segment: read only that one table (recursing user groups,
        // since a table may live in a subgroup). With no segment, read every table recursively, so
        // tables filed in subgroups are not silently skipped (parity with ListTagTablesAsync).
        var tableName = PathSegment(scopePath, "tagtable");
        IEnumerable<PlcTagTable> tables = !string.IsNullOrEmpty(tableName)
            ? new[] { ResolveTagTable(software, tableName) ?? throw NotFound("tag table", scopePath) }
            : EnumerateTagTables(software.TagTableGroup);

        var tags = new List<TagInfo>();
        string? firstTable = null;
        foreach (var table in tables)
        {
            firstTable ??= table.Name;
            var tablePath = plcPath + "/tagtable:" + table.Name;
            foreach (PlcTag tag in table.Tags)
            {
                tags.Add(ToTagInfo(tag, tablePath));
            }
        }

        var page = tags
            .Skip(Math.Max(0, offset))
            .Take(limit <= 0 ? 50 : Math.Min(limit, 500))
            .ToArray();
        var label = plcPath + "/tagtable:" + (firstTable ?? "Default");
        return Task.FromResult(new ListTagsResult(label, tags.Count, Math.Max(0, offset), page.Length, page));
    }

    public Task<string> CreateTagAsync(string tagTablePath, CreateTagRequest request, CancellationToken ct)
    {
        var software = ResolvePlcSoftware(tagTablePath) ?? throw NotFound("PLC / tag table", tagTablePath);
        var tableName = PathSegment(tagTablePath, "tagtable");

        PlcTagTable table;
        if (!string.IsNullOrEmpty(tableName))
        {
            // "Default" aliases the real projects' "Default tag table" (see ResolveTagTable).
            table = ResolveTagTable(software, tableName) ?? throw NotFound("tag table", tagTablePath);
        }
        else
        {
            // Default to the first tag table, creating "Default" if none exists yet.
            table = software.TagTableGroup.TagTables.FirstOrDefault()
                    ?? software.TagTableGroup.TagTables.Create("Default");
        }

        var dataType = string.IsNullOrWhiteSpace(request.DataType) ? "Bool" : request.DataType;
        var address = request.Address ?? string.Empty;
        // Create(name, dataTypeName, address); empty address => auto.
        var tag = table.Tags.Create(request.Name, dataType, address);
        // Live-verified 2026-08-23: the comment parameter used to be silently dropped — new tags
        // carry one empty MultilingualTextItem per project language, so setting text = picking
        // the en-US item (fallback: first item) and assigning .Text.
        SetMultilingualText(tag.Comment, request.Comment);
        return Task.FromResult(PlcPathFor(tagTablePath) + "/tagtable:" + table.Name + "/tag:" + request.Name);
    }

    public Task DeleteTagAsync(string tagPath, CancellationToken ct)
    {
        var software = ResolvePlcSoftware(tagPath) ?? throw NotFound("PLC", tagPath);
        var tableName = PathSegment(tagPath, "tagtable");
        var tagName = PathSegment(tagPath, "tag");
        if (string.IsNullOrEmpty(tableName) || string.IsNullOrEmpty(tagName))
        {
            throw NotFound("tag", tagPath);
        }

        var table = ResolveTagTable(software, tableName) ?? throw NotFound("tag table", tagPath);
        var tag = table.Tags.Find(tagName) ?? throw NotFound("tag", tagPath);
        tag.Delete();
        return Task.CompletedTask;
    }

    // ============================== device items / online (V21 Openness; see docs/P3-openness-notes.md) ==============================
    //
    // V21 has NO IPlcWebApi / GoOnline / CPU OperatingState API — the V19+ model is provider-based:
    // hardware is enumerated via DeviceItem, and the only real "go online" action is DownloadProvider.
    // So device items + download are wired to the real API; online-state/connect/disconnect/run/stop
    // report the SDK boundary honestly (NotSupportedException + reason) instead of a NotImplemented stub.

    public Task<IReadOnlyList<DeviceItemInfo>> ListDeviceItemsAsync(string devicePath, CancellationToken ct)
    {
        var device = ResolveDevice(devicePath) ?? throw NotFound("device", devicePath);
        var items = new List<DeviceItemInfo>();
        foreach (var item in EnumerateDeviceItems(device))
        {
            items.Add(ToDeviceItemInfo(item, devicePath));
        }
        return Task.FromResult<IReadOnlyList<DeviceItemInfo>>(items);
    }

    // ============================== hardware catalog / network provisioning (P6) ==============================

    public Task<IReadOnlyList<CatalogEntry>> SearchEquipmentCatalogAsync(
        string scopePath, string query, CancellationToken ct)
    {
        var results = new List<CatalogEntry>();
        if (_portal is null)
        {
            throw new InvalidOperationException("No TIA Portal session is connected. Call tia_connect first.");
        }
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult<IReadOnlyList<CatalogEntry>>(results);
        }

        var q = query.Trim();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Verified V21 path: TiaPortal.HardwareCatalog.Find(query) returns CatalogEntry objects.
            foreach (CatalogEntrySdk entry in _portal.HardwareCatalog.Find(q))
            {
                var ti = ReadProp(entry, "TypeIdentifier");
                if (string.IsNullOrEmpty(ti))
                {
                    continue;
                }

                var name = ReadProp(entry, "Name");
                var path = ReadProp(entry, "CatalogPath");
                var article = ReadProp(entry, "ArticleNumber") ?? ReadProp(entry, "OrderNumber");
                var desc = ReadProp(entry, "Description");
                if (!Contains(name, q) && !Contains(ti, q) && !Contains(article, q) && !Contains(desc, q))
                {
                    continue;
                }

                if (!seen.Add(ti!))
                {
                    continue;
                }

                results.Add(new CatalogEntry(name, article, ReadProp(entry, "Version"), ti!, path, desc));
            }
        }
        catch (EngineeringException ex)
        {
            Console.Error.WriteLine("[catalog] HardwareCatalog.Find failed: " + ex.Message);
        }

        return Task.FromResult<IReadOnlyList<CatalogEntry>>(results);
    }

    public Task<AddDeviceResult> AddNetworkDeviceAsync(
        string projectPath, string typeIdentifier, string deviceName, string deviceItemName, CancellationToken ct)
    {
        var project = ResolveProject(projectPath) ?? throw NotFound("project", projectPath);
        var warnings = new List<string>();

        var device = TryCreateDevice(project, typeIdentifier, deviceName, deviceItemName, warnings);
        if (device is null)
        {
            return Task.FromResult(new AddDeviceResult(
                deviceName, deviceItemName, typeIdentifier, warnings));
        }

        // V21 quirk (live-verified 2026-08-25): Devices.CreateWithItem(typeId, name, deviceItemName)
        // names the STATION with the 3rd arg and the root CPU item with the 2nd — the reverse of the
        // plain reading of our tool params. Every later call addresses the station as
        // <project>/device:<deviceName>, and the historically verified calls (P3 notes) always
        // passed identical names, hiding the swap. Force the station name to match deviceName;
        // if TIA refuses the rename, the warning says so and the result still reports the real name.
        try
        {
            if (!string.Equals(SafeStr(() => device.Name), deviceName, StringComparison.OrdinalIgnoreCase))
            {
                device.Name = deviceName;
            }
        }
        catch (EngineeringException ex)
        {
            warnings.Add("TIA named the station '" + SafeStr(() => device.Name) +
                         "' (rename to '" + deviceName + "' refused): " + ex.Message);
        }

        var name = SafeStr(() => device.Name, deviceName);
        var ti = SafeStr(() => device.TypeIdentifier, typeIdentifier);
        var rootItem = deviceItemName;
        foreach (DeviceItem item in device.DeviceItems)
        {
            rootItem = SafeStr(() => item.Name, rootItem);
            break;
        }
        return Task.FromResult(new AddDeviceResult(name, rootItem, ti, warnings));
    }

    public Task<AddModuleResult> AddModuleAsync(
        string projectPath, string deviceName, string typeIdentifier, int? slot, string? moduleName, CancellationToken ct)
    {
        // Verified V21: HardwareObject.PlugNew(typeIdentifier, name, positionNumber) creates + plugs a
        // device item. Device/DeviceItem both inherit HardwareObject. positionNumber 65535 = "auto-pick
        // next free slot" (Siemens code-snippets HardwareSnippets.cs). Plug into the rack (rail).
        var project = ResolveProject(projectPath) ?? throw NotFound("project", projectPath);
        var device = FindDeviceByName(project, deviceName) ?? throw NotFound("device", deviceName);

        // The rack/rail carries signal & communication modules. Match on TypeIdentifier "System:Rack"
        // (per the snippet), falling back to a Rail-named top-level item.
        DeviceItem? rack = null;
        foreach (DeviceItem top in device.DeviceItems)
        {
            var ti = SafeStr(() => top.TypeIdentifier);
            if (ti.IndexOf("System:Rack", StringComparison.OrdinalIgnoreCase) >= 0 ||
                SafeStr(() => top.Name).IndexOf("Rail", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                rack = top;
                break;
            }
        }
        if (rack is null)
        {
            throw new InvalidOperationException("Device '" + deviceName + "' has no rack/rail to plug modules into.");
        }

        var name = moduleName ?? string.Empty;

        // Resolve the slot. An explicit slot is pre-checked as-is; for auto-slot we don't trust the
        // 65535 sentinel alone (it's rejected on some racks, e.g. compact CPUs) — we probe the rack's
        // real free slots and take the first TIA will actually accept. CanPlug pre-checks also surface
        // a clean reason instead of a raw EngineeringException on a bad slot/type.
        int positionNumber;
        if (slot.HasValue)
        {
            positionNumber = slot.Value;
            if (!CanPlugChecked(rack, typeIdentifier, name, positionNumber))
            {
                throw new InvalidOperationException(
                    "TIA Portal will not plug '" + typeIdentifier + "' into " + deviceName + " slot " + slot.Value +
                    " — incompatible module, occupied slot, or wrong rack. Try a different slot or module.");
            }
        }
        else
        {
            positionNumber = ResolveAutoSlot(rack, typeIdentifier, name);
            if (positionNumber < 0)
            {
                throw new InvalidOperationException(
                    "TIA Portal will not plug '" + typeIdentifier + "' into " + deviceName +
                    " (no free rack slot accepts this module — incompatible module or full rack). " +
                    "Try a different module or pass an explicit slot.");
            }
        }

        DeviceItem module;
        try
        {
            module = rack.PlugNew(typeIdentifier, name, positionNumber);
        }
        catch (EngineeringException ex)
        {
            throw new InvalidOperationException(
                "TIA Portal could not plug module '" + (moduleName ?? typeIdentifier) + "' into '" +
                deviceName + "': " + ex.Message, ex);
        }

        var finalName = SafeStr(() => module.Name, moduleName ?? typeIdentifier);
        var finalSlot = SafeInt(() => (int)module.PositionNumber);
        var devicePath = projectPath + "/device:" + deviceName;
        return Task.FromResult(new AddModuleResult(
            deviceName, finalName, typeIdentifier, finalSlot, devicePath + "/item:" + finalName));
    }

    public Task<ConfigureNetworkDeviceResult> ConfigureNetworkDeviceAsync(
        string projectPath, string deviceName, string? ipAddress, string? subnetMask,
        string? pnDeviceName, string? subnetName, string? ioSystemName, CancellationToken ct)
    {
        var project = ResolveProject(projectPath) ?? throw NotFound("project", projectPath);
        var applied = new Dictionary<string, string>();
        var skipped = new Dictionary<string, string>();
        var messages = new List<string>();

        var device = FindDeviceByName(project, deviceName)
                     ?? throw NotFound("device", deviceName);
        var ni = FindNetworkInterface(device)
                 ?? throw new InvalidOperationException("Device '" + deviceName + "' has no network interface.");
        var node = FirstNode(ni)
                   ?? throw new InvalidOperationException("Device '" + deviceName + "' network interface has no node.");

        SetNodeAttribute(node, "Address", ipAddress, applied, skipped);
        SetNodeAttribute(node, "SubnetMask", subnetMask, applied, skipped);
        SetNodeAttribute(node, "PnDeviceName", pnDeviceName, applied, skipped);

        // Subnet: connect to an existing subnet by name, or create+connect one if none by that name
        // (verified V21: Node.CreateAndConnectToSubnet — replaces the old "must already exist" behavior).
        if (!string.IsNullOrWhiteSpace(subnetName))
        {
            ConnectSubnet(project, node, subnetName!, applied, skipped);
        }

        // IO system: make this PROFINET interface an IO controller, creating the IO system if needed
        // (verified V21: NetworkInterface.IoControllers -> IoController.CreateIoSystem). Requires the
        // node to be on a subnet first, so this normally follows a subnetName above.
        if (!string.IsNullOrWhiteSpace(ioSystemName))
        {
            ConnectIoSystem(ni, node, ioSystemName!, applied, skipped);
        }

        if (applied.Count == 0 && skipped.Count == 0)
        {
            messages.Add("No network settings were provided.");
        }

        return Task.FromResult(new ConfigureNetworkDeviceResult(deviceName, applied, skipped, messages));
    }

    public Task<CpuMemoryConfig> ConfigureCpuMemoryAsync(
        string devicePath, bool? enableSystemMemory, long? systemMemoryByte,
        bool? enableClockMemory, long? clockMemoryByte, CancellationToken ct)
    {
        var device = ResolveDevice(devicePath) ?? throw NotFound("device", devicePath);
        var cpu = FindCpuItem(device) ?? throw NotFound("CPU device item", devicePath);

        // S7-1200 CPUs do not expose System/Clock memory via Openness — GetAttribute throws
        // EngineeringNotSupportedException for these names (S7-1500 returns the bool). Detect it so we
        // give a clear message instead of silently returning false/0, which would wrongly imply the
        // feature is off when it is in fact configured (e.g. %MB0/%MB1 in the tag table).
        try { ((IEngineeringObject)cpu).GetAttribute("SystemMemoryByte"); }
        catch (EngineeringNotSupportedException)
        {
            throw new InvalidOperationException(
                "System/clock memory is not accessible via Openness on this CPU. This is a known Siemens " +
                "limitation: the SystemMemoryByte / ClockMemoryByte attributes are exposed on S7-1500 but " +
                "throw EngineeringNotSupportedException on S7-1200. The bytes may still be configured in the " +
                "project — verify on the CPU's 'System and clock memory' page in the TIA GUI.");
        }

        var curSysEn = AttrBool(cpu, "SystemMemoryByte");
        var curSysAddr = AttrLong(cpu, "SystemMemoryByteAddress");
        var curClkEn = AttrBool(cpu, "ClockMemoryByte");
        var curClkAddr = AttrLong(cpu, "ClockMemoryByteAddress");

        if (enableSystemMemory.HasValue)
            ((IEngineeringObject)cpu).SetAttribute("SystemMemoryByte", enableSystemMemory.Value);
        if (systemMemoryByte.HasValue)
            ((IEngineeringObject)cpu).SetAttribute("SystemMemoryByteAddress", (ulong)systemMemoryByte.Value);
        if (enableClockMemory.HasValue)
            ((IEngineeringObject)cpu).SetAttribute("ClockMemoryByte", enableClockMemory.Value);
        if (clockMemoryByte.HasValue)
            ((IEngineeringObject)cpu).SetAttribute("ClockMemoryByteAddress", (ulong)clockMemoryByte.Value);

        return Task.FromResult(new CpuMemoryConfig(
            devicePath,
            enableSystemMemory ?? curSysEn,
            systemMemoryByte ?? curSysAddr,
            enableClockMemory ?? curClkEn,
            clockMemoryByte ?? curClkAddr));
    }

    public Task<HardwareConfig> ReadHardwareConfigAsync(string projectPath, CancellationToken ct)
    {
        var project = ResolveProject(projectPath) ?? throw NotFound("project", projectPath);
        // project.Devices only holds grouped/top-level stations; PROFINET/PROFIBUS IO slaves (GSD
        // devices) live under UngroupedDevicesGroup, so a project.Devices-only scan misses every slave.
        var allDevices = EnumerateAllDevices(project);

        var devices = new List<HwDevice>();
        foreach (Device d in allDevices)
        {
            try
            {
                devices.Add(new HwDevice(
                    SafeStr(() => d.Name), SafeStr(() => d.TypeIdentifier), ReadHwItems(d.DeviceItems)));
            }
            catch (EngineeringException ex)
            {
                Console.Error.WriteLine("[hwconfig] skip device: " + ex.Message);
            }
        }

        // Build subnet membership from the DEVICE side. Subnet.Nodes/IoSystems are not reliably populated
        // on the read path, but every node, IO controller and IO connector is reachable via a device's
        // NetworkInterface (verified V21 — mirrors how tia_network_configure builds the network). Walk
        // every device (incl. ungrouped slaves), naming each by its head DeviceItem (the friendly station
        // name, e.g. "OP10-Load") rather than the project device name (e.g. "GSD device_51").
        var nodesBySubnet = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        // IO systems keyed by subnet+name so a duplicated IO-system name on another subnet can't merge.
        var ioSystems = new Dictionary<string, IoSysAcc>(StringComparer.OrdinalIgnoreCase);

        foreach (Device d in allDevices)
        {
            var devName = SafeStr(() => d.Name);
            foreach (DeviceItem item in EnumerateDeviceItems(d))
            {
                NetworkInterface? ni;
                try { ni = item.GetService<NetworkInterface>(); }
                catch { continue; }
                if (ni is null) { continue; }
                var friendly = FriendlyDeviceName(item, devName);

                try
                {
                    foreach (Node node in ni.Nodes)
                    {
                        Subnet? sub = null;
                        try { sub = node.ConnectedSubnet; } catch { /* unconnected node */ }
                        if (sub is null) { continue; }
                        var addr = SafeAttr(node, "Address");
                        var label = friendly + (string.IsNullOrWhiteSpace(addr) ? "" : " @ " + addr);
                        Bucket(nodesBySubnet, SafeStr(() => sub.Name)).Add(label);
                    }
                }
                catch (EngineeringException ex) { Console.Error.WriteLine("[hwconfig] nodes: " + ex.Message); }

                // This interface OWNS an IO system if it is an IO controller (ioController.IoSystem).
                try
                {
                    foreach (IoController ioc in ni.IoControllers)
                    {
                        IoSystem? iosys = null;
                        try { iosys = ioc.IoSystem; } catch { /* controller without a system */ }
                        if (iosys is null) { continue; }
                        IoAcc(ioSystems, iosys).Controller = friendly;
                    }
                }
                catch (EngineeringException ex) { Console.Error.WriteLine("[hwconfig] iocontrollers: " + ex.Message); }

                // This interface is a SLAVE on an IO system if it has an IoConnector connected to one.
                try
                {
                    foreach (IoConnector conn in ni.IoConnectors)
                    {
                        IoSystem? iosys = null;
                        try { iosys = conn.ConnectedToIoSystem; } catch { /* unconnected connector */ }
                        if (iosys is null) { continue; }
                        IoAcc(ioSystems, iosys).Connected.Add(friendly);
                    }
                }
                catch (EngineeringException ex) { Console.Error.WriteLine("[hwconfig] ioconnectors: " + ex.Message); }
            }
        }

        var subnets = new List<HwSubnet>();
        foreach (Subnet s in project.Subnets)
        {
            try
            {
                var sname = SafeStr(() => s.Name);
                var type = SafeStr(() => s.NetType.ToString());
                var nodes = nodesBySubnet.TryGetValue(sname, out var nl) ? nl : new List<string>();
                var ios = ioSystems.Values
                    .Where(a => string.Equals(a.SubnetName, sname, StringComparison.OrdinalIgnoreCase))
                    .Select(a => new HwIoSystem(a.Name, a.Controller, a.Connected))
                    .ToList();
                subnets.Add(new HwSubnet(sname, string.IsNullOrEmpty(type) ? null : type, nodes, ios));
            }
            catch (EngineeringException ex)
            {
                Console.Error.WriteLine("[hwconfig] skip subnet: " + ex.Message);
            }
        }

        return Task.FromResult(new HardwareConfig(devices, subnets));
    }

    // ============================== #3: structured interface read ==============================

    public Task<InterfaceInfo> ReadInterfaceAsync(string path, CancellationToken ct)
    {
        var software = ResolvePlcSoftware(path);
        string xml;
        string name;
        string blockType;

        // A block path resolves a block; if no block has that name, fall back to a UDT of the same name
        // (block names and type names are program-wide unique in TIA, so this stays unambiguous).
        var block = ResolveBlock(path);
        if (block is not null)
        {
            name = block.Name;
            blockType = BlockTypeOf(block).ToString();
            xml = ExportXmlWithRetry(() => ExportToText(block, unique: true), software);
        }
        else
        {
            var type = ResolveType(path) ?? throw NotFound("block or type", path);
            name = type.Name;
            blockType = "UDT";
            xml = ExportXmlWithRetry(() =>
            {
                var file = Path.Combine(Path.GetTempPath(), "udt_" + Guid.NewGuid().ToString("N") + ".xml");
                type.Export(new FileInfo(file), ExportOptions.WithDefaults);
                return File.ReadAllText(file);
            }, software);
        }

        return Task.FromResult(new InterfaceInfo(path, name, blockType, ParseInterfaceSections(xml)));
    }

    /// <summary>Export to SimaticML XML, compiling once and retrying on failure. A freshly imported /
    /// edited block or UDT is "inconsistent" until compiled and Export throws; a compile reconciles it
    /// (same recovery as ReadBlockSourceAsync).</summary>
    private string ExportXmlWithRetry(Func<string> export, PlcSoftware? software)
    {
        try
        {
            return export();
        }
        catch (Exception)
        {
            if (software?.BlockGroup.GetService<ICompilable>() is { } recompiler)
            {
                RunCompile(recompiler);
            }

            return export(); // rethrows if it still genuinely fails
        }
    }

    /// <summary>Parse the SimaticML &lt;Interface&gt;/&lt;Sections&gt; into a structured section/member tree.
    /// Namespace-robust (matches by local name, since the interface ns version drifts across releases).</summary>
    private static IReadOnlyList<InterfaceSection> ParseInterfaceSections(string xml)
    {
        var doc = XDocument.Parse(xml);
        var sections = new List<InterfaceSection>();
        foreach (var sec in doc.Descendants().Where(e => e.Name.LocalName == "Section"))
        {
            var secName = sec.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value ?? string.Empty;
            sections.Add(new InterfaceSection(secName, ParseMembers(sec)));
        }
        return sections;
    }

    private static IReadOnlyList<InterfaceMember> ParseMembers(XElement parent)
    {
        var members = new List<InterfaceMember>();
        foreach (var m in parent.Elements().Where(e => e.Name.LocalName == "Member"))
        {
            var nm = m.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value ?? string.Empty;
            var dt = m.Attributes().FirstOrDefault(a => a.Name.LocalName == "Datatype")?.Value ?? string.Empty;
            var start = m.Elements().FirstOrDefault(e => e.Name.LocalName == "StartValue")?.Value;
            var comment = m.Elements().FirstOrDefault(e => e.Name.LocalName == "Comment")
                ?.Descendants().FirstOrDefault(e => e.Name.LocalName == "MultiLanguageText")?.Value;
            members.Add(new InterfaceMember(nm, dt, start, comment, ParseMembers(m))); // recurse nested struct members
        }
        return members;
    }

    // ============================== #4: cross references ==============================

    public Task<CrossRefResult> GetCrossReferencesAsync(string path, CancellationToken ct)
    {
        // Blocks AND tags carry the cross-reference service. A tag's where-used answers "which
        // blocks read/write me" — exactly the dependency view a tag deletion needs to preview.
        var entries = new List<XRefEntry>();
        if (!string.IsNullOrEmpty(PathSegment(path, "tag")))
        {
            var software = ResolvePlcSoftware(path) ?? throw NotFound("PLC", path);
            var tableName = PathSegment(path, "tagtable");
            var table = ResolveTagTable(software, tableName);
            var tag = table?.Tags.Find(PathSegment(path, "tag")) ?? throw NotFound("tag", path);
            var tagSvc = tag.GetService<CrossReferenceService>()
                         ?? throw new NotSupportedException("Cross-reference service unavailable for this tag.");
            CollectXRefs(tagSvc.GetCrossReferences(CrossReferenceFilter.AllObjects).Sources, entries);
            return Task.FromResult(new CrossRefResult(path, entries.Count, entries));
        }

        var block = ResolveBlock(path) ?? throw NotFound("block", path);
        var svc = block.GetService<CrossReferenceService>()
                  ?? throw new NotSupportedException("Cross-reference service unavailable for this object.");
        var result = svc.GetCrossReferences(CrossReferenceFilter.AllObjects);

        CollectXRefs(result.Sources, entries);
        return Task.FromResult(new CrossRefResult(path, entries.Count, entries));
    }

    /// <summary>Flatten the cross-reference tree (SourceObject -> References -> Locations), recursing the
    /// nested SourceObject children. Each referenced object becomes one entry carrying its use locations.</summary>
    private static void CollectXRefs(SourceObjectComposition sources, List<XRefEntry> into)
    {
        foreach (SourceObject so in sources)
        {
            foreach (ReferenceObject r in so.References)
            {
                var locs = new List<XRefLocation>();
                foreach (Location loc in r.Locations)
                {
                    locs.Add(new XRefLocation(
                        SafeStr(() => loc.ReferenceType.ToString()),
                        SafeStr(() => loc.Access.ToString()),
                        SafeStr(() => loc.Address),
                        SafeStr(() => loc.ReferenceLocation)));
                }

                into.Add(new XRefEntry(
                    SafeStr(() => r.Name), SafeStr(() => r.Path), SafeStr(() => r.TypeName), SafeStr(() => r.Address), locs));
            }

            CollectXRefs(so.Children, into);
        }
    }

    // ============================== #5: hardware deletes ==============================

    public Task<string> DeleteDeviceAsync(string projectPath, string deviceName, CancellationToken ct)
    {
        var project = ResolveProject(projectPath) ?? throw NotFound("project", projectPath);
        var dev = FindDeviceByName(project, deviceName) ?? throw NotFound("device", deviceName);
        dev.Delete();
        return Task.FromResult(deviceName);
    }

    public Task<string> DeleteModuleAsync(string projectPath, string deviceName, string moduleName, CancellationToken ct)
    {
        var project = ResolveProject(projectPath) ?? throw NotFound("project", projectPath);
        var dev = FindDeviceByName(project, deviceName) ?? throw NotFound("device", deviceName);
        var item = EnumerateDeviceItems(dev).FirstOrDefault(
            i => string.Equals(SafeStr(() => i.Name), moduleName, StringComparison.OrdinalIgnoreCase))
            ?? throw NotFound("module", moduleName);
        item.Delete(); // throws if the item is not deletable (e.g. a fixed built-in submodule)
        return Task.FromResult(moduleName);
    }

    public Task<string> DeleteSubnetAsync(string projectPath, string subnetName, CancellationToken ct)
    {
        var project = ResolveProject(projectPath) ?? throw NotFound("project", projectPath);
        Subnet? subnet = null;
        foreach (Subnet s in project.Subnets)
        {
            if (string.Equals(SafeStr(() => s.Name), subnetName, StringComparison.OrdinalIgnoreCase))
            {
                subnet = s;
                break;
            }
        }

        (subnet ?? throw NotFound("subnet", subnetName)).Delete();
        return Task.FromResult(subnetName);
    }

    /// <summary>Resolve a UDT (PlcType) by the path's <c>block:</c> segment name, recursing type groups.</summary>
    private PlcType? ResolveType(string path)
    {
        var software = ResolvePlcSoftware(path);
        if (software is null)
        {
            return null;
        }

        var name = PathSegment(path, "block");
        return string.IsNullOrEmpty(name) ? null : FindType(software.TypeGroup, name!);
    }

    private static PlcType? FindType(PlcTypeGroup group, string name)
    {
        if (group.Types.Find(name) is { } hit)
        {
            return hit;
        }

        foreach (PlcTypeUserGroup sub in group.Groups)
        {
            if (FindType(sub, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Resolve the type subgroup named in the path's <c>typegroup:</c> segment (recursing
    /// nested user groups); the root <see cref="PlcTypeGroup"/> when the path has no such segment;
    /// or throws if a segment names a group that does not exist.</summary>
    private PlcTypeGroup ResolveTypeGroup(string path, PlcSoftware software)
    {
        var name = PathSegment(path, "typegroup");
        if (string.IsNullOrEmpty(name))
        {
            return software.TypeGroup;
        }

        return FindTypeGroup(software.TypeGroup, name!) ?? throw NotFound("type group", name!);
    }

    private static PlcTypeUserGroup? FindTypeGroup(PlcTypeGroup group, string name)
    {
        foreach (PlcTypeUserGroup sub in group.Groups)
        {
            if (string.Equals(sub.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return sub;
            }

            if (FindTypeGroup(sub, name) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    // ============================== #6: enumerate UDTs / tag tables ==============================

    public Task<ListUdtsResult> ListUdtsAsync(string scopePath, CancellationToken ct)
    {
        var software = ResolvePlcSoftware(scopePath) ?? throw NotFound("PLC", scopePath);
        var plc = PlcPathFor(scopePath);
        var list = new List<UdtInfo>();
        CollectUdts(software.TypeGroup, "", plc, list);
        return Task.FromResult(new ListUdtsResult(plc, list.Count, list));
    }

    private static void CollectUdts(PlcTypeGroup group, string groupPath, string plc, List<UdtInfo> into)
    {
        foreach (PlcType t in group.Types)
        {
            into.Add(new UdtInfo(t.Name, groupPath, plc + "/block:" + t.Name));
        }

        foreach (PlcTypeUserGroup sub in group.Groups)
        {
            var childPath = string.IsNullOrEmpty(groupPath) ? sub.Name : groupPath + "/" + sub.Name;
            CollectUdts(sub, childPath, plc, into);
        }
    }

    public Task<ListTagTablesResult> ListTagTablesAsync(string scopePath, CancellationToken ct)
    {
        var software = ResolvePlcSoftware(scopePath) ?? throw NotFound("PLC", scopePath);
        var plc = PlcPathFor(scopePath);
        var list = new List<TagTableInfo>();
        CollectTagTables(software.TagTableGroup, "", plc, list);
        return Task.FromResult(new ListTagTablesResult(plc, list.Count, list));
    }

    private static void CollectTagTables(PlcTagTableGroup group, string groupPath, string plc, List<TagTableInfo> into)
    {
        foreach (PlcTagTable t in group.TagTables)
        {
            var n = 0;
            foreach (PlcTag _ in t.Tags) { n++; }
            into.Add(new TagTableInfo(t.Name, n, groupPath, plc + "/tagtable:" + t.Name));
        }

        foreach (PlcTagTableUserGroup sub in group.Groups)
        {
            var childPath = string.IsNullOrEmpty(groupPath) ? sub.Name : groupPath + "/" + sub.Name;
            CollectTagTables(sub, childPath, plc, into);
        }
    }

    /// <summary>Depth-first enumeration of every tag table in a group and its nested user groups —
    /// the read twin of <see cref="CollectTagTables"/> — so <see cref="ListTagsAsync"/> does not
    /// silently skip tables filed in subgroups.</summary>
    private static IEnumerable<PlcTagTable> EnumerateTagTables(PlcTagTableGroup group)
    {
        foreach (PlcTagTable t in group.TagTables)
        {
            yield return t;
        }
        foreach (PlcTagTableUserGroup sub in group.Groups)
        {
            foreach (var t in EnumerateTagTables(sub))
            {
                yield return t;
            }
        }
    }

    public Task<ExportResult> ExportTagTableAsync(string tagTablePath, string? outDir, CancellationToken ct)
    {
        // The reverse of ImportTagTableAsync: resolve a single tag table by name (recursing user groups,
        // since a table may live in a subgroup), then Export its SimaticML XML. Tag-table names are
        // program-wide unique, so a recursive find-by-name is unambiguous.
        var software = ResolvePlcSoftware(tagTablePath) ?? throw NotFound("PLC", tagTablePath);
        var name = PathSegment(tagTablePath, "tagtable");
        var table = ResolveTagTable(software, name);
        if (table is null) throw NotFound("tag table", tagTablePath);

        var dir = string.IsNullOrWhiteSpace(outDir)
            ? Path.Combine(Path.GetTempPath(), "tiamcp-export")
            : Path.GetFullPath(outDir); // worker CWD is its own bin folder; TIA Export rejects relative paths
        Directory.CreateDirectory(dir);
        // Siemens Export REFUSES to overwrite an existing file (throws); clear stale output so
        // re-export is idempotent (mirrors ExportBlockAsync).
        var file = Path.Combine(dir, table.Name + ".xml");
        if (File.Exists(file)) File.Delete(file);
        table.Export(new FileInfo(file), ExportOptions.WithDefaults);
        var bytes = (int)new FileInfo(file).Length;
        return Task.FromResult(new ExportResult(tagTablePath, ExportFormat.Xml, file, bytes));
    }

    /// <summary>Resolve a tag table from a path segment: recursive find by name, with the "Default"
    /// short alias for real projects' "Default tag table" (live-verified 2026-08-23/25). Every
    /// by-name tag-table lookup must go through this — creation used to accept the alias while
    /// list/delete/export/cross-ref didn't, splitting the create→read round-trip.</summary>
    private static PlcTagTable? ResolveTagTable(PlcSoftware software, string? name)
        => string.IsNullOrEmpty(name)
            ? null
            : FindTagTable(software.TagTableGroup, name!)
              ?? (name!.Equals("Default", StringComparison.OrdinalIgnoreCase)
                  ? FindTagTable(software.TagTableGroup, "Default tag table")
                  : null);

    /// <summary>Depth-first find of a tag table by name across the tag-table group and all nested user
    /// groups (tables can live in subgroups, not just the root). Case-insensitive.</summary>
    private static PlcTagTable? FindTagTable(PlcTagTableGroup group, string name)
    {
        foreach (PlcTagTable t in group.TagTables)
        {
            if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) return t;
        }
        foreach (PlcTagTableUserGroup sub in group.Groups)
        {
            var found = FindTagTable(sub, name);
            if (found is not null) return found;
        }
        return null;
    }

    // ============================== P7: import / source / groups / library ==============================

    public Task<ImportResult> ImportUdtAsync(string plcPath, string sourceXml, CancellationToken ct)
    {
        var software = ResolvePlcSoftware(plcPath) ?? throw NotFound("PLC", plcPath);
        var before = SnapshotTypeNames(software);
        // sourceXml is either inline SimaticML XML, or a path to an existing .xml file on disk (lets
        // an agent import exported/extracted UDT files without inlining the content).
        var file = ResolveSourceFile(sourceXml) ?? WriteTempXml("udt", sourceXml);
        // Target a type subgroup when the path carries a `typegroup:NAME` segment; otherwise the root
        // type folder. Verified: Import takes SimaticML XML on the group's PlcTypeComposition.
        var target = ResolveTypeGroup(plcPath, software);
        // Importing into a subgroup is a MOVE: TIA rejects creating a type whose name exists
        // project-wide, so first delete any same-named types wherever they currently live.
        if (!ReferenceEquals(target, software.TypeGroup))
        {
            foreach (var typeName in ParseTypeNames(file))
            {
                if (FindType(software.TypeGroup, typeName) is { } existing)
                {
                    existing.Delete();
                }
            }
        }
        target.Types.Import(new FileInfo(file), ImportOptions.Override);
        var created = DiffNames(before, SnapshotTypeNames(software));
        var plc = PlcPathFor(plcPath);
        return Task.FromResult(new ImportResult(
            plc, created, "Imported " + created.Count + " UDT(s)" + NameList(created) + "."));
    }

    public Task<ImportResult> ImportTagTableAsync(string plcPath, string sourceXml, CancellationToken ct)
    {
        var software = ResolvePlcSoftware(plcPath) ?? throw NotFound("PLC", plcPath);
        var before = SnapshotTagTableNames(software);
        // sourceXml is either inline SimaticML XML, or a path to an existing .xml file on disk — same
        // rule as block/UDT import (verified live 2026-08-23: passing a path used to fail with
        // "Data at the root level is invalid" because the path was written to disk AS the XML).
        var file = ResolveSourceFile(sourceXml) ?? WriteTempXml("tagtable", sourceXml);
        software.TagTableGroup.TagTables.Import(new FileInfo(file), ImportOptions.Override);
        var created = DiffNames(before, SnapshotTagTableNames(software));
        var plc = PlcPathFor(plcPath);
        return Task.FromResult(new ImportResult(
            plc, created, "Imported " + created.Count + " tag table(s)" + NameList(created) + "."));
    }

    public Task<ImportResult> GenerateBlocksFromSourceAsync(
        string plcPath, string sourceName, string sourceText, CancellationToken ct)
    {
        var software = ResolvePlcSoftware(plcPath) ?? throw NotFound("PLC", plcPath);
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            sourceName = "Source_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }
        // ExternalSources wants a real file on disk. Default to .scl; callers pass AWL etc. by name.
        var ext = Path.HasExtension(sourceName) ? "" : ".scl";
        var file = Path.Combine(Path.GetTempPath(), sourceName + ext);
        File.WriteAllText(file, sourceText ?? string.Empty, new UTF8Encoding(false));

        var external = software.ExternalSourceGroup.ExternalSources.CreateFromFile(sourceName + ext, file);
        var before = SnapshotBlockNames(software);
        // GenerateBlocksFromSource parses the source and creates/updates the blocks it declares.
        external.GenerateBlocksFromSource();
        var after = SnapshotBlockNames(software);
        var created = DiffNames(before, after);
        // TIA SILENTLY SKIPS declarations whose block already exists (e.g. the project's default OB
        // "Main") — a before/after diff cannot see that. Live-verified 2026-08-25: an OB "Main" + DB
        // source reported "Generated 1 block(s)" with no word about the untouched OB. Parse the
        // declared names so an Applied result never hides a dropped declaration.
        var declared = new List<string>();
        foreach (Match m in Regex.Matches(
                     sourceText ?? string.Empty,
                     "(?:FUNCTION_BLOCK|FUNCTION|ORGANIZATION_BLOCK|DATA_BLOCK|TYPE)\\s+\"([^\"]+)\"",
                     RegexOptions.IgnoreCase))
        {
            var n = m.Groups[1].Value;
            if (!created.Contains(n) && !declared.Contains(n, StringComparer.OrdinalIgnoreCase))
            {
                declared.Add(n);
            }
        }
        var note = string.Empty;
        if (declared.Count > 0)
        {
            var untouched = declared.Where(n => before.Contains(n)).ToList();
            var missing = declared.Where(n => !before.Contains(n)).ToList();
            if (untouched.Count > 0)
            {
                note += " TIA left existing block(s) untouched: " + string.Join(", ", untouched) + ".";
            }
            if (missing.Count > 0)
            {
                note += " Declared but not generated (check syntax): " + string.Join(", ", missing) + ".";
            }
        }
        var plc = PlcPathFor(plcPath);
        return Task.FromResult(new ImportResult(
            plc, created, "Generated " + created.Count + " block(s) from source '" + sourceName + "'"
            + NameList(created) + "." + note));
    }

    public Task<string> CreateGroupAsync(string plcPath, string groupKind, string groupName, CancellationToken ct)
    {
        var software = ResolvePlcSoftware(plcPath) ?? throw NotFound("PLC", plcPath);
        var plc = PlcPathFor(plcPath);
        switch (NormalizeGroupKind(groupKind))
        {
            case "type":
                software.TypeGroup.Groups.Create(groupName);
                return Task.FromResult(plc + "/typegroup:" + groupName);
            case "tagtable":
                software.TagTableGroup.Groups.Create(groupName);
                return Task.FromResult(plc + "/tagtablegroup:" + groupName);
            default:
                software.BlockGroup.Groups.Create(groupName);
                return Task.FromResult(plc + "/blockgroup:" + groupName);
        }
    }

    public Task<LibraryInfo> OpenLibraryAsync(string libraryPath, bool readOnly, CancellationToken ct)
    {
        // A library is opened against a live Portal (no project required). Reuse/attach one.
        EnsurePortal(new ConnectRequest("interactive"));
        var fi = new FileInfo(Path.GetFullPath(libraryPath));
        if (!fi.Exists)
        {
            throw NotFound("library file", fi.FullName);
        }
        // The library may already be open in this Portal (the GUI, or a prior open in the same
        // session) — V21's Open() then throws "Cannot change the open mode of an already open global
        // library". Reuse the already-open instance instead of re-opening.
        var lib = FindOpenGlobalLibrary(fi);
        var alreadyOpen = lib is not null;
        if (lib is null)
        {
            var mode = readOnly ? OpenMode.ReadOnly : OpenMode.ReadWrite;
            lib = _portal!.GlobalLibraries.Open(fi, mode);
        }
        _openLibraries[lib.Name] = lib;
        var copies = new List<MasterCopyInfo>();
        CollectMasterCopies(lib.MasterCopyFolder, "", copies);
        return Task.FromResult(new LibraryInfo(lib.Name, fi.FullName, readOnly && !alreadyOpen, copies.Count));
    }

    public Task<IReadOnlyList<MasterCopyInfo>> ListMasterCopiesAsync(string libraryName, CancellationToken ct)
    {
        var lib = ResolveLibrary(libraryName) ?? throw NotFound("opened library", libraryName);
        var copies = new List<MasterCopyInfo>();
        CollectMasterCopies(lib.MasterCopyFolder, "", copies);
        return Task.FromResult<IReadOnlyList<MasterCopyInfo>>(copies);
    }

    public Task<string> CreateBlockFromCopyAsync(
        string plcPath, string libraryName, string masterCopyName, CancellationToken ct)
    {
        var software = ResolvePlcSoftware(plcPath) ?? throw NotFound("PLC", plcPath);
        var lib = ResolveLibrary(libraryName) ?? throw NotFound("opened library", libraryName);
        var mc = FindMasterCopy(lib.MasterCopyFolder, masterCopyName)
                 ?? throw NotFound("master copy", masterCopyName);
        var before = SnapshotBlockNames(software);
        // Instantiate the master copy as a real block in this PLC's program-block group.
        software.BlockGroup.Blocks.CreateFrom(mc);
        var created = DiffNames(before, SnapshotBlockNames(software));
        var newName = created.Count > 0 ? created[0] : masterCopyName;
        return Task.FromResult(PlcPathFor(plcPath) + "/block:" + newName);
    }

    // ----- P6 helpers -----

    // Create a device, with a GSD head-module suffix fallback. GSD typeIdentifiers
    // ("GSD:GSDML-...XML/<head>") need the correct head-access-point suffix; the right one varies by
    // device. We try the caller's id first, then — only for GSD ids that lack a recognized suffix —
    // retry with the common head suffixes (per the reference generator). Catalog (OrderNumber:) ids
    // are tried once. Returns null and fills warnings if every attempt fails.
    private static Device? TryCreateDevice(
        Project project, string typeIdentifier, string deviceName, string deviceItemName, List<string> warnings)
    {
        var candidates = new List<string> { typeIdentifier };
        var isGsd = typeIdentifier.StartsWith("GSD:", StringComparison.OrdinalIgnoreCase);
        var hasHead = typeIdentifier.IndexOf("/DAP", StringComparison.OrdinalIgnoreCase) >= 0
                      || typeIdentifier.EndsWith("/D", StringComparison.OrdinalIgnoreCase)
                      || typeIdentifier.EndsWith("/SM", StringComparison.OrdinalIgnoreCase)
                      || typeIdentifier.EndsWith("/M", StringComparison.OrdinalIgnoreCase);
        if (isGsd && !hasHead)
        {
            foreach (var suffix in new[] { "/DAP", "/D", "/SM", "/M" })
            {
                candidates.Add(typeIdentifier + suffix);
            }
        }

        foreach (var candidate in candidates)
        {
            try
            {
                return project.Devices.CreateWithItem(candidate, deviceName, deviceItemName);
            }
            catch (EngineeringException ex)
            {
                warnings.Add("CreateWithItem('" + candidate + "') failed: " + ex.Message);
            }
        }
        return null;
    }

    private static Device? FindDeviceByName(Project project, string deviceName)
    {
        // Walk ALL devices (top-level stations + ungrouped IO slaves + nested device groups), so this
        // also resolves PROFINET/PROFIBUS slaves that live under UngroupedDevicesGroup, not just stations.
        var all = EnumerateAllDevices(project);
        // Preferred: exact match on the station/Device name (the canonical target).
        foreach (Device d in all)
        {
            if (string.Equals(SafeStr(() => d.Name), deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return d;
            }
        }
        // Fallback: callers commonly pass the CPU/DeviceItem name (e.g. "PLC_1") instead of the
        // station name. Return the owning Device if any contained DeviceItem matches by name.
        foreach (Device d in all)
        {
            foreach (DeviceItem top in d.DeviceItems)
            {
                if (DeviceItemMatchesName(top, deviceName))
                {
                    return d;
                }
            }
        }
        return null;
    }

    private static bool DeviceItemMatchesName(DeviceItem item, string name)
    {
        if (string.Equals(SafeStr(() => item.Name), name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        foreach (DeviceItem child in item.DeviceItems)
        {
            if (DeviceItemMatchesName(child, name))
            {
                return true;
            }
        }
        return false;
    }

    // True if TIA will plug (typeIdentifier, name) at position; wraps the raw Openness exception
    // into a clean message instead of letting an EngineeringException escape the pre-check.
    private static bool CanPlugChecked(DeviceItem rack, string typeIdentifier, string name, int position)
    {
        try
        {
            return rack.CanPlugNew(typeIdentifier, name, position);
        }
        catch (EngineeringException ex)
        {
            throw new InvalidOperationException("CanPlugNew check failed for '" + typeIdentifier + "': " + ex.Message, ex);
        }
    }

    // Pick a slot for an auto-slot request. The documented 65535 sentinel is tried first (cheapest on
    // racks that support it), then we fall back to probing the rack's reported free slots and return
    // the first one TIA will accept. Returns -1 if nothing fits.
    private static int ResolveAutoSlot(DeviceItem rack, string typeIdentifier, string name)
    {
        const int AutoSlot = 65535;
        if (CanPlugChecked(rack, typeIdentifier, name, AutoSlot))
        {
            return AutoSlot;
        }
        try
        {
            foreach (var loc in rack.GetPlugLocations())
            {
                int pos = (int)loc.PositionNumber;
                if (CanPlugChecked(rack, typeIdentifier, name, pos))
                {
                    return pos;
                }
            }
        }
        catch (EngineeringException) { /* GetPlugLocations unsupported on this rack — give up cleanly */ }
        return -1;
    }

    private static NetworkInterface? FindNetworkInterface(Device device)
    {
        foreach (DeviceItem top in device.DeviceItems)
        {
            var ni = FindNetworkInterface(top);
            if (ni is not null)
            {
                return ni;
            }
        }
        return null;
    }

    private static NetworkInterface? FindNetworkInterface(DeviceItem item)
    {
        try
        {
            if (((IEngineeringServiceProvider)item).GetService<NetworkInterface>() is { } ni)
            {
                return ni;
            }
        }
        catch (EngineeringException) { }

        foreach (DeviceItem child in item.DeviceItems)
        {
            var ni = FindNetworkInterface(child);
            if (ni is not null)
            {
                return ni;
            }
        }
        return null;
    }

    private static Node? FirstNode(NetworkInterface ni)
    {
        foreach (Node n in ni.Nodes)
        {
            return n;
        }
        return null;
    }

    private static void SetNodeAttribute(
        Node node, string attribute, string? value,
        Dictionary<string, string> applied, Dictionary<string, string> skipped)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        try
        {
            ((IEngineeringObject)node).SetAttribute(attribute, value);
            applied[attribute] = value!;
        }
        catch (EngineeringException ex)
        {
            skipped[attribute] = ex.Message;
        }
    }

    private static void ConnectSubnet(
        Project project, Node node, string subnetName,
        Dictionary<string, string> applied, Dictionary<string, string> skipped)
    {
        // Connect to an existing subnet by name; if none exists, create one and connect this node
        // to it (verified V21: Node.CreateAndConnectToSubnet). Replaces the old connect-only behavior
        // that silently skipped subnet creation on a fresh project.
        Subnet? subnet = null;
        foreach (Subnet s in project.Subnets)
        {
            if (string.Equals(s.Name, subnetName, StringComparison.OrdinalIgnoreCase))
            {
                subnet = s;
                break;
            }
        }
        try
        {
            if (subnet is not null)
            {
                node.ConnectToSubnet(subnet);
                applied["SubnetName"] = subnetName + " (existing)";
            }
            else
            {
                var created = node.CreateAndConnectToSubnet(subnetName);
                applied["SubnetName"] = SafeStr(() => created.Name, subnetName) + " (created)";
            }
        }
        catch (Exception ex)
        {
            skipped["SubnetName"] = Unwrap(ex);
        }
    }

    private static void ConnectIoSystem(
        NetworkInterface ni, Node node, string ioSystemName,
        Dictionary<string, string> applied, Dictionary<string, string> skipped)
    {
        // Make this PROFINET interface an IO controller, creating the IO system if it has none yet
        // (verified V21: NetworkInterface.IoControllers -> IoController.CreateIoSystem). Requires the
        // node to already be on a subnet (provide subnetName in the same call).
        IoController? ioController = null;
        try { ioController = ni.IoControllers.FirstOrDefault(); }
        catch (EngineeringException) { }

        if (ioController is null)
        {
            skipped["IoSystemName"] = "The network interface exposes no IO controller (not a PROFINET controller?).";
            return;
        }

        Subnet? subnet = null;
        try { subnet = node.ConnectedSubnet; }
        catch (EngineeringException) { }
        if (subnet is null)
        {
            skipped["IoSystemName"] = "Connect a subnet first (subnetName) before creating an IO system.";
            return;
        }

        try
        {
            if (ioController.IoSystem is { } existing)
            {
                applied["IoSystemName"] = (existing.Name ?? ioSystemName) + " (existing)";
            }
            else
            {
                var created = ioController.CreateIoSystem(ioSystemName);
                applied["IoSystemName"] = (created.Name ?? ioSystemName) + " (created)";
            }
        }
        catch (Exception ex)
        {
            skipped["IoSystemName"] = Unwrap(ex);
        }
    }

    private static void InvokeFirst(object target, IEnumerable<string> methodNames, object argument)
    {
        foreach (var name in methodNames)
        {
            var method = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == name
                    && m.GetParameters() is { Length: 1 } p
                    && p[0].ParameterType.IsInstanceOfType(argument));
            if (method is not null)
            {
                method.Invoke(target, new[] { argument });
                return;
            }
        }
        throw new InvalidOperationException("No supported connection method found on '" + target.GetType().Name + "'.");
    }

    private static List<HwDeviceItem> ReadHwItems(DeviceItemComposition items)
    {
        var result = new List<HwDeviceItem>();
        foreach (DeviceItem item in items)
        {
            try
            {
                var name = SafeStr(() => item.Name);
                var slot = SafeInt(() => (int)item.PositionNumber);
                var addr = SafeAttr(item, "Address");
                result.Add(new HwDeviceItem(name, SafeStr(() => item.TypeIdentifier), slot, addr, ReadHwItems(item.DeviceItems)));
            }
            catch (EngineeringException) { }
        }
        return result;
    }

    private static string? SafeAttr(IEngineeringObject obj, string attribute)
    {
        try { return obj.GetAttribute(attribute)?.ToString(); }
        catch (EngineeringException) { return null; }
    }

    private static int SafeInt(Func<int> read) { try { return read(); } catch (EngineeringException) { return 0; } }

    private static string SafeStr(Func<string> read, string fallback = "")
    {
        try { return read(); }
        catch (EngineeringException) { return fallback; }
    }

    private static bool Contains(string? value, string query) =>
        value?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Append-or-create the list bucketed under <paramref name="key"/>.</summary>
    private static List<T> Bucket<T>(Dictionary<string, List<T>> map, string key)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<T>();
            map[key] = list;
        }
        return list;
    }

    /// <summary>Mutable accumulator for an IO system seen across the device walk: the controller fills in
    /// from the owning interface, slaves append from their IoConnectors — order of discovery is arbitrary.</summary>
    private sealed class IoSysAcc
    {
        public string Name = "";
        public string SubnetName = "";
        public string? Controller;
        public readonly List<string> Connected = new List<string>();
    }

    /// <summary>Get-or-create the accumulator for an IO system, keyed by subnet+name so a duplicated
    /// IO-system name on a different subnet stays a distinct entry.</summary>
    private static IoSysAcc IoAcc(Dictionary<string, IoSysAcc> map, IoSystem iosys)
    {
        var name = SafeStr(() => iosys.Name);
        var subnet = SafeStr(() => iosys.Subnet.Name);
        var key = subnet + " " + name;
        if (!map.TryGetValue(key, out var acc))
        {
            acc = new IoSysAcc { Name = name, SubnetName = subnet };
            map[key] = acc;
        }
        return acc;
    }

    /// <summary>The friendly station name for a network-interface item: its head DeviceItem (the parent —
    /// e.g. "OP10-Load" or the CPU "Demo-OP10"), which is far more useful than the project device name
    /// (e.g. "GSD device_51"). Falls back to <paramref name="fallback"/> when the parent has no name.</summary>
    private static string FriendlyDeviceName(DeviceItem interfaceItem, string fallback)
    {
        var parent = interfaceItem.Parent as DeviceItem;
        var name = parent is null ? null : SafeStr(() => parent.Name);
        return string.IsNullOrWhiteSpace(name) ? fallback : name!;
    }

    /// <summary>Read a property as a string via reflection (catalog entries expose no common interface).
    /// A missing property returns null silently; a present-but-unreadable one is logged to stderr.</summary>
    private static string? ReadProp(object? instance, string propertyName)
    {
        if (instance is null)
        {
            return null;
        }
        try
        {
            return instance.GetType()
                .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(instance)?.ToString();
        }
        catch (TargetInvocationException ex) when (ex.InnerException is EngineeringException eng)
        {
            Console.Error.WriteLine("[reflect] " + instance.GetType().Name + "." + propertyName + " -> " + eng.Message);
            return null;
        }
        catch (EngineeringException eng)
        {
            Console.Error.WriteLine("[reflect] " + instance.GetType().Name + "." + propertyName + " -> " + eng.Message);
            return null;
        }
    }

    private static string Unwrap(Exception ex) =>
        ex is TargetInvocationException tie && tie.InnerException is not null ? tie.InnerException.Message : ex.Message;

    public Task<OnlineStatus> GetOnlineStatusAsync(string devicePath, CancellationToken ct)
    {
        // No CPU online-state query exists in V21 Openness. PlcSoftware.CompareToOnline() does a full
        // online compare (slow, needs a reachable PLC) and returns a diff, not a RUN/STOP state — so we
        // surface the limitation rather than fabricate a state.
        _ = ResolvePlcSoftware(devicePath) ?? throw NotFound("PLC", devicePath);
        return Task.FromResult(new OnlineStatus(
            devicePath, Online: false, PlcState: "Unknown",
            Detail: "V21 Openness provides no PLC online-state query (no IPlcWebApi / CPU OperatingState). " +
                    "Use tia_project_compile + tia_download to interact with real hardware."));
    }

    public Task ConnectOnlineAsync(string devicePath, CancellationToken ct) =>
        throw new NotSupportedException(
            "V21 Openness has no explicit online-connect API (no IPlcWebApi.GoOnline). An online " +
            "connection is established implicitly by tia_download, driven from the device's configured " +
            "network interface.");

    public Task DisconnectOnlineAsync(string devicePath, CancellationToken ct) =>
        throw new NotSupportedException(
            "V21 Openness has no explicit online-disconnect API. The connection opened by tia_download " +
            "is released automatically when the download finishes.");

    public Task DownloadAsync(string devicePath, CompileMode scope, CancellationToken ct)
    {
        var software = ResolvePlcSoftware(devicePath) ?? throw NotFound("PLC", devicePath);
        var device = ResolveDevice(devicePath) ?? throw NotFound("device", devicePath);

        // Provider-based download (Siemens.Engineering.Download.DownloadProvider). The provider hangs off
        // the PLC software; the connection config comes from the device's network interface. Both require
        // a real, reachable PLC, so this can only succeed against configured hardware.
        var provider = software.GetService<DownloadProvider>()
            ?? throw new NotSupportedException(
                "This PLC exposes no DownloadProvider via V21 Openness. Download needs a real PLC whose " +
                "software container is reachable through Openness.");

        var config = FindConnectionConfig(device)
            ?? throw new InvalidOperationException(
                "No configured online connection was found on the PLC's network interface(s). Configure the " +
                "CPU's PROFINET/Ethernet interface (accessible node / IP) in TIA Portal first, then download.");

        try
        {
            provider.Download(config, null, null, MapDownloadOptions(scope));
        }
        catch (Exception ex) when (ex is NonRecoverableException || ex is EngineeringObjectDisposedException)
        {
            throw PortalCrashed("download", ex);
        }
        return Task.CompletedTask;
    }

    public Task PlcRunAsync(string devicePath, CancellationToken ct) =>
        throw new NotSupportedException(
            "V21 Openness exposes no CPU RUN/STOP control API. After tia_download the PLC returns to RUN " +
            "per its configured startup mode; Openness cannot force RUN directly.");

    public Task PlcStopAsync(string devicePath, CancellationToken ct) =>
        throw new NotSupportedException(
            "V21 Openness exposes no CPU RUN/STOP control API (see tia_plc_run). Openness cannot force " +
            "STOP directly.");

    // ----- P4 helpers -----

    private static DeviceItemInfo ToDeviceItemInfo(DeviceItem item, string devicePath)
    {
        int slot;
        try { slot = (int)item.PositionNumber; }
        catch { slot = 0; } // some unconfigured nodes throw on PositionNumber

        string? typeId;
        try { typeId = item.TypeIdentifier; }
        catch { typeId = null; } // ditto

        // V21 DeviceItem has no direct OrderNumber/TypeName property; TypeIdentifier already carries the
        // module identity (e.g. System:Module.6ES7...), so leave TypeName null.
        return new DeviceItemInfo(item.Name, typeId, slot, TypeName: null, devicePath + "/item:" + item.Name);
    }

    private static DownloadOptions MapDownloadOptions(CompileMode mode) => mode switch
    {
        CompileMode.Hardware => DownloadOptions.Hardware,
        CompileMode.All => DownloadOptions.Hardware | DownloadOptions.Software,
        _ => DownloadOptions.Software,
    };

    /// <summary>
    /// Walk the device's network interfaces for an online <see cref="IConfiguration"/>. V21's
    /// NetworkInterface.Nodes lead to a connection config; the exact node→config hop is version-sensitive,
    /// so we accept a node that IS an IConfiguration directly and otherwise reflect for a
    /// ConnectionConfiguration property. Returns null when nothing is configured (no reachable PLC).
    /// </summary>
    private static IConfiguration? FindConnectionConfig(Device device)
    {
        foreach (var item in EnumerateDeviceItems(device))
        {
            NetworkInterface? ni;
            try { ni = item.GetService<NetworkInterface>(); }
            catch { continue; }
            if (ni is null) { continue; }

            foreach (var node in ni.Nodes)
            {
                if (node is IConfiguration direct)
                {
                    return direct;
                }

                try
                {
                    var cc = node.GetType().InvokeMember(
                        "ConnectionConfiguration",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.GetProperty,
                        null, node, null);
                    if (cc is IConfiguration cfg)
                    {
                        return cfg;
                    }
                }
                catch
                {
                    // no ConnectionConfiguration on this node — try the next
                }
            }
        }

        return null;
    }

    // ============================== portal / path resolution ==============================

    /// <summary>
    /// True if <see cref="_portal"/> references a LIVE TIA Portal. A portal can die out from under us
    /// while the field still holds the (now dead) reference: a compile-time
    /// <see cref="NonRecoverableException"/> tears down the whole Portal process, or the user closes an
    /// attached GUI. Operating on that corpse throws <see cref="EngineeringObjectDisposedException"/>
    /// (or a COM error) on every subsequent call, yet <c>_portal is not null</c> still reads true — which
    /// is why <c>tia_status</c> used to report TiaAvailable=true and <c>tia_connect attach</c> used to
    /// "succeed" without reconnecting. Probe the handle; if it has died, drop the stale reference and its
    /// cached projects so callers re-attach/respawn instead.
    /// </summary>
    private bool IsPortalAlive()
    {
        if (_portal is null)
        {
            return false;
        }
        try
        {
            // Force a round-trip to the Portal process; a disposed/dead portal throws here.
            foreach (var _ in _portal.Projects)
            {
                break;
            }
            return true;
        }
        catch (Exception ex) when (
            ex is EngineeringObjectDisposedException ||
            ex is NonRecoverableException ||
            ex is System.Runtime.InteropServices.COMException)
        {
            DropDeadPortal("probe failed (" + ex.GetType().Name + "): " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Drop a dead/disposed <see cref="_portal"/> reference and its cached projects so the next call
    /// re-attaches or respawns instead of operating on a corpse. Best-effort: we do NOT dispose (the
    /// handle is already dead, and in attach mode we never owned the Portal lifecycle anyway).
    /// </summary>
    private void DropDeadPortal(string reason)
    {
        Console.Error.WriteLine("[worker] dropping dead portal reference — " + reason);
        _portal = null;
        _openProjects.Clear();
    }

    /// <summary>
    /// Run <paramref name="compiler"/>.Compile(), converting a portal-killing crash into a clear,
    /// recoverable error. A <see cref="NonRecoverableException"/> (or an already-disposed handle) means
    /// the TiaPortal — and in attach mode the user's whole GUI Portal — is gone.
    /// </summary>
    private CompilerResult RunCompile(ICompilable compiler)
    {
        try
        {
            return compiler.Compile();
        }
        catch (Exception ex) when (ex is NonRecoverableException || ex is EngineeringObjectDisposedException)
        {
            throw PortalCrashed("compile", ex);
        }
    }

    /// <summary>
    /// Drop the dead portal and build a clear, actionable error for a heavy op (compile/download) that
    /// crashed the TiaPortal. Returns the exception so callers write <c>throw PortalCrashed(...)</c>.
    /// Observed live 2026-06-23: a compile <see cref="NonRecoverableException"/> took down an attached
    /// GUI Portal, after which every call threw a bare <see cref="EngineeringObjectDisposedException"/>.
    /// </summary>
    private InvalidOperationException PortalCrashed(string op, Exception ex)
    {
        DropDeadPortal(op + " raised " + ex.GetType().Name + ": " + ex.Message);
        var detail = string.IsNullOrEmpty(ex.Message) ? "" : " (" + ex.Message + ")";
        return new InvalidOperationException(
            "TIA Portal entered an unrecoverable state during " + op + " and the session was lost" + detail +
            ". An attached GUI Portal may have crashed — reopen TIA Portal and reconnect with tia_connect. " +
            "Compile/download are more robust on a spawned instance (mode 'headless'/'interactive') than on " +
            "an attached GUI Portal.", ex);
    }

    private void EnsurePortal(ConnectRequest request)
    {
        // Treat a dead-but-non-null portal as absent so attach/spawn actually re-runs (IsPortalAlive
        // clears the stale reference when the handle has died).
        if (IsPortalAlive())
        {
            return;
        }

        // Attach mode: connect to an ALREADY-RUNNING TIA Portal instance the user opened, instead of
        // spawning a new one. The agent never owns the Portal lifecycle or the project lock, which
        // sidesteps the orphan-lock / portal-dispose-bricks-worker problems a spawned instance causes.
        // (Mirror of Czarnak/tia-portal-mcp's attach model.)
        if (string.Equals(request.Mode, "attach", StringComparison.OrdinalIgnoreCase))
        {
            var first = TiaPortal.GetProcesses().FirstOrDefault();
            if (first is null)
            {
                throw new InvalidOperationException(
                    "No running TIA Portal V21 instance found to attach to. Start TIA Portal (and open " +
                    "a project) first, or connect with mode 'interactive' / 'headless' to spawn one.");
            }

            _portal = first.Attach();
            WirePortalEvents(_portal);
            // Pre-load the projects the user already opened so path resolution works immediately.
            foreach (Project p in _portal.Projects)
            {
                _openProjects[p.Name] = p;
            }
            return;
        }

        var headless = !string.Equals(request.Mode, "interactive", StringComparison.OrdinalIgnoreCase);
        // Verified: single-arg ctor; TiaPortalMode = WithUserInterface | WithoutUserInterface.
        _portal = CreatePortal(headless);
        WirePortalEvents(_portal);
    }

    /// <summary>
    /// Construct the <c>TiaPortal</c>, retrying on a transient <c>EngineeringSecurityException</c>.
    /// The Openness handshake (<c>new TiaPortal</c>) can time out with a security error when a
    /// previous TIA session is still shutting down (common during rapid connect/disconnect or right
    /// after a kill). A short retry usually lets the old session finish and the new one establish.
    /// </summary>
    private static TiaPortal CreatePortal(bool headless)
    {
        var mode = headless ? TiaPortalMode.WithoutUserInterface : TiaPortalMode.WithUserInterface;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new TiaPortal(mode);
            }
            catch (EngineeringSecurityException ex) when (attempt < 3)
            {
                Console.Error.WriteLine("[worker] TiaPortal handshake failed (attempt " + attempt + "), retrying in 5s: " + ex.Message);
                Thread.Sleep(5000);
            }
        }
    }

    /// <summary>
    /// Hook TIA Portal events so headless operation never blocks on a modal. Notifications go to
    /// stderr; Confirmations are auto-accepted (Yes) — the MCP-layer AccessGuard is the real safety
    /// boundary, and these are internal engineering prompts (reorganize / save-changes / etc.) that
    /// would otherwise hang the worker indefinitely in headless mode.
    /// </summary>
    private static void WirePortalEvents(TiaPortal portal)
    {
        portal.Notification += (_, e) => Console.Error.WriteLine("[tia] notification: " + e.Text);
        portal.Confirmation += (_, e) => e.Result = ConfirmationResult.Yes;
    }

    // ----- P7 helpers -----

    /// <summary>Set the text of an Openness MultilingualText (tag/block comment): prefer the
    /// en-US culture item, else the first item. New objects carry one empty item per project
    /// language, so writing = picking the item and assigning <c>.Text</c> (live-verified pattern;
    /// reading is the P3-notes verified <c>foreach (MultilingualTextItem it in x.Items)</c>).</summary>
    private static void SetMultilingualText(MultilingualText? text, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || text is null)
        {
            return;
        }
        MultilingualTextItem? first = null;
        foreach (MultilingualTextItem it in text.Items)
        {
            first ??= it;
            if (string.Equals(it.Language.Culture.Name, "en-US", StringComparison.OrdinalIgnoreCase))
            {
                first = it;
                break;
            }
        }
        if (first is not null)
        {
            first.Text = value;
        }
    }

    /// <summary>If <paramref name="source"/> is a path to an existing file (and not inline XML),
    /// return that path so the importer reads it directly; otherwise null (caller writes inline XML).</summary>
    private static string? ResolveSourceFile(string? source)
    {
        if (source is null || source.TrimStart().StartsWith("<")) return null;
        return File.Exists(source) ? source : null;
    }

    /// <summary>Read the type names (each <c>&lt;SW.Types.PlcStruct&gt;</c>'s <c>&lt;Name&gt;</c>) from
    /// a SimaticML file on disk — used to delete same-named types before a subgroup move-import.</summary>
    private static List<string> ParseTypeNames(string xmlFile)
    {
        var names = new List<string>();
        var doc = XDocument.Load(xmlFile);
        foreach (var st in doc.Descendants().Where(e => e.Name.LocalName == "SW.Types.PlcStruct"))
        {
            var al = st.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
            var nameEl = al?.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
            if (nameEl != null && !string.IsNullOrWhiteSpace(nameEl.Value))
            {
                names.Add(nameEl.Value.Trim());
            }
        }
        return names;
    }

    private static string WriteTempXml(string prefix, string xml)
    {
        var file = Path.Combine(Path.GetTempPath(), prefix + "_" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(file, xml ?? string.Empty, new UTF8Encoding(false));
        return file;
    }

    private static HashSet<string> SnapshotBlockNames(PlcSoftware software)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PlcBlock b in software.BlockGroup.Blocks)
        {
            set.Add(b.Name);
        }
        return set;
    }

    private static HashSet<string> SnapshotTypeNames(PlcSoftware software)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PlcType t in software.TypeGroup.Types)
        {
            set.Add(t.Name);
        }
        return set;
    }

    private static HashSet<string> SnapshotTagTableNames(PlcSoftware software)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PlcTagTable t in software.TagTableGroup.TagTables)
        {
            set.Add(t.Name);
        }
        return set;
    }

    private static List<string> DiffNames(HashSet<string> before, HashSet<string> after)
    {
        var added = new List<string>();
        foreach (var name in after)
        {
            if (!before.Contains(name))
            {
                added.Add(name);
            }
        }
        return added;
    }

    private static string NameList(List<string> names) =>
        names.Count == 0 ? "" : " (" + string.Join(", ", names) + ")";

    private static string NormalizeGroupKind(string groupKind)
    {
        var k = (groupKind ?? string.Empty).Trim().ToLowerInvariant();
        if (k == "type" || k == "udt") return "type";
        if (k == "tag" || k == "tagtable") return "tagtable";
        return "block";
    }

    /// <summary>Find a global library already open in this Portal matching the given file (a global
    /// library's Name is its file name without extension). Lets OpenLibrary reuse it instead of
    /// re-Open()ing — V21 throws "Cannot change the open mode of an already open global library"
    /// otherwise (the GUI commonly already has it open). Only UserGlobalLibrary instances qualify.</summary>
    private UserGlobalLibrary? FindOpenGlobalLibrary(FileInfo fi)
    {
        if (_portal is null)
        {
            return null;
        }

        var wantName = Path.GetFileNameWithoutExtension(fi.Name);
        foreach (GlobalLibrary g in _portal.GlobalLibraries)
        {
            if (g is UserGlobalLibrary ug
                && string.Equals(ug.Name, wantName, StringComparison.OrdinalIgnoreCase))
            {
                return ug;
            }
        }

        return null;
    }

    private UserGlobalLibrary? ResolveLibrary(string libraryName)
    {
        if (!string.IsNullOrEmpty(libraryName) && _openLibraries.TryGetValue(libraryName, out var lib))
        {
            return lib;
        }
        // Fall back to the only opened library if the caller didn't name one.
        return _openLibraries.Count == 1 ? _openLibraries.Values.First() : null;
    }

    private static void CollectMasterCopies(MasterCopyFolder folder, string folderPath, List<MasterCopyInfo> into)
    {
        foreach (MasterCopy mc in folder.MasterCopies)
        {
            into.Add(new MasterCopyInfo(SafeStr(() => mc.Name), folderPath, null));
        }
        foreach (MasterCopyUserFolder sub in folder.Folders)
        {
            var name = SafeStr(() => sub.Name);
            var childPath = string.IsNullOrEmpty(folderPath) ? name : folderPath + "/" + name;
            CollectMasterCopies(sub, childPath, into);
        }
    }

    private static MasterCopy? FindMasterCopy(MasterCopyFolder folder, string name)
    {
        foreach (MasterCopy mc in folder.MasterCopies)
        {
            if (string.Equals(SafeStr(() => mc.Name), name, StringComparison.OrdinalIgnoreCase))
            {
                return mc;
            }
        }
        foreach (MasterCopyUserFolder sub in folder.Folders)
        {
            var found = FindMasterCopy(sub, name);
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }

    private Project? ResolveProject(string path)
    {
        var name = PathSegment(path, "project");
        // Prefer a LIVE object straight from the Portal: the cached _openProjects reference can become
        // stale / disposed after SaveAs or a structural change (add device), and operating on it throws
        // ObjectDisposedException. Refresh the cache from the source of truth on every resolve.
        if (_portal is not null)
        {
            foreach (Project p in _portal.Projects)
            {
                // A rebind (SaveAs + Close + immediate Open of a same-named clone) can leave a DISPOSED
                // Project in the Portal's enumeration right next to the live one; probing p.Name here
                // skips the corpse instead of returning it (which crashed callers with ObjectDisposed).
                if (!IsProjectAlive(p)) continue;
                _openProjects[p.Name] = p;
                if (string.IsNullOrEmpty(name) || string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return p;
                }
            }
        }

        if (!string.IsNullOrEmpty(name) && _openProjects.TryGetValue(name!, out var cached) && IsProjectAlive(cached))
        {
            return cached;
        }

        // Last resort: any live cached project.
        foreach (var p in _openProjects.Values)
        {
            if (IsProjectAlive(p)) return p;
        }

        return null;
    }

    /// <summary>Probe whether a <see cref="Project"/> handle is still usable. Accessing any property of a
    /// disposed/dead project throws <see cref="EngineeringObjectDisposedException"/>; we catch and treat
    /// that as "not alive" so <see cref="ResolveProject"/> skips it instead of returning a time bomb.</summary>
    private static bool IsProjectAlive(Project p)
    {
        try { _ = p.Name; return true; }
        catch (EngineeringObjectDisposedException) { return false; }
        catch (EngineeringException) { return false; }
    }

    private Device? ResolveDevice(string path)
    {
        var project = ResolveProject(path);
        if (project is null)
        {
            return null;
        }

        // Device names may contain '/' (Siemens defaults to e.g. "S7-1500/ET200MP station_1"), which
        // collides with the path separator. So don't trust the generic parser's single segment — take
        // everything after "device:" and longest-prefix-match against the project's real device names.
        // Trailing segments like "plc:program" simply won't match any name and are ignored.
        // Match against EVERY device (top-level stations + ungrouped/grouped slaves): real projects
        // file stations under UngroupedDevicesGroup/DeviceGroups, where a project.Devices-only lookup
        // finds nothing (same reason ListTargetsAsync must recurse).
        const string marker = "device:";
        var at = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return EnumerateAllDevices(project).FirstOrDefault();
        }

        var parts = path.Substring(at + marker.Length).Split('/');
        var all = EnumerateAllDevices(project);
        Device? best = null;
        for (var n = 1; n <= parts.Length; n++)
        {
            var candidate = string.Join("/", parts, 0, n).Trim();
            var match = all.FirstOrDefault(
                d => string.Equals(d.Name, candidate, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                best = match;
            }
        }

        return best; // a device: segment was present; null means it matched nothing
    }

    private PlcSoftware? ResolvePlcSoftware(string path)
    {
        var device = ResolveDevice(path);
        return device is null ? null : GetPlcSoftware(device);
    }

    private PlcBlock? ResolveBlock(string path)
    {
        var software = ResolvePlcSoftware(path);
        if (software is null)
        {
            return null;
        }

        var name = PathSegment(path, "block");
        // Block names are unique program-wide in TIA, so a recursive find-by-name across all groups is
        // unambiguous — and necessary, because the block may live in a user group, not the root folder.
        return string.IsNullOrEmpty(name) ? null : FindBlock(software.BlockGroup, name!);
    }

    /// <summary>Depth-first enumeration of every block under a group, including all nested user groups.
    /// TIA files user blocks into BlockGroup.Groups; a root-only scan (BlockGroup.Blocks) misses them.</summary>
    private static IEnumerable<PlcBlock> EnumerateBlocks(PlcBlockGroup group)
    {
        foreach (PlcBlock b in group.Blocks)
        {
            yield return b;
        }

        foreach (PlcBlockUserGroup sub in group.Groups)
        {
            foreach (var b in EnumerateBlocks(sub))
            {
                yield return b;
            }
        }
    }

    /// <summary>Find a block by name anywhere in the group tree (root folder + every nested user group).</summary>
    private static PlcBlock? FindBlock(PlcBlockGroup group, string name)
    {
        if (group.Blocks.Find(name) is { } hit)
        {
            return hit;
        }

        foreach (PlcBlockUserGroup sub in group.Groups)
        {
            if (FindBlock(sub, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Resolve the block subgroup named in the path's <c>blockgroup:</c> segment (recursing
    /// nested user groups); the root <see cref="PlcBlockGroup"/> when the path has no such segment;
    /// or throws if a segment names a group that does not exist.</summary>
    private PlcBlockGroup ResolveBlockGroup(string path, PlcSoftware software)
    {
        var name = PathSegment(path, "blockgroup");
        if (string.IsNullOrEmpty(name))
        {
            return software.BlockGroup;
        }

        return FindBlockGroup(software.BlockGroup, name!) ?? throw NotFound("block group", name!);
    }

    private static PlcBlockUserGroup? FindBlockGroup(PlcBlockGroup group, string name)
    {
        foreach (PlcBlockUserGroup sub in group.Groups)
        {
            if (string.Equals(sub.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return sub;
            }

            if (FindBlockGroup(sub, name) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    /// <summary>Walk the device's entire item tree and return the first PlcSoftware found via its
    /// SoftwareContainer service. A CPU lives under rack→slot (a nested DeviceItem), not at the top
    /// level, so this must recurse — a top-level-only scan misses real PLCs.</summary>
    private static PlcSoftware? GetPlcSoftware(Device device)
    {
        foreach (DeviceItem item in EnumerateDeviceItems(device))
        {
            var container = item.GetService<SoftwareContainer>();
            if (container?.Software is PlcSoftware software)
            {
                return software;
            }
        }

        return null;
    }

    /// <summary>True when the device hosts HMI software (e.g. an <c>HmiTarget</c>), detected by the
    /// runtime type name of its <c>Software</c> so the worker needs no compile-time reference to the
    /// HMI namespace. This distinguishes real HMI devices from PROFINET slaves / drives, which also
    /// enumerate as devices but hold no HMI software.</summary>
    private static bool IsHmiDevice(Device device)
    {
        foreach (DeviceItem item in EnumerateDeviceItems(device))
        {
            try
            {
                var sw = item.GetService<SoftwareContainer>()?.Software;
                if (sw is not null &&
                    sw.GetType().Name.IndexOf("Hmi", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            catch
            {
                // Some device items do not expose a software container; skip them.
            }
        }
        return false;
    }

    /// <summary>Depth-first traversal of a device's item tree (top level + every nested sub-item).</summary>
    private static IEnumerable<DeviceItem> EnumerateDeviceItems(Device device)
    {
        var stack = new Stack<DeviceItem>();
        foreach (DeviceItem top in device.DeviceItems)
        {
            stack.Push(top);
        }

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            foreach (DeviceItem child in current.DeviceItems)
            {
                stack.Push(child);
            }
        }
    }

    /// <summary>Find the CPU device item. On S7-1500 the CPU is the item that exposes the
    /// SystemMemoryByte attribute (returned directly). On S7-1200 that attribute throws
    /// EngineeringNotSupportedException, so we fall back to the item carrying the PlcSoftware (the
    /// CPU); ConfigureCpuMemoryAsync then detects the unsupported case and reports it clearly.
    /// Returns null only when the device has no CPU at all.</summary>
    private static DeviceItem? FindCpuItem(Device device)
    {
        DeviceItem? bySoftware = null;
        foreach (var item in EnumerateDeviceItems(device))
        {
            try
            {
                if (((IEngineeringObject)item).GetAttribute("SystemMemoryByte") is bool)
                    return item;
            }
            catch { /* attribute not supported on this item (e.g. S7-1200 CPU) */ }

            if (bySoftware is null)
            {
                try { if (item.GetService<SoftwareContainer>()?.Software is PlcSoftware) bySoftware = item; }
                catch { /* not a CPU item */ }
            }
        }
        return bySoftware;
    }

    private static bool AttrBool(DeviceItem item, string name)
    {
        try { return ((IEngineeringObject)item).GetAttribute(name) is bool b && b; }
        catch { return false; }
    }

    private static long AttrLong(DeviceItem item, string name)
    {
        try
        {
            return ((IEngineeringObject)item).GetAttribute(name) switch
            {
                ulong u => (long)u,
                long l => l,
                int i => i,
                uint ui => ui,
                _ => 0
            };
        }
        catch { return 0; }
    }

    /// <summary>Every device in the project: top-level stations (project.Devices), the IO slaves filed
    /// under UngroupedDevicesGroup (where PROFINET/PROFIBUS devices land by default), and any nested
    /// device groups. project.Devices alone omits the ungrouped slaves, so hardware reads miss them.
    /// Best-effort: each source is guarded so a missing group on some version can't sink the whole walk.</summary>
    private static List<Device> EnumerateAllDevices(Project project)
    {
        var result = new List<Device>();
        try { foreach (Device d in project.Devices) { result.Add(d); } }
        catch (EngineeringException) { }

        try
        {
            var ungrouped = project.UngroupedDevicesGroup;
            if (ungrouped is not null)
            {
                foreach (Device d in ungrouped.Devices) { result.Add(d); }
            }
        }
        catch (EngineeringException) { }

        try
        {
            var groups = new Stack<DeviceUserGroup>();
            foreach (DeviceUserGroup g in project.DeviceGroups) { groups.Push(g); }
            while (groups.Count > 0)
            {
                var g = groups.Pop();
                foreach (Device d in g.Devices) { result.Add(d); }
                foreach (DeviceUserGroup sub in g.Groups) { groups.Push(sub); }
            }
        }
        catch (EngineeringException) { }

        return result;
    }

    /// <summary>Rebuild the canonical .../project:X/device:Y/plc:program prefix from any scope path.
    /// Device names may contain '/' (e.g. "S7-1500/ET200MP station_1"), which the generic path parser
    /// would split — so locate the <c>plc:</c> anchor by index instead of parsing, keeping the full
    /// device name intact. Truncates anything after the plc segment (block/tagtable/tag).</summary>
    private static string PlcPathFor(string path)
    {
        var plcIdx = path.IndexOf("/plc:", StringComparison.OrdinalIgnoreCase);
        if (plcIdx < 0)
        {
            return path; // no plc segment — nothing to truncate to
        }

        var afterSeg = plcIdx + "/plc:".Length;
        var nextSlash = path.IndexOf('/', afterSeg);
        return nextSlash < 0 ? path : path.Substring(0, nextSlash);
    }

    private static BlockInfo ToBlockInfo(PlcBlock b, string plcPath) => new(
        b.Name,
        BlockTypeOf(b),
        (int)b.Number, // uint -> int
        b.ProgrammingLanguage.ToString(),
        CommentOf(b),
        plcPath + "/block:" + b.Name,
        GroupPathOf(b));

    /// <summary>Slash-joined trail of user groups a block is filed under (empty for a root-level block).
    /// Walks the block's parent chain up through PlcBlockUserGroup links, stopping at the system root.
    /// Best-effort: any failure degrades to "" (group path is informational, never load-bearing).</summary>
    private static string GroupPathOf(PlcBlock b)
    {
        try
        {
            var parts = new List<string>();
            var cur = b.Parent;
            while (cur is PlcBlockUserGroup g)
            {
                parts.Add(g.Name);
                cur = g.Parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static BlockType BlockTypeOf(PlcBlock b)
    {
        if (b is FB) return BlockType.FB;
        if (b is OB) return BlockType.OB;
        if (b is FC) return BlockType.FC;
        if (b.ProgrammingLanguage == ProgrammingLanguage.DB) return BlockType.DB;
        return BlockType.FC;
    }

    private static string? CommentOf(PlcBlock b)
    {
        try
        {
            var comment = b.Comment; // MultilingualText
            if (comment is null)
            {
                return null;
            }

            foreach (MultilingualTextItem item in comment.Items)
            {
                return item.Text; // first available culture
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static TagInfo ToTagInfo(PlcTag tag, string tablePath)
    {
        string? comment = null;
        try
        {
            if (tag.Comment is { } c)
            {
                foreach (MultilingualTextItem it in c.Items)
                {
                    comment = it.Text;
                    break;
                }
            }
        }
        catch
        {
            // ignore
        }

        // LogicalAddress may be uint or string depending on type; .ToString() is safe for both.
        string? address;
        try
        {
            address = tag.LogicalAddress?.ToString();
        }
        catch
        {
            address = null;
        }

        return new TagInfo(tag.Name, address, tag.DataTypeName, comment, tablePath + "/tag:" + tag.Name);
    }

    private static string ExportToText(PlcBlock b, bool unique)
    {
        // A fresh unique temp file when requested: dodges "file in use" from a lingering handle on the
        // shared name (seen on OB exports) and avoids collisions across same-named blocks.
        var file = unique
            ? Path.Combine(Path.GetTempPath(), b.Name + "_src_" + Guid.NewGuid().ToString("N") + ".xml")
            : Path.Combine(Path.GetTempPath(), b.Name + "_src.xml");
        b.Export(new FileInfo(file), ExportOptions.WithDefaults);
        return File.ReadAllText(file);
    }

    private static CompileSeverity MapSeverity(CompilerResultState state) => state switch
    {
        CompilerResultState.Error => CompileSeverity.Error,
        CompilerResultState.Warning => CompileSeverity.Warning,
        _ => CompileSeverity.Info,
    };

    private static string ParentPath(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx > 0 ? path.Substring(0, idx) : path;
    }

    private static string? PathSegment(string path, string kind)
    {
        foreach (var s in TiaPathParser.Parse(path))
        {
            if (string.Equals(s.Kind, kind, StringComparison.OrdinalIgnoreCase))
            {
                return s.Name;
            }
        }

        return null;
    }

    private static FileNotFoundException NotFound(string what, string path) =>
        new FileNotFoundException(what + " not found at '" + path + "'.");
}
