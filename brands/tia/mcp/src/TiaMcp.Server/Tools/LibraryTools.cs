using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcp.Contract;
using TiaMcp.Server.Safety;

namespace TiaMcp.Server.Tools;

/// <summary>P7 library reuse: open a global library, list its master copies, and instantiate a master
/// copy as a block in a PLC. Opening a library and instantiating are writes (need --mode ReadWrite);
/// listing is read-only.</summary>
[McpServerToolType]
public sealed class LibraryTools
{
    private readonly ITiaBackend _backend;
    private readonly AccessGuard _guard;
    private readonly AuditLog _audit;

    public LibraryTools(ITiaBackend backend, AccessGuard guard, AuditLog audit)
    {
        _backend = backend;
        _guard = guard;
        _audit = audit;
    }

    [McpServerTool(Name = "tia_library_open")]
    [Description(
        "Open a global library (.al21) in the current Portal session for master-copy reuse (write; " +
        "needs --mode ReadWrite). Returns the library name (use it in tia_mastercopy_list / " +
        "tia_block_create_from_copy) and how many master copies it holds.")]
    public async Task<MutationResult> TiaLibraryOpenAsync(
        [Description("Absolute path to the .al21 global library file.")]
        string libraryPath,
        [Description("Open read-only (default true). Set false to allow writing the library.")]
        bool readOnly = true,
        CancellationToken ct = default)
    {
        var op = TiaOps.OpenLibrary;
        var decision = _guard.Check(op, confirm: false);
        if (!decision.Allow)
        {
            return MutationResult.Denied(op, decision.DenyReason!);
        }

        try
        {
            var lib = await _backend.OpenLibraryAsync(libraryPath, readOnly, ct);
            _audit.Append(op, lib.Path, success: true);
            return MutationResult.Applied(op,
                $"Opened library '{lib.Name}' ({lib.MasterCopyCount} master copies){(lib.ReadOnly ? " read-only" : "")}.");
        }
        catch (Exception ex)
        {
            _audit.Append(op, libraryPath, success: false, error: ex.Message);
            return MutationResult.Failed(op, ex.Message);
        }
    }

    [McpServerTool(Name = "tia_mastercopy_list")]
    [Description(
        "List the master copies in an opened global library (read). Pass the library name returned by " +
        "tia_library_open (omit if only one is open). Each entry has the copy name and its folder path.")]
    public Task<object> TiaMasterCopyListAsync(
        [Description("Library name from tia_library_open (optional if a single library is open).")]
        string? libraryName = null,
        CancellationToken ct = default) =>
        ToolErrors.InvokeAsync(() => _backend.ListMasterCopiesAsync(libraryName ?? string.Empty, ct));

    [McpServerTool(Name = "tia_block_create_from_copy")]
    [Description(
        "Instantiate a library master copy as a new block in a PLC (write; needs --mode ReadWrite). " +
        "Open the library first with tia_library_open. Returns the new block path.")]
    public async Task<MutationResult> TiaBlockCreateFromCopyAsync(
        [Description("PLC scope path, e.g. .../device:PLC_1/plc:program.")]
        string plcPath,
        [Description("Master copy name (from tia_mastercopy_list).")]
        string masterCopyName,
        [Description("Library name from tia_library_open (optional if a single library is open).")]
        string? libraryName = null,
        CancellationToken ct = default)
    {
        var op = TiaOps.CreateBlockFromCopy;
        var decision = _guard.Check(op, confirm: false);
        if (!decision.Allow)
        {
            return MutationResult.Denied(op, decision.DenyReason!);
        }

        try
        {
            var path = await _backend.CreateBlockFromCopyAsync(plcPath, libraryName ?? string.Empty, masterCopyName, ct);
            _audit.Append(op, path, success: true);
            return MutationResult.Applied(op, $"Created block from master copy '{masterCopyName}' -> {path}.");
        }
        catch (Exception ex)
        {
            _audit.Append(op, plcPath, success: false, error: ex.Message);
            return MutationResult.Failed(op, ex.Message);
        }
    }
}
