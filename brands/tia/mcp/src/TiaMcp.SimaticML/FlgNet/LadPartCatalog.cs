namespace TiaMcp.SimaticML.FlgNet;

/// <summary>
/// Known FlgNet part names and their pin semantics. Names cover everything seen in the real-project
/// corpus (86 LAD blocks) plus the hand-verified skill examples; unknown names are still dumped
/// structurally — the catalog only decides whether a network can be rendered as an expression.
/// </summary>
internal static class LadPartCatalog
{
    /// <summary>Contact family: serial energy flow with one operand pin.</summary>
    internal static readonly HashSet<string> Contacts = new()
    {
        "Contact", "PContact", "NContact",
    };

    /// <summary>Coil family: terminal energy-flow output with one operand pin.</summary>
    internal static readonly HashSet<string> Coils = new()
    {
        "Coil", "SCoil", "RCoil",
    };

    /// <summary>GRAPH subnet terminator coils (machine-verified on real TIA V21): SvCoil ends a
    /// step's supervision condition, IlCoil its interlock, TrCoil a transition condition. They carry
    /// NO operand — their meaning comes from the enclosing step/transition, so operand-less output
    /// entries are allowed for them (rendered (SV)/(IL)/(TR)).</summary>
    internal static readonly HashSet<string> GraphCoils = new()
    {
        "SvCoil", "IlCoil", "TrCoil",
    };

    /// <summary>Boolean merge parts with dynamic inputs in1..inN and one output.</summary>
    internal static readonly HashSet<string> Merges = new()
    {
        "O",
    };

    /// <summary>Inline compare parts (S7-1200 dialect): pins pre (power in) / in1 / in2 / out (power
    /// out) — they sit IN the energy chain like a contact, not as en/eno boxes. S7-1500 LAD uses the
    /// same shape. Renderable as inline terms ("a == b").</summary>
    internal static readonly HashSet<string> Compares = new()
    {
        "Eq", "Ne", "Ge", "Gt", "Le", "Lt",
    };

    /// <summary>IEC timers with an instance and PT/Q/ET pins (renderable as boxes inline in the rung).</summary>
    internal static readonly HashSet<string> Timers = new()
    {
        "TON", "TOF", "TP",
    };

    /// <summary>Dataflow boxes: MOVE / arithmetic / convert / calls. Their pins are read
    /// from the wires themselves; they never block a network from rendering its boolean backbone.</summary>
    internal static readonly HashSet<string> Boxes = new()
    {
        "Move",
        "Add", "Sub", "Mul", "Div",
        "Convert", "T_CONV", "T_DIFF", "RD_SYS_T", "RD_LOC_T",
        "Call", "CoilTON", "PBox", "GetInstanceName",
        "CTU", "CTD", "CTUD",
    };

    /// <summary>Every name the catalog knows (anything else renders a fallback).</summary>
    internal static bool IsKnown(string name) =>
        Contacts.Contains(name) || Coils.Contains(name) || GraphCoils.Contains(name) ||
        Merges.Contains(name) || Compares.Contains(name) || Timers.Contains(name) || Boxes.Contains(name);

    /// <summary>Whether the part participates in the boolean energy-flow backbone rendering.</summary>
    internal static bool IsRenderable(string name) =>
        Contacts.Contains(name) || Coils.Contains(name) || GraphCoils.Contains(name) ||
        Merges.Contains(name) || Compares.Contains(name) || Timers.Contains(name);

    /// <summary>Whether the part is a terminal coil WITHOUT an operand pin (GRAPH subnet coils).</summary>
    internal static bool IsOperandLessCoil(string name) => GraphCoils.Contains(name);

    /// <summary>Power-input pin of a contact/compare part — plain Contact/Coil use "in", edge
    /// contacts (PContact/NContact) and inline compares use "pre" (S7-1200 corpus dialect).</summary>
    internal static string? PowerInPin(string name) => name switch
    {
        "PContact" or "NContact" => "pre",
        "Eq" or "Ne" or "Ge" or "Gt" or "Le" or "Lt" => "pre",
        "Contact" => "in",
        "Coil" or "SCoil" or "RCoil" => "in",
        _ => null,
    };

    /// <summary>First power-input pin that a part family could be fed on ("in", then "pre").</summary>
    internal static string[] PowerInCandidates(string name) =>
        PowerInPin(name) is { } pin ? new[] { pin } : new[] { "in", "pre" };

    internal static string CoilKind(string partName) => partName switch
    {
        "SCoil" => "set",
        "RCoil" => "reset",
        "SvCoil" => "supervision",
        "IlCoil" => "interlock",
        "TrCoil" => "transition",
        _ => "coil",
    };

    internal static string CoilSymbol(string kind) => kind switch
    {
        "set" => "(S)",
        "reset" => "(R)",
        "supervision" => "(SV)",
        "interlock" => "(IL)",
        "transition" => "(TR)",
        _ => "( )",
    };
}
