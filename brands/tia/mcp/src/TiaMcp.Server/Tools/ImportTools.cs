using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcp.Contract;
using TiaMcp.Server.Safety;

namespace TiaMcp.Server.Tools;

/// <summary>P7 data-shaping writes: import UDTs / tag tables from SimaticML XML, generate blocks from
/// external source text, and create organizing groups. All need <c>--mode ReadWrite</c>.</summary>
[McpServerToolType]
public sealed class ImportTools
{
    private readonly ITiaBackend _backend;
    private readonly AccessGuard _guard;
    private readonly AuditLog _audit;

    public ImportTools(ITiaBackend backend, AccessGuard guard, AuditLog audit)
    {
        _backend = backend;
        _guard = guard;
        _audit = audit;
    }

    [McpServerTool(Name = "tia_udt_import")]
    [Description(
        "Import one or more UDTs (PLC data types) from SimaticML XML into a PLC (write; needs " +
        "--mode ReadWrite). Returns the names of the data types that appeared.")]
    public Task<MutationResult> TiaUdtImportAsync(
        [Description("PLC scope path, e.g. .../device:PLC_1/plc:program. Append '/typegroup:NAME' to import into a specific PLC-data-type subgroup (folder); omit it to import into the root type folder.")]
        string plcPath,
        [Description("Full SimaticML XML of the UDT(s) (SW.Types.PlcStruct), OR an absolute path to an existing .xml file on disk (exported/extracted UDT) to import directly without inlining the content.")]
        string sourceXml,
        CancellationToken ct = default) =>
        RunImportAsync(TiaOps.ImportUdt, plcPath, () => _backend.ImportUdtAsync(plcPath, sourceXml, ct), "UDT import");

    [McpServerTool(Name = "tia_tagtable_import")]
    [Description(
        "Import a tag table (with its tags) from SimaticML XML into a PLC (write; needs --mode " +
        "ReadWrite). Use this to bulk-load a tag table instead of many tia_tag_create calls.")]
    public Task<MutationResult> TiaTagTableImportAsync(
        [Description("PLC scope path, e.g. .../device:PLC_1/plc:program.")]
        string plcPath,
        [Description("Full SimaticML XML of the tag table (SW.Tags.PlcTagTable).")]
        string sourceXml,
        CancellationToken ct = default) =>
        RunImportAsync(TiaOps.ImportTagTable, plcPath, () => _backend.ImportTagTableAsync(plcPath, sourceXml, ct), "tag-table import");

    [McpServerTool(Name = "tia_block_generate_from_source")]
    [Description(
        "Generate program blocks from external source text (e.g. SCL/AWL) into a PLC via the " +
        "ExternalSources path (write; needs --mode ReadWrite). Unlike tia_block_import (which takes " +
        "SimaticML XML), this accepts raw source text. Returns the blocks that were created.")]
    public Task<MutationResult> TiaBlockGenerateFromSourceAsync(
        [Description("PLC scope path, e.g. .../device:PLC_1/plc:program.")]
        string plcPath,
        [Description("Source name (becomes the external-source object; add an extension like .scl/.awl, else .scl is assumed).")]
        string sourceName,
        [Description("Full source text (e.g. one or more SCL FUNCTION_BLOCK/FUNCTION declarations).")]
        string sourceText,
        CancellationToken ct = default) =>
        RunImportAsync(TiaOps.GenerateBlocks, plcPath,
            () => _backend.GenerateBlocksFromSourceAsync(plcPath, sourceName, sourceText, ct), "source generation");

    [McpServerTool(Name = "tia_group_create")]
    [Description(
        "Create an organizing group/folder under a PLC (write; needs --mode ReadWrite). kind = " +
        "block | type | tagtable. Returns the new group path.")]
    public async Task<MutationResult> TiaGroupCreateAsync(
        [Description("PLC scope path, e.g. .../device:PLC_1/plc:program.")]
        string plcPath,
        [Description("Group name.")]
        string name,
        [Description("block | type | tagtable (default block).")]
        string kind = "block",
        CancellationToken ct = default)
    {
        var op = TiaOps.CreateGroup;
        var decision = _guard.Check(op, confirm: false);
        if (!decision.Allow)
        {
            return MutationResult.Denied(op, decision.DenyReason!);
        }

        try
        {
            var path = await _backend.CreateGroupAsync(plcPath, kind, name, ct);
            _audit.Append(op, path, success: true);
            return MutationResult.Applied(op, $"Created {kind} group '{name}' -> {path}.");
        }
        catch (Exception ex)
        {
            _audit.Append(op, plcPath, success: false, error: ex.Message);
            return MutationResult.Failed(op, ex.Message);
        }
    }

    private async Task<MutationResult> RunImportAsync(
        string op, string plcPath, Func<Task<ImportResult>> action, string label)
    {
        var decision = _guard.Check(op, confirm: false);
        if (!decision.Allow)
        {
            return MutationResult.Denied(op, decision.DenyReason!);
        }

        try
        {
            var result = await action();
            _audit.Append(op, result.ScopePath, success: true);
            return MutationResult.Applied(op, result.Message);
        }
        catch (Exception ex)
        {
            _audit.Append(op, plcPath, success: false, error: ex.Message);
            return MutationResult.Failed(op, $"{label} failed: {ex.Message}");
        }
    }
}
