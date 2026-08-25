using System.ComponentModel;
using System.IO;
using ModelContextProtocol.Server;
using TiaMcp.Contract;
using TiaMcp.Server.Safety;

namespace TiaMcp.Server.Tools;

/// <summary>TIA project lifecycle tools (P1: open / list targets / compile; P5: status/save/saveAs/create/archive/close).</summary>
[McpServerToolType]
public sealed class ProjectTools
{
    private readonly ITiaBackend _backend;
    private readonly AccessGuard _guard;
    private readonly AuditLog _audit;

    public ProjectTools(ITiaBackend backend, AccessGuard guard, AuditLog audit)
    {
        _backend = backend;
        _guard = guard;
        _audit = audit;
    }

    [McpServerTool(Name = "tia_project_open")]
    [Description(
        "Open a TIA project: pass the .ap19/.ap21 file path, or a bare project NAME to resolve a " +
        "project already open in this Portal (attach). Returns project info including its path for " +
        "further calls. The Fake backend always exposes the single 'Demo' project regardless of the path given.")]
    public Task<object> TiaProjectOpenAsync(
        [Description("Session path returned by tia_connect, e.g. session:s-fake.")]
        string sessionPath,
        [Description("Path to the .ap19/.ap21 project file (may be relative to the workspace), or a bare project name of one already open in the Portal.")]
        string path,
        [Description("Open visibly in the TIA UI (default true).")]
        bool visible = true,
        CancellationToken ct = default)
    {
        // Openness rejects relative paths, and the net48 worker is a separate process whose CWD
        // differs from the agent's. Resolve against the server's CWD (= the agent's workspace) here
        // so the worker always receives an absolute path. ONLY for real .ap1x file paths though —
        // a bare project NAME (attach case: resolve a project already open in the Portal) must pass
        // through untouched, or GetFullPath would weld it to the CWD and the name could never match.
        var absPath = Path.GetExtension(path).StartsWith(".ap", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(path)
            : path;
        return ToolErrors.InvokeAsync(() => _backend.OpenProjectAsync(sessionPath, new OpenProjectRequest(absPath, visible), ct));
    }

    [McpServerTool(Name = "tia_project_list")]
    [Description("List the device targets (PLC/HMI) in a project. Returns each target's name, kind and path.")]
    public Task<object> TiaProjectListAsync(
        [Description("Project path, e.g. session:s-fake/project:Demo.")]
        string projectPath,
        CancellationToken ct = default) =>
        ToolErrors.InvokeAsync(() => _backend.ListTargetsAsync(projectPath, ct));

    [McpServerTool(Name = "tia_project_compile")]
    [Description(
        "Compile a project / device / session (Software | Hardware | All). Compiling reconciles " +
        "block versions and writes to disk, so it needs --mode ReadWrite. Returns success plus " +
        "structured diagnostics. Long-running on real TIA.")]
    public async Task<object> TiaProjectCompileAsync(
        [Description("Scope path: a session, project or device.")]
        string scopePath,
        [Description("Software | Hardware | All (default Software).")]
        string mode = "Software",
        CancellationToken ct = default)
    {
        var compileMode = EnumText.TryParse<CompileMode>(mode) ?? CompileMode.Software;
        const string op = TiaOps.CompileProject;
        var decision = _guard.Check(op, confirm: false);
        if (!decision.Allow)
        {
            // Surface the access denial as a (failed) compile result so the return shape stays stable.
            return new CompileResult(
                compileMode,
                Success: false,
                Errors: 1,
                Warnings: 0,
                new[] { new CompileDiagnostic(CompileSeverity.Error, "ACCESS", decision.DenyReason ?? "Denied by access policy.", scopePath, null) },
                DurationMs: 0);
        }

        var result = await ToolErrors.InvokeAsync(
            () => _backend.CompileAsync(scopePath, compileMode, ct)).ConfigureAwait(false);
        _audit.Append(op, scopePath, success: result is CompileResult { Success: true });
        return result;
    }

    // --- P5: project lifecycle ---

    [McpServerTool(Name = "tia_project_status")]
    [Description("Read the active project's metadata: name/path/version/author, isModified, timestamps, size.")]
    public Task<object> TiaProjectStatusAsync(
        [Description("Project path, e.g. session:s-openness/project:Project1.")]
        string projectPath,
        CancellationToken ct = default) =>
        ToolErrors.InvokeAsync(() => _backend.GetProjectStatusAsync(projectPath, ct));

    [McpServerTool(Name = "tia_project_save")]
    [Description("Save the project to disk (write; needs --mode ReadWrite).")]
    public Task<MutationResult> TiaProjectSaveAsync(
        [Description("Project path.")] string projectPath,
        CancellationToken ct = default) =>
        GuardedAsync(TiaOps.SaveProject, projectPath, () => _backend.SaveProjectAsync(projectPath, ct));

    [McpServerTool(Name = "tia_project_save_as")]
    [Description(
        "Save a copy of the project under a new directory/name (write; needs --mode ReadWrite). " +
        "TIA's SaveAs MOVES the project handle to the copy, so afterwards the source is closed in " +
        "this session either way: rebind=false leaves nothing open (open source or copy explicitly " +
        "to continue), rebind=true reopens the copy in its place.")]
    public Task<MutationResult> TiaProjectSaveAsAsync(
        [Description("Source project path.")] string projectPath,
        [Description("Absolute target directory (must exist).")] string targetDirectory,
        [Description("New project name.")] string targetName,
        [Description("Close the source and reopen the copy (default false).")] bool rebind = false,
        CancellationToken ct = default)
    {
        var abs = Path.GetFullPath(targetDirectory);
        return GuardedAsync(TiaOps.SaveProjectAs, projectPath,
            () => _backend.SaveProjectAsAsync(projectPath, abs, targetName, rebind, ct));
    }

    [McpServerTool(Name = "tia_project_create")]
    [Description(
        "Create a brand-new empty project in a directory (write; needs --mode ReadWrite). " +
        "The new project is open in the session afterwards at <sessionPath>/project:<projectName> " +
        "(the .ap1x file lands in <projectDirectory>\\<projectName>\\).")]
    public Task<MutationResult> TiaProjectCreateAsync(
        [Description("Session path returned by tia_connect.")] string sessionPath,
        [Description("Absolute directory to create the project in (must exist).")] string projectDirectory,
        [Description("New project name.")] string projectName,
        string? author = null,
        string? comment = null,
        CancellationToken ct = default)
    {
        var abs = Path.GetFullPath(projectDirectory);
        return GuardedAsync(TiaOps.CreateProject, sessionPath,
            () => _backend.CreateProjectAsync(sessionPath, abs, projectName, author, comment, ct));
    }

    [McpServerTool(Name = "tia_project_archive")]
    [Description(
        "Archive the project to a .zap1x file (write; needs --mode ReadWrite). " +
        "mode: None | DiscardRestorableData | Compressed | DiscardRestorableDataAndCompressed.")]
    public Task<MutationResult> TiaProjectArchiveAsync(
        [Description("Project path.")] string projectPath,
        [Description("Absolute archive output directory (must exist).")] string archiveDirectory,
        [Description("Archive file name (without extension).")] string archiveName,
        [Description("None | DiscardRestorableData | Compressed | DiscardRestorableDataAndCompressed (default Compressed).")]
        string mode = "Compressed",
        [Description("Save before archiving (default true).")] bool saveBeforeArchive = true,
        CancellationToken ct = default)
    {
        var abs = Path.GetFullPath(archiveDirectory);
        var m = EnumText.TryParse<ArchiveMode>(mode) ?? ArchiveMode.Compressed;
        return GuardedAsync(TiaOps.ArchiveProject, projectPath,
            () => _backend.ArchiveProjectAsync(projectPath, abs, archiveName, m, saveBeforeArchive, ct));
    }

    [McpServerTool(Name = "tia_project_close")]
    [Description(
        "Close the project in this session (write; needs --mode ReadWrite). " +
        "Set saveBeforeClose=true to persist first.")]
    public Task<MutationResult> TiaProjectCloseAsync(
        [Description("Project path.")] string projectPath,
        [Description("Save before closing (default false).")] bool saveBeforeClose = false,
        CancellationToken ct = default) =>
        GuardedAsync(TiaOps.CloseProject, projectPath,
            () => _backend.CloseProjectAsync(projectPath, saveBeforeClose, ct));

    /// <summary>Gate a lifecycle write through the AccessGuard + audit; return MutationResult.</summary>
    private async Task<MutationResult> GuardedAsync(
        string op, string target, Func<Task<ProjectLifecycleResult>> run)
    {
        var decision = _guard.Check(op, confirm: false);
        if (!decision.Allow)
        {
            return MutationResult.Denied(op, decision.DenyReason!);
        }

        try
        {
            var r = await run().ConfigureAwait(false);
            _audit.Append(op, target, success: true);
            return MutationResult.Applied(op, $"{op} -> {r.ProjectPath ?? r.Project?.Name}");
        }
        catch (Exception ex)
        {
            _audit.Append(op, target, success: false, error: ex.Message);
            return MutationResult.Failed(op, ex.Message);
        }
    }
}
