using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcp.Contract;
using TiaMcp.Server.Safety;

namespace TiaMcp.Server.Tools;

/// <summary>PLC tag (variable) tools. P2: list (read) + create / delete (write).</summary>
[McpServerToolType]
public sealed class TagTools
{
    private readonly ITiaBackend _backend;
    private readonly AccessGuard _guard;
    private readonly AuditLog _audit;

    public TagTools(ITiaBackend backend, AccessGuard guard, AuditLog audit)
    {
        _backend = backend;
        _guard = guard;
        _audit = audit;
    }

    [McpServerTool(Name = "tia_tag_list")]
    [Description("List PLC tags under a scope path (a plc or tag-table path). Paginated.")]
    public Task<object> TiaTagListAsync(
        [Description("Scope path, e.g. .../device:PLC_1/plc:program or .../plc:program/tagtable:Default.")]
        string path,
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default)
    {
        var realLimit = limit <= 0 ? 50 : Math.Min(limit, 500);
        return ToolErrors.InvokeAsync(() => _backend.ListTagsAsync(path, realLimit, Math.Max(0, offset), ct));
    }

    [McpServerTool(Name = "tia_tagtable_list")]
    [Description(
        "List the PLC tag tables under a scope path (with tag counts), recursing tag-table user-groups. " +
        "Each entry carries its group path and a tag-table path for drill-down (tia_tag_list). Read-only.")]
    public Task<object> TiaTagTableListAsync(
        [Description("Scope path, e.g. session:s-fake or .../device:PLC_1/plc:program.")] string path,
        CancellationToken ct = default) =>
        ToolErrors.InvokeAsync(() => _backend.ListTagTablesAsync(path, ct));

    [McpServerTool(Name = "tia_tagtable_export")]
    [Description(
        "Export a PLC tag table (with its tags) to a SimaticML XML file — the reverse of " +
        "tia_tagtable_import, and the format tia_tagtable_import consumes. Read-only. " +
        "Lets a project's tag layer be migrated/archived alongside its UDT/block layers.")]
    public Task<object> TiaTagTableExportAsync(
        [Description("Tag-table path, e.g. .../plc:program/tagtable:Cabinet_OP10. The table may live in a subgroup; it is found by name.")]
        string path,
        [Description("Output directory (default a temp dir). The file is written as <TableName>.xml.")]
        string? outDir = null,
        CancellationToken ct = default) =>
        ToolErrors.InvokeAsync(() => _backend.ExportTagTableAsync(path, outDir, ct));

    [McpServerTool(Name = "tia_tag_create")]
    [Description("Create a PLC tag in the default tag table (write; needs --mode ReadWrite). Fails on name conflict.")]
    public async Task<MutationResult> TiaTagCreateAsync(
        [Description("Tag-table / plc scope path.")]
        string tagTablePath,
        [Description("Tag name, e.g. Valve_Open.")]
        string name,
        [Description("Address, e.g. %I0.5.")]
        string address,
        [Description("Data type, e.g. Bool / Int / Real (default Bool).")]
        string dataType = "Bool",
        string? comment = null,
        CancellationToken ct = default)
    {
        var op = TiaOps.CreateTag;
        var decision = _guard.Check(op, confirm: false);
        if (!decision.Allow)
        {
            return MutationResult.Denied(op, decision.DenyReason!);
        }

        try
        {
            var req = new CreateTagRequest(tagTablePath, name, address, dataType, comment);
            var path = await _backend.CreateTagAsync(tagTablePath, req, ct);
            _audit.Append(op, path, success: true);
            return MutationResult.Applied(op, $"Created tag '{name}' -> {path}.");
        }
        catch (Exception ex)
        {
            _audit.Append(op, tagTablePath, success: false, error: ex.Message);
            return MutationResult.Failed(op, ex.Message);
        }
    }

    [McpServerTool(Name = "tia_tag_delete")]
    [Description(
        "Delete a PLC tag (destructive; needs --mode ReadWrite AND confirm=true). " +
        "Without confirm it returns a preview. Preview AND result report the tag's dependents " +
        "(blocks that read/write it) from cross-references, so a delete that would break block " +
        "logic is visible before confirming.")]
    public async Task<MutationResult> TiaTagDeleteAsync(
        [Description("Tag path to delete, e.g. .../tagtable:Default/tag:Valve_Open.")]
        string path,
        bool confirm = false,
        CancellationToken ct = default)
    {
        var op = TiaOps.DeleteTag;
        var decision = _guard.Check(op, confirm);
        if (!decision.Allow)
        {
            if (!decision.NeedsConfirm)
            {
                return MutationResult.Denied(op, decision.DenyReason!);
            }

            var deps = await XrefImpact.DependentsOfAsync(_backend, path, ct).ConfigureAwait(false);
            return MutationResult.Awaiting(op,
                $"Delete tag at '{path}'." + XrefImpact.DependentsSuffix(deps, null, orphansNow: false),
                "Re-call tia_tag_delete with confirm=true to proceed.", deps);
        }

        try
        {
            // Gather dependents BEFORE deleting — after deletion the cross-references are gone.
            var deps = await XrefImpact.DependentsOfAsync(_backend, path, ct).ConfigureAwait(false);
            await _backend.DeleteTagAsync(path, ct);
            _audit.Append(op, path, success: true, details: deps.Count > 0 ? string.Join("; ", deps) : null);
            return MutationResult.Applied(
                op, $"Deleted tag '{path}'." + XrefImpact.DependentsSuffix(deps, null, orphansNow: true), deps);
        }
        catch (Exception ex)
        {
            _audit.Append(op, path, success: false, error: ex.Message);
            return MutationResult.Failed(op, ex.Message);
        }
    }
}
