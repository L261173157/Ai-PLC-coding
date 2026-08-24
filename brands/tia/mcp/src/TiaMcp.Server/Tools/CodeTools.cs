using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using TiaMcp.Contract;
using TiaMcp.Server.Safety;

namespace TiaMcp.Server.Tools;

/// <summary>
/// Structured block-BODY read/write (LAD / FBD / SCL / GRAPH) — the compact alternative to
/// tia_block_read_source's raw SimaticML XML and to hand-editing FlgNet by hand. Parsing and
/// generation run net10-side on top of the existing block-source / import RPCs, so the net48
/// Openness worker is not involved beyond what those tools already use.
/// </summary>
[McpServerToolType]
public sealed class CodeTools
{
    private static readonly JsonSerializerOptions SpecJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ITiaCodeBackend _backend;
    private readonly AccessGuard _guard;
    private readonly AuditLog _audit;

    public CodeTools(ITiaCodeBackend backend, AccessGuard guard, AuditLog audit)
    {
        _backend = backend;
        _guard = guard;
        _audit = audit;
    }

    [McpServerTool(Name = "tia_block_read_code")]
    [Description(
        "Read a block's BODY as a compact structured view instead of raw SimaticML XML: LAD/FBD " +
        "networks render as boolean expressions + output coils + box listings (timers / MOVE / " +
        "compares …), SCL bodies come back as flattened text, GRAPH blocks return a step/transition " +
        "view. Pure contact/coil networks also carry an editable `logic` expression tree (the same " +
        "shape tia_block_write_code accepts). Anything the parser cannot prove degrades to a " +
        "structural `fallback` listing — it never guesses an expression. On a checksum-inconsistent " +
        "block the underlying export may trigger a one-off recompile (same as tia_block_read_source).")]
    public Task<object> TiaBlockReadCodeAsync(
        [Description("Block path, e.g. .../plc:program/block:FB_Motor.")] string path,
        [Description("First network index to include (inclusive), for paging large bodies.")] int? networkFrom = null,
        [Description("Last network index to include (inclusive).")] int? networkTo = null,
        [Description("Include the interface member tree in the result (default true; false = body only).")]
        bool includeInterface = true,
        CancellationToken ct = default) =>
        ToolErrors.InvokeAsync(() =>
            _backend.ReadBlockCodeAsync(path, new ReadCodeOptions(networkFrom, networkTo, includeInterface), ct));

    [McpServerTool(Name = "tia_block_write_code")]
    [Description(
        "Write a LAD or GRAPH block from a STRUCTURED JSON SPEC (write; needs --mode ReadWrite): the " +
        "spec is compiled net10-side into SimaticML XML and imported through the same path as " +
        "tia_block_import (ImportOptions.Override — re-writing the same name is idempotent, no delete " +
        "first). ⚠ Pick by INPUT TYPE, not by 'writing a block': a JSON spec with networks/sequence → " +
        "this tool; raw SCL/AWL text → tia_block_generate_from_source; SimaticML XML → tia_block_import. " +
        "LAD v1 instruction set: contacts (NO/NC), nested and/or trees, coil/set/reset, IEC " +
        "timers ton/tof/tp (multi-instance + PT, on FB), move, inline compares eq/ne/ge/gt/le/lt. " +
        "GRAPH: a linear sequence (machine-verified from-scratch shape, compiles 0 errors on TIA V21) " +
        "via `sequence: [{ name, actions: [{ qualifier: N|R|S|D|L|…, operand }], transitionOperand }]` " +
        "— at most ONE action per step (TIA V21 XML import rejects more; split extra actions into " +
        "their own steps or drive the operand from the calling OB). Set `loop: true` to close the " +
        "sequence with a Jump connection back to the initial step (machine-verified circular " +
        "sequencer). The first non-standard Input member doubles as the mandatory supervision/interlock " +
        "condition. Use dryRun=true to get the generated XML without importing (allowed in ReadOnly). " +
        "FBD is read-only.")]
    public async Task<object> TiaBlockWriteCodeAsync(
        [Description("PLC scope path, e.g. .../device:PLC_1/plc:program. Append '/blockgroup:NAME' to target a block subgroup.")]
        string plcPath,
        [Description(
            "Block spec as a JSON string: { name, blockType: FB|FC, language: LAD|GRAPH, comment?, " +
            "interface: [{ section, members: [{ name, datatype, comment?, startValue? }] }], " +
            "networks: [{ title?, rungs: [{ logic: <expr>, output: <out> }] }] (LAD) | " +
            "sequence: [{ name, actions: [{qualifier, operand}], transitionOperand? }] (GRAPH) | " +
            "loop?: true (GRAPH: last transition jumps back to the initial step) }. " +
            "NOT raw SCL text (that is tia_block_generate_from_source) and NOT SimaticML XML (tia_block_import). " +
            "logic expr: { op: contact, operand, negated? } | { op: and|or, args: [expr,…] }. " +
            "output: { kind: coil|set|reset, operand } | { kind: ton|tof|tp, instance, pt, q?: coil } | " +
            "{ kind: move, src, dst } | { kind: compare, part: eq|ne|ge|gt|le|lt, in1, in2, out?: coil }.")]
        string specJson,
        [Description("Overrides the spec's name (optional).")] string? name = null,
        [Description("Generate the SimaticML XML and return it WITHOUT importing (no guard, works in ReadOnly).")]
        bool dryRun = false,
        CancellationToken ct = default)
    {
        var op = TiaOps.WriteBlockCode;

        CodeBlockSpec spec;
        try
        {
            spec = ParseSpec(specJson);
            if (!string.IsNullOrEmpty(name))
            {
                spec = spec with { Name = name };
            }
        }
        catch (Exception ex)
        {
            return MutationResult.Failed(op, $"specJson is not a valid block spec: {ex.Message}");
        }

        if (dryRun)
        {
            try
            {
                var xml = await _backend.GenerateBlockCodeAsync(spec, ct);
                return new DryRunResult("Applied", op,
                    $"dry-run: generated SimaticML for '{spec.Name}' (nothing imported).", xml);
            }
            catch (Exception ex)
            {
                return MutationResult.Failed(op, ex.Message);
            }
        }

        var decision = _guard.Check(op, confirm: false);
        if (!decision.Allow)
        {
            return MutationResult.Denied(op, decision.DenyReason!);
        }

        try
        {
            var path = await _backend.WriteBlockCodeAsync(plcPath, spec, ct);
            _audit.Append(op, path, success: true);
            return MutationResult.Applied(op, $"Wrote block '{spec.Name}' from spec -> {path}.");
        }
        catch (Exception ex)
        {
            _audit.Append(op, plcPath, success: false, error: ex.Message);
            return MutationResult.Failed(op, ex.Message);
        }
    }

    private static CodeBlockSpec ParseSpec(string specJson)
    {
        var trimmed = specJson?.Trim() is { Length: > 0 } t ? t
            : throw new ArgumentException("specJson is empty.");
        if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}'))
        {
            throw new ArgumentException("specJson must be a JSON object.");
        }

        return JsonSerializer.Deserialize<CodeBlockSpec>(trimmed, SpecJsonOptions)
               ?? throw new ArgumentException("specJson deserialized to null.");
    }

    /// <summary>Dry-run payload: a MutationResult-shaped envelope carrying the generated XML.</summary>
    public sealed record DryRunResult(string Status, string Operation, string Message, string Xml);
}
