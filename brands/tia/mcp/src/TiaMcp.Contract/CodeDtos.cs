namespace TiaMcp.Contract;

// --- #7: structured block body read/write (compact LAD/GRAPH/SCL view behind
//          tia_block_read_code / tia_block_write_code) ---

/// <summary>
/// LAD boolean expression tree node, shared by the structured read (<c>networks[].logic</c>) and the
/// write spec (<c>networks[].rungs[].logic</c>) so a read result can be edited and written back.
/// <para><c>Op</c> is <c>"contact"</c> (leaf: <c>Operand</c>, optional <c>Negated</c>, optional
/// <c>Edge</c> = <c>"rising"</c>|<c>"falling"</c>) or <c>"and"</c>/<c>"or"</c> (inner: <c>Args</c>).
/// Plain System.Text.Json round-trips it with no custom converter; ill-formed trees are rejected by
/// the generator, not the type.</para>
/// </summary>
public sealed record LadExpr(
    string Op,
    string? Operand = null,
    bool Negated = false,
    string? Edge = null,
    IReadOnlyList<LadExpr>? Args = null);

/// <summary>One output element of a LAD network. <c>Kind</c> is <c>"coil"</c> | <c>"set"</c> | <c>"reset"</c>.</summary>
public sealed record NetworkOutput(string Kind, string Operand);

/// <summary>
/// One box instruction (timer / MOVE / compare / arithmetic / call…) with the operands actually bound
/// to its pins, e.g. <c>{ part = "TON", instance = "DelayTimer", pins = { pt = "T#3S" } }</c>.
/// Pin names come from the source XML itself, so unknown boxes still dump faithfully.
/// </summary>
public sealed record NetworkBox(string Part, string? Instance, IReadOnlyDictionary<string, string> Pins);

/// <summary>A part rendered as part of a <see cref="NetworkFallback"/> structural dump.</summary>
public sealed record FallbackPart(long UId, string Name, string? Operand, string? Instance, IReadOnlyList<string> Negated);

/// <summary>A wire rendered as part of a <see cref="NetworkFallback"/> structural dump; endpoints are
/// <c>"powerrail"</c>, <c>"&lt;uid&gt;.&lt;pin&gt;"</c>, or <c>"open"</c>.</summary>
public sealed record FallbackWire(string From, string To);

/// <summary>
/// Structural dump of a network whose topology the renderer does not understand — parts and wires
/// listed verbatim, with the reason rendering stopped. Never a guessed expression.
/// </summary>
public sealed record NetworkFallback(string Reason, IReadOnlyList<FallbackPart> Parts, IReadOnlyList<FallbackWire> Wires);

/// <summary>
/// One network of a block body as a compact structured view. <c>Kind</c> is <c>"lad"</c> | <c>"fbd"</c> |
/// <c>"scl"</c>. Pure boolean networks get <c>Render</c> + <c>Logic</c>; networks with boxes inline them
/// into <c>Render</c> and list them in <c>Boxes</c>; SCL networks carry flattened text in <c>Text</c>;
/// anything unsupported degrades to <c>Fallback</c>.
/// </summary>
public sealed record NetworkCode(
    int Index,
    string Kind,
    string? Title,
    string? Comment,
    string? Render,
    LadExpr? Logic,
    IReadOnlyList<NetworkOutput> Outputs,
    IReadOnlyList<NetworkBox> Boxes,
    NetworkFallback? Fallback,
    string? Text);

/// <summary>GRAPH sequencer view: steps with actions, transitions with conditions, and whether
/// the sequence is closed (a Jump connection back to the initial step — the read-side mirror of
/// the write spec's <c>loop: true</c>).</summary>
public sealed record GraphCode(
    IReadOnlyList<GraphStep> Steps,
    IReadOnlyList<GraphTransition> Transitions,
    string? FallbackNote,
    bool Loop = false);

/// <summary>One GRAPH step: actions (qualifier N/R/S/D/L/… + operand) and best-effort interlock /
/// supervision expressions (rendered from their per-step FlgNet subnetworks).</summary>
public sealed record GraphStep(
    int Number, string Name, bool Init, IReadOnlyList<GraphAction> Actions,
    string? Interlock, string? Supervision);

public sealed record GraphAction(string Qualifier, string Operand);

public sealed record GraphTransition(int Number, string Name, string? Condition);

/// <summary>
/// Compact structured body of a block — what <c>tia_block_read_code</c> returns instead of raw
/// SimaticML XML. <c>Graph</c> is set for GRAPH blocks; code blocks (LAD/FBD/SCL) carry
/// <c>Networks</c>. <c>Interface</c> reuses the structured member tree (null when excluded).
/// </summary>
public sealed record BlockCode(
    string Path,
    string Name,
    string BlockType,
    string Language,
    IReadOnlyList<InterfaceSection>? Interface,
    IReadOnlyList<NetworkCode> Networks,
    GraphCode? Graph,
    IReadOnlyList<string> Warnings);

/// <summary>Range/filter options for <c>tia_block_read_code</c>. Nulls = no filtering.</summary>
public sealed record ReadCodeOptions(int? NetworkFrom, int? NetworkTo, bool IncludeInterface);
