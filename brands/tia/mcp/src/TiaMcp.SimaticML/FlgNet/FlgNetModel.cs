namespace TiaMcp.SimaticML.FlgNet;

/// <summary>One endpoint a wire can touch: the left power rail, a part pin, an access (operand), or a
/// dangling open connection.</summary>
internal abstract record FlgEndpoint;

internal sealed record PowerrailEnd : FlgEndpoint;

internal sealed record PinEnd(long PartUId, string Pin) : FlgEndpoint;

internal sealed record AccessEnd(long AccessUId) : FlgEndpoint;

internal sealed record OpenEnd : FlgEndpoint;

/// <summary>
/// One FlgNet access (an operand reference). <c>Text</c> is the display form: <c>#Name</c> for
/// LocalVariable, plain name for GlobalVariable, the literal for TypedConstant, raw text otherwise.
/// </summary>
internal sealed record AccessNode(long UId, string Scope, string Text);

/// <summary>
/// One FlgNet part (an instruction: Contact, Coil, O, TON, Move, …). <c>Negated</c> holds pin names
/// negated via <c>&lt;Negated Name="operand"/&gt;</c>; <c>TemplateValues</c> holds template values
/// (Cardinality, time_type, …); <c>BoundOperand</c> is filled by the parser from the
/// IdentCon→NameCon(operand) wire.
/// </summary>
internal sealed class PartNode
{
    public required long UId { get; init; }
    public required string Name { get; init; }
    public string? Version { get; init; }
    public string? Instance { get; init; }
    public required HashSet<string> Negated { get; init; }
    public required Dictionary<string, string> TemplateValues { get; init; }
    public string? BoundOperand { get; set; }
}

/// <summary>One wire: a source endpoint plus every target endpoint it feeds. Child order in the XML
/// carries direction (first child = source), and Powerrail/IdentCon/OpenCon are sources by construction.</summary>
internal sealed record WireNode(long UId, FlgEndpoint Source, IReadOnlyList<FlgEndpoint> Targets);

/// <summary>Normalized view of one FlgNet network: accesses, parts, wires keyed by part UId.</summary>
internal sealed class FlgNetModel
{
    public required Dictionary<long, AccessNode> Accesses { get; init; }
    public required Dictionary<long, PartNode> Parts { get; init; }
    public required List<WireNode> Wires { get; init; }

    /// <summary>The (at most one per network) wire whose source is the power rail; null if absent.</summary>
    public WireNode? PowerrailWire => Wires.FirstOrDefault(w => w.Source is PowerrailEnd);

    /// <summary>Wires whose source is the given part pin.</summary>
    public IEnumerable<WireNode> WiresFrom(FlgEndpoint src) => Wires.Where(w => Equals(w.Source, src));

    /// <summary>Wires that feed the given part pin (it appears as a target).</summary>
    public IEnumerable<WireNode> WiresTo(FlgEndpoint dst) => Wires.Where(w => w.Targets.Contains(dst));

    public string? AccessText(FlgEndpoint? ep) =>
        ep is AccessEnd a && Accesses.TryGetValue(a.AccessUId, out var acc) ? acc.Text : null;
}
