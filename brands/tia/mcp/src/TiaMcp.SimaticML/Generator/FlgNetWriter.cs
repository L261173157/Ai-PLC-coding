using System.Xml.Linq;
using TiaMcp.Contract;

namespace TiaMcp.SimaticML.Generator;

/// <summary>A part pin reference ((part UId, pin name)) used while wiring a network.</summary>
internal sealed record PinRef(long UId, string Pin);

/// <summary>
/// Compiles the rungs of one spec network into a FlgNet element, obeying the four iron wiring rules
/// from the verified examples: one powerrail wire per network (every rail-connected head is appended
/// to it as a target), one wire per source pin (multi-target only from one source), coils/timers fed
/// by energy flow, typed constants via the right Access scope. Shapes are byte-modeled on the two
/// compile-verified skill examples (启保停 FC + 定时器 FB) and the S7-1200 corpus extracts
/// (MOVE en/in/out1, inline compares pre/in1/in2/out with SrcType).
/// </summary>
internal sealed class FlgNetWriter
{
    private const string FlgNetNs = "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5";

    private readonly UidAllocator _uid = new();
    private readonly List<XElement> _accesses = new();
    private readonly List<XElement> _parts = new();
    private readonly List<XElement> _wires = new();
    private readonly List<XElement> _railTargets = new();
    private readonly HashSet<string> _locals;
    private readonly Dictionary<string, string> _datatypes;

    public FlgNetWriter(HashSet<string> locals, Dictionary<string, string> datatypes)
    {
        _locals = locals;
        _datatypes = datatypes;
    }

    public XElement BuildNetwork(IReadOnlyList<SpecRung> rungs)
    {
        foreach (var rung in rungs)
        {
            if (rung.Output is null)
            {
                throw new ArgumentException("spec rung is missing its output element.");
            }

            PinRef? feed = null;
            if (rung.Logic is not null)
            {
                var (heads, tail) = EmitExpr(rung.Logic);
                // The tree's left-most inputs (parallel branch heads included) go to the rail;
                // the tree's single output tail feeds the rung's output element.
                foreach (var h in heads)
                {
                    _railTargets.Add(new XElement("NameCon", new XAttribute("UId", h.UId), new XAttribute("Name", h.Pin)));
                }

                feed = tail;
            }

            EmitOutput(rung.Output, feed);
        }

        if (_railTargets.Count > 0)
        {
            // Iron rule 1: exactly one powerrail wire per network; every rail-connected input pin
            // rides it as an additional target (启保停 wire 34 pattern).
            _wires.Add(new XElement("Wire", new XAttribute("UId", _uid.Next()),
                new object[] { new XElement("Powerrail") }.Concat(_railTargets)));
        }

        return new XElement(XName.Get("FlgNet", FlgNetNs),
            new XElement(XName.Get("Parts", FlgNetNs), _accesses.Concat(_parts)),
            new XElement(XName.Get("Wires", FlgNetNs), _wires));
    }

    // ----- expression tree -> contact / O topology -----

    /// <summary>Returns the input pins the upstream (or rail) must feed, and the chain's output pin.</summary>
    private (List<PinRef> Heads, PinRef Tail) EmitExpr(LadExpr e)
    {
        switch (e.Op)
        {
            case "contact":
            {
                if (string.IsNullOrEmpty(e.Operand))
                {
                    throw new ArgumentException("spec contact is missing its operand.");
                }

                if (!string.IsNullOrEmpty(e.Edge))
                {
                    throw new ArgumentException(
                        $"edge contact '{e.Operand}': edge contacts are not in the v1 write subset " +
                        "(their FlgNet dialect carries an edge-memory bit pin that is not safely " +
                        "generatable yet — edit via template or use a plain contact).");
                }

                var uid = AddPart("Contact", negatedOperand: e.Negated);
                BindOperand(uid, "operand", e.Operand);
                return (new List<PinRef> { new(uid, "in") }, new PinRef(uid, "out"));
            }

            case "and":
            {
                var args = RequireArgs(e);
                var first = EmitExpr(args[0]);
                var heads = first.Heads;
                var tail = first.Tail;
                for (var i = 1; i < args.Count; i++)
                {
                    var next = EmitExpr(args[i]);
                    // Series chaining: the previous tail feeds EVERY input head of the next element
                    // (an `or` next in series has several parallel heads — one wire, many targets).
                    _wires.Add(Wire(tail, next.Heads));
                    tail = next.Tail;
                }

                return (heads, tail);
            }

            case "or":
            {
                var args = RequireArgs(e);
                var heads = new List<PinRef>();
                var tails = new List<PinRef>();
                foreach (var arg in args)
                {
                    var emitted = EmitExpr(arg);
                    heads.AddRange(emitted.Heads);
                    tails.Add(emitted.Tail);
                }

                var card = tails.Count;
                var o = new XElement("Part", new XAttribute("Name", "O"), new XAttribute("UId", _uid.Next()),
                    new XElement("TemplateValue",
                        new XAttribute("Name", "Card"), new XAttribute("Type", "Cardinality"), card.ToString()));
                var oUid = (long)o.Attribute("UId")!;
                _parts.Add(o);
                for (var k = 0; k < card; k++)
                {
                    _wires.Add(Wire(tails[k], new[] { new PinRef(oUid, $"in{k + 1}") }));
                }

                return (heads, new PinRef(oUid, "out"));
            }

            default:
                throw new ArgumentException($"spec logic node op '{e.Op}' is not contact|and|or.");
        }
    }

    private static List<LadExpr> RequireArgs(LadExpr e)
    {
        var args = (e.Args ?? Array.Empty<LadExpr>()).Where(a => a is not null).ToList();
        if (args.Count == 0)
        {
            throw new ArgumentException($"spec logic node op '{e.Op}' needs at least one arg.");
        }

        return args;
    }

    // ----- output elements -----

    private void EmitOutput(SpecOutput o, PinRef? feed)
    {
        switch (o.Kind)
        {
            case "coil" or "set" or "reset":
            {
                if (string.IsNullOrEmpty(o.Operand))
                {
                    throw new ArgumentException($"spec {o.Kind} output is missing its operand.");
                }

                var uid = AddPart(o.Kind == "coil" ? "Coil" : o.Kind == "set" ? "SCoil" : "RCoil");
                ConnectFeed(uid, "in", feed);
                BindOperand(uid, "operand", o.Operand!);
                break;
            }

            case "ton" or "tof" or "tp":
            {
                if (string.IsNullOrEmpty(o.Instance))
                {
                    throw new ArgumentException($"spec {o.Kind} output is missing its instance.");
                }

                var name = o.Kind.ToUpperInvariant();
                var instanceUid = _uid.Next();
                var part = new XElement("Part",
                    new XAttribute("Name", name), new XAttribute("Version", "1.0"), new XAttribute("UId", _uid.Next()),
                    new XElement("Instance", new XAttribute("Scope", "LocalVariable"), new XAttribute("UId", instanceUid),
                        new XElement("Component", new XAttribute("Name", o.Instance))),
                    new XElement("TemplateValue", new XAttribute("Name", "time_type"), new XAttribute("Type", "Type"), "Time"));
                var uid = (long)part.Attribute("UId")!;
                _parts.Add(part);
                ConnectFeed(uid, "IN", feed);

                if (string.IsNullOrEmpty(o.Pt))
                {
                    throw new ArgumentException($"spec {o.Kind} output is missing pt (e.g. \"T#3S\" or an operand name).");
                }
                BindOperand(uid, "PT", o.Pt);

                if (o.Q is { } q)
                {
                    var coilUid = AddPart(q.Kind == "set" ? "SCoil" : q.Kind == "reset" ? "RCoil" : "Coil");
                    _wires.Add(Wire(new PinRef(uid, "Q"), new[] { new PinRef(coilUid, "in") }));
                    BindOperand(coilUid, "operand", q.Operand);
                }
                else
                {
                    WireToOpen(uid, "Q");
                }

                WireToOpen(uid, "ET"); // declared outputs must be wired (verified pitfall)
                break;
            }

            case "move":
            {
                if (string.IsNullOrEmpty(o.Src) || string.IsNullOrEmpty(o.Dst))
                {
                    throw new ArgumentException("spec move output needs src and dst.");
                }

                var part = new XElement("Part", new XAttribute("Name", "Move"), new XAttribute("UId", _uid.Next()),
                    new XAttribute("DisabledENO", "true"),
                    new XElement("TemplateValue", new XAttribute("Name", "Card"), new XAttribute("Type", "Cardinality"), "1"));
                var uid = (long)part.Attribute("UId")!;
                _parts.Add(part);
                ConnectFeed(uid, "en", feed);
                BindOperand(uid, "in", o.Src);
                DataOut(uid, "out1", o.Dst);
                break;
            }

            case "compare":
            {
                var partName = (o.Part ?? "").ToLowerInvariant() switch
                {
                    "eq" => "Eq", "ne" => "Ne", "ge" => "Ge", "gt" => "Gt", "le" => "Le", "lt" => "Lt",
                    _ => null,
                };
                if (partName is null)
                {
                    throw new ArgumentException(
                        $"spec compare part '{o.Part}' is not one of eq|ne|ge|gt|le|lt.");
                }

                if (string.IsNullOrEmpty(o.In1) || string.IsNullOrEmpty(o.In2))
                {
                    throw new ArgumentException("spec compare output needs in1 and in2.");
                }

                var uid = AddPart(partName, templateValues: new[]
                {
                    ("SrcType", "Type", InferSrcType(o.In1!, o.In2!)),
                });
                ConnectFeed(uid, "pre", feed);
                BindOperand(uid, "in1", o.In1);
                BindOperand(uid, "in2", o.In2);

                if (o.Out is { } chained)
                {
                    var coilUid = AddPart(chained.Kind == "set" ? "SCoil" : chained.Kind == "reset" ? "RCoil" : "Coil");
                    _wires.Add(Wire(new PinRef(uid, "out"), new[] { new PinRef(coilUid, "in") }));
                    BindOperand(coilUid, "operand", chained.Operand);
                }
                else
                {
                    WireToOpen(uid, "out");
                }

                break;
            }

            default:
                throw new ArgumentException(
                    $"spec output kind '{o.Kind}' is not in the v1 write subset " +
                    "(coil|set|reset|ton|tof|tp|move|compare). For anything else, export a similar " +
                    "block as template, edit its XML, and tia_block_import it (skill path B).");
        }
    }

    // ----- primitives -----

    private long AddPart(string name, bool negatedOperand = false,
        IReadOnlyList<(string Name, string Type, string Value)>? templateValues = null)
    {
        var uid = _uid.Next();
        var part = new XElement("Part", new XAttribute("Name", name), new XAttribute("UId", uid));
        if (negatedOperand)
        {
            part.Add(new XElement("Negated", new XAttribute("Name", "operand")));
        }

        if (templateValues is not null)
        {
            foreach (var (tvName, tvType, tvValue) in templateValues)
            {
                part.Add(new XElement("TemplateValue",
                    new XAttribute("Name", tvName), new XAttribute("Type", tvType), tvValue));
            }
        }

        _parts.Add(part);
        return uid;
    }

    /// <summary>Emits an Access for the operand and wires it to the part pin (data inputs).</summary>
    private void BindOperand(long partUid, string pin, string operand)
    {
        var accessUid = _uid.Next();
        _accesses.Add(AccessElement(accessUid, operand));
        _wires.Add(new XElement("Wire", new XAttribute("UId", _uid.Next()),
            new XElement("IdentCon", new XAttribute("UId", accessUid)),
            new XElement("NameCon", new XAttribute("UId", partUid), new XAttribute("Name", pin))));
    }

    /// <summary>Wires a part pin OUT to an operand access (data outputs, e.g. MOVE out1).</summary>
    private void DataOut(long partUid, string pin, string operand)
    {
        var accessUid = _uid.Next();
        _accesses.Add(AccessElement(accessUid, operand));
        _wires.Add(new XElement("Wire", new XAttribute("UId", _uid.Next()),
            new XElement("NameCon", new XAttribute("UId", partUid), new XAttribute("Name", pin)),
            new XElement("IdentCon", new XAttribute("UId", accessUid))));
    }

    private XElement AccessElement(long uid, string operand)
    {
        // Numeric literal -> LiteralConstant with ConstantType (corpus shape); T#… -> TypedConstant
        // WITHOUT ConstantType (verified pitfall); spec-declared member -> LocalVariable; else a
        // global tag (tia_tag_create is expected to have made it — same rule as the skill examples).
        if (IsNumber(operand))
        {
            return new XElement("Access", new XAttribute("Scope", "LiteralConstant"), new XAttribute("UId", uid),
                new XElement("Constant",
                    new XElement("ConstantType", operand.Contains('.') ? "Real" : "Int"),
                    new XElement("ConstantValue", operand)));
        }

        if (operand.StartsWith("T#", StringComparison.OrdinalIgnoreCase))
        {
            return new XElement("Access", new XAttribute("Scope", "TypedConstant"), new XAttribute("UId", uid),
                new XElement("Constant", new XElement("ConstantValue", operand)));
        }

        if (_locals.Contains(operand))
        {
            return LocalAccess(uid, operand);
        }

        return new XElement("Access", new XAttribute("Scope", "GlobalVariable"), new XAttribute("UId", uid),
            new XElement("Symbol", new XElement("Component", new XAttribute("Name", operand))));
    }

    private static XElement LocalAccess(long uid, string operand) =>
        new("Access", new XAttribute("Scope", "LocalVariable"), new XAttribute("UId", uid),
            new XElement("Symbol", new XElement("Component", new XAttribute("Name", operand))));

    private static bool IsNumber(string text) =>
        text.Length > 0 && text.All(ch => char.IsDigit(ch) || ch == '.' || ch == '-' || ch == '+') &&
        text.Any(char.IsDigit);

    /// <summary>Compares carry a SrcType template (the operands' shared type). Literals with a
    /// decimal point are Real; otherwise the spec's declared datatype wins (a Real-declared member
    /// compared against "8" is still Real); fallback Int.</summary>
    private string InferSrcType(string a, string b)
    {
        foreach (var operand in new[] { a, b })
        {
            if (_datatypes.TryGetValue(operand, out var dt) &&
                dt.StartsWith("Real", StringComparison.OrdinalIgnoreCase))
            {
                return "Real";
            }
        }

        return a.Contains('.') || b.Contains('.') ? "Real" : "Int";
    }

    private void ConnectFeed(long partUid, string pin, PinRef? feed)
    {
        if (feed is { } src)
        {
            _wires.Add(Wire(src, new[] { new PinRef(partUid, pin) }));
        }
        else
        {
            _railTargets.Add(new XElement("NameCon", new XAttribute("UId", partUid), new XAttribute("Name", pin)));
        }
    }

    private void WireToOpen(long partUid, string pin)
    {
        _wires.Add(new XElement("Wire", new XAttribute("UId", _uid.Next()),
            new XElement("NameCon", new XAttribute("UId", partUid), new XAttribute("Name", pin)),
            new XElement("OpenCon", new XAttribute("UId", _uid.Next()))));
    }

    private XElement Wire(PinRef source, IReadOnlyList<PinRef> targets)
    {
        var wire = new XElement("Wire", new XAttribute("UId", _uid.Next()),
            new XElement("NameCon", new XAttribute("UId", source.UId), new XAttribute("Name", source.Pin)));
        foreach (var t in targets)
        {
            wire.Add(new XElement("NameCon", new XAttribute("UId", t.UId), new XAttribute("Name", t.Pin)));
        }

        return wire;
    }
}
