namespace TiaMcp.Contract;

// --- #7 write side: structured block spec consumed by tia_block_write_code ---

/// <summary>
/// Structured block description that <c>tia_block_write_code</c> compiles into SimaticML XML and
/// imports via the existing (Override, idempotent) block-import path. LAD v1 instruction set:
/// contacts (NO / NC — edge contacts are not yet supported for writing), nested and/or trees,
/// coil / set / reset outputs, IEC timers (ton/tof/tp with multi-instance + PT), MOVE, and inline
/// compares (eq/ne/ge/gt/le/lt). GRAPH writes a linear sequence (machine-verified from-scratch
/// shape; TIA auto-appends the GRAPH runtime interface on import).
/// </summary>
public sealed record CodeBlockSpec(
    string Name,
    string BlockType,
    string Language,
    string? Comment,
    IReadOnlyList<SpecSection>? Interface,
    IReadOnlyList<SpecNetwork>? Networks,
    IReadOnlyList<GraphStepSpec>? Sequence);

/// <summary>
/// One GRAPH step for the write spec: a name, actions (qualifier N/R/S/D/L/… + operand, by default
/// N), and the operand of the transition condition that leaves the step (a Bool operand name —
/// the transition becomes a plain contact on it; null = keep the template's condition).
/// </summary>
public sealed record GraphStepSpec(
    string Name,
    IReadOnlyList<GraphActionSpec>? Actions,
    string? TransitionOperand);

public sealed record GraphActionSpec(string Qualifier, string Operand);

/// <summary>One interface section (Input / Output / InOut / Static / Temp / Constant).</summary>
public sealed record SpecSection(string Section, IReadOnlyList<SpecMember>? Members);

public sealed record SpecMember(string Name, string Datatype, string? Comment, string? StartValue);

/// <summary>One network: a title/comment plus independent rungs (branches sharing the power rail).</summary>
public sealed record SpecNetwork(string? Title, string? Comment, IReadOnlyList<SpecRung>? Rungs);

/// <summary>One rung: a boolean <see cref="LadExpr"/> tree feeding one output element.</summary>
public sealed record SpecRung(LadExpr? Logic, SpecOutput? Output);

/// <summary>
/// The output element a rung ends in. <c>Kind</c>: <c>coil|set|reset</c> (operand required),
/// <c>ton|tof|tp</c> (instance + pt; optional <c>q</c> coil), <c>move</c> (src → dst),
/// <c>compare</c> (part eq/ne/ge/gt/le/lt + in1/in2, optional chained <c>out</c> coil).
/// </summary>
public sealed record SpecOutput(
    string Kind,
    string? Operand = null,
    string? Instance = null,
    string? Pt = null,
    SpecCoil? Q = null,
    string? Part = null,
    string? In1 = null,
    string? In2 = null,
    string? Src = null,
    string? Dst = null,
    SpecCoil? Out = null);

/// <summary>A coil element: kind <c>coil|set|reset</c> plus its operand.</summary>
public sealed record SpecCoil(string Kind, string Operand);
