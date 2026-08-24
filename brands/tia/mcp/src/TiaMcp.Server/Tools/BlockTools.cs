using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcp.Contract;
using TiaMcp.Server.Safety;

namespace TiaMcp.Server.Tools;

/// <summary>PLC program block tools. P1 = read (list/info/read_source/export); P2 = write (import/delete).</summary>
[McpServerToolType]
public sealed class BlockTools
{
    private readonly ITiaBackend _backend;
    private readonly AccessGuard _guard;
    private readonly AuditLog _audit;

    public BlockTools(ITiaBackend backend, AccessGuard guard, AuditLog audit)
    {
        _backend = backend;
        _guard = guard;
        _audit = audit;
    }

    [McpServerTool(Name = "tia_block_list")]
    [Description(
        "List PLC program blocks (OB/FB/FC/DB/UDT) under a scope path (a session, project, device or " +
        "plc). Paginated; each block returns a path string for drill-down.")]
    public Task<object> TiaBlockListAsync(
        [Description("Scope path, e.g. session:s-fake or .../device:PLC_1/plc:program.")]
        string path,
        string? type = null,
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default)
    {
        var realLimit = limit <= 0 ? 50 : Math.Min(limit, 500);
        return ToolErrors.InvokeAsync(() => _backend.ListBlocksAsync(path, BlockTypeParser.TryParse(type), realLimit, Math.Max(0, offset), ct));
    }

    [McpServerTool(Name = "tia_block_info")]
    [Description("Get a block's header: name, type, number, programming language, comment and path.")]
    public Task<object> TiaBlockInfoAsync(string path, CancellationToken ct = default) =>
        ToolErrors.InvokeAsync(() => _backend.GetBlockAsync(path, ct));

    [McpServerTool(Name = "tia_block_read_source")]
    [Description(
        "Read a block's source. On the real Openness backend this returns SimaticML XML (the block's " +
        "full exported representation), NOT plain SCL — for clean SCL text use tia_block_export with " +
        "format=SclSource, or tia_interface_read for a structured member tree. " +
        "On a checksum-inconsistent block the read may trigger a one-off recompile (which writes to " +
        "disk) as automatic recovery so the export succeeds. Use this to understand existing logic " +
        "before modifying it.")]
    public Task<object> TiaBlockReadSourceAsync(string path, CancellationToken ct = default) =>
        ToolErrors.InvokeAsync(() => _backend.ReadBlockSourceAsync(path, ct));

    [McpServerTool(Name = "tia_interface_read")]
    [Description(
        "Read a block's or UDT's interface as a structured member tree (sections -> members, with " +
        "datatype, start value and comment; nested for structs) — instead of raw SimaticML XML. " +
        "Pass a block path; a UDT resolves by the same name. On an inconsistent block the underlying " +
        "export may trigger a one-off recompile (writes to disk) to recover. Read-only.")]
    public Task<object> TiaInterfaceReadAsync(
        [Description("Block path, e.g. .../plc:program/block:OP10_HMI (a UDT resolves by name).")] string path,
        CancellationToken ct = default) =>
        ToolErrors.InvokeAsync(() => _backend.ReadInterfaceAsync(path, ct));

    [McpServerTool(Name = "tia_udt_list")]
    [Description(
        "List the PLC data types (UDTs) under a scope path, recursing type user-groups. Each entry " +
        "carries its group path and a block path for drill-down (tia_interface_read). Read-only.")]
    public Task<object> TiaUdtListAsync(
        [Description("Scope path, e.g. session:s-fake or .../device:PLC_1/plc:program.")] string path,
        CancellationToken ct = default) =>
        ToolErrors.InvokeAsync(() => _backend.ListUdtsAsync(path, ct));

    [McpServerTool(Name = "tia_cross_reference")]
    [Description(
        "Cross-references for a block: what it uses and where it is used, each with reference type " +
        "(Uses/UsedBy/…) and access (Read/Write/Call/…). Read-only. Use to understand impact before editing. " +
        "aggregate=true collapses each entry's per-access locations into counts (much smaller response — " +
        "the full form lists one location per single access); limit caps the number of reference entries " +
        "returned (total stays in count; truncated=true when clipped) — use both on large callers.")]
    public async Task<object> TiaCrossReferenceAsync(
        [Description("Block path, e.g. .../plc:program/block:OP10_Valve.")] string path,
        [Description("Collapse each entry's locations into per referenceType/access counts (default false = full list).")]
        bool aggregate = false,
        [Description("Cap on reference entries returned (default = all). When clipping, count holds the total and truncated=true.")]
        int? limit = null,
        CancellationToken ct = default)
    {
        var result = await ToolErrors.InvokeAsync(() => _backend.GetCrossReferencesAsync(path, ct))
            .ConfigureAwait(false);
        if (result is not CrossRefResult xref)
        {
            return result; // ToolError passthrough
        }

        var entries = xref.References;
        var truncated = false;
        if (limit is > 0 && entries.Count > limit.Value)
        {
            entries = entries.Take(limit.Value).ToList();
            truncated = true;
        }

        if (!aggregate)
        {
            return new { xref.Path, xref.Count, truncated, references = entries };
        }

        // Aggregate: one count per (referenceType, access) pair per entry — the "who/how many" view
        // without per-access location noise (a hot caller block can repeat the same access dozens of times).
        var slim = entries.Select(e => new
        {
            e.Name,
            e.Path,
            e.TypeName,
            e.Address,
            counts = e.Locations
                .GroupBy(l => (l.ReferenceType, l.Access))
                .OrderByDescending(g => g.Count())
                .ToDictionary(g => $"{g.Key.ReferenceType}/{g.Key.Access}", g => g.Count()),
        }).ToList();
        return new { xref.Path, xref.Count, truncated, references = slim };
    }

    [McpServerTool(Name = "tia_block_export")]
    [Description("Export a block (or a UDT/PLC data type) to a file (SclSource = .scl text [blocks only], Xml = SimaticML). UDTs resolve by the same name and export as Xml. Returns the absolute file path written.")]
    public Task<object> TiaBlockExportAsync(
        string path,
        string format = "SclSource",
        string? outDir = null,
        CancellationToken ct = default) =>
        ToolErrors.InvokeAsync(() => _backend.ExportBlockAsync(
            path, EnumText.TryParse<ExportFormat>(format) ?? ExportFormat.SclSource, outDir, ct));

    [McpServerTool(Name = "tia_block_import")]
    [Description(
        "Import a block from SimaticML XML into a PLC (write; needs --mode ReadWrite). " +
        "The `source` must be SimaticML XML (the format tia_block_export produces with format=Xml), " +
        "NOT raw SCL — for raw SCL/AWL text use tia_block_generate_from_source instead. " +
        "If a block with that name already exists it is OVERRIDDEN (ImportOptions.Override) — re-import " +
        "is idempotent, no need to delete first. Returns the new block path.")]
    public async Task<MutationResult> TiaBlockImportAsync(
        [Description("PLC scope path, e.g. .../device:PLC_1/plc:program. Append '/blockgroup:NAME' to import into a specific block subgroup (folder); omit it for the root block folder.")]
        string plcPath,
        [Description("New block name (no quotes), e.g. FB_Valve.")]
        string name,
        [Description("Full SimaticML XML of the block (as exported by tia_block_export format=Xml), OR an absolute path to an existing .xml file on disk to import directly without inlining the content. " +
            "The XML carries the block type and language, so the type/language params below are ignored. " +
            "For raw SCL text, use tia_block_generate_from_source instead.")]
        string source,
        [Description("Ignored — the SimaticML XML carries the block type. Kept for compatibility (default FB).")]
        string type = "FB",
        [Description("Ignored — the SimaticML XML carries the language. Kept for compatibility (default SCL).")]
        string language = "SCL",
        CancellationToken ct = default)
    {
        var op = TiaOps.ImportBlock;
        var decision = _guard.Check(op, confirm: false);
        if (!decision.Allow)
        {
            return MutationResult.Denied(op, decision.DenyReason!);
        }

        try
        {
            var req = new CreateBlockRequest(
                plcPath, name, BlockTypeParser.TryParse(type) ?? BlockType.FB, source, language);
            var path = await _backend.ImportBlockAsync(plcPath, req, ct);
            _audit.Append(op, path, success: true);
            return MutationResult.Applied(op, $"Imported block '{name}' -> {path}.");
        }
        catch (Exception ex)
        {
            _audit.Append(op, plcPath, success: false, error: ex.Message);
            return MutationResult.Failed(op, ex.Message);
        }
    }

    [McpServerTool(Name = "tia_block_delete")]
    [Description(
        "Delete a block (destructive; needs --mode ReadWrite AND confirm=true). " +
        "Without confirm it returns a preview and does nothing. Preview AND result report the block's " +
        "dependents (who calls it, which instance DBs type it) from cross-references, so a delete that " +
        "would break callers is visible before confirming.")]
    public async Task<MutationResult> TiaBlockDeleteAsync(
        [Description("Block path to delete.")]
        string path,
        [Description("Set to true to actually delete. Default false returns a preview.")]
        bool confirm = false,
        CancellationToken ct = default)
    {
        var op = TiaOps.DeleteBlock;
        var decision = _guard.Check(op, confirm);
        if (!decision.Allow)
        {
            if (!decision.NeedsConfirm)
            {
                return MutationResult.Denied(op, decision.DenyReason!);
            }

            var (dependents, usedTypes) = await DescribeDeleteImpactAsync(path, ct).ConfigureAwait(false);
            return MutationResult.Awaiting(op,
                $"Delete block at '{path}'." + XrefImpact.DependentsSuffix(dependents, usedTypes, orphansNow: false),
                "Re-call tia_block_delete with confirm=true to proceed.", dependents);
        }

        try
        {
            // Gather dependents BEFORE deleting — after deletion the cross-references are gone.
            var (dependents, usedTypes) = await DescribeDeleteImpactAsync(path, ct).ConfigureAwait(false);
            await _backend.DeleteBlockAsync(path, ct);
            _audit.Append(op, path, success: true, details: dependents.Count > 0 ? string.Join("; ", dependents) : null);
            return MutationResult.Applied(
                op, $"Deleted block '{path}'." + XrefImpact.DependentsSuffix(dependents, usedTypes, orphansNow: true),
                dependents);
        }
        catch (Exception ex)
        {
            _audit.Append(op, path, success: false, error: ex.Message);
            return MutationResult.Failed(op, ex.Message);
        }
    }

    /// <summary>Deletion impact from the block's cross-references: direct dependents (what a delete
    /// BREAKS — see <see cref="XrefImpact"/>) plus the user PLC data types it declares with
    /// (Uses/Declaration into 'PLC data types' — what a delete may ORPHAN).</summary>
    private async Task<(List<string> Dependents, List<string> UsedTypes)> DescribeDeleteImpactAsync(
        string path, CancellationToken ct)
    {
        try
        {
            var xref = await _backend.GetCrossReferencesAsync(path, ct).ConfigureAwait(false);
            var usedTypes = xref.References
                .Where(e => e.Path is not null
                            && e.Path.Contains("PLC data types")
                            && !e.Path.Contains("System data types")
                            && e.Locations.Any(l => l.ReferenceType == "Uses" && l.Access == "Declaration"))
                .Select(e => e.Name)
                .ToList();
            return (XrefImpact.ExtractDependents(xref), usedTypes);
        }
        catch
        {
            return (new List<string>(), new List<string>());
        }
    }
}
