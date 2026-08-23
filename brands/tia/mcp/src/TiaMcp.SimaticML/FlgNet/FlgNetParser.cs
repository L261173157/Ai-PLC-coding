using System.Xml.Linq;

namespace TiaMcp.SimaticML.FlgNet;

/// <summary>
/// Parses one FlgNet element (a LAD/FBD network) into the normalized <see cref="FlgNetModel"/>.
/// Fully namespace-agnostic (local-name matching, same approach as the worker's
/// ParseInterfaceSections): exports carry xmlns on the FlgNet root and we never rely on it.
/// </summary>
internal static class FlgNetParser
{
    public static FlgNetModel Parse(XElement flgNet)
    {
        var partsEl = Child(flgNet, "Parts") ?? new XElement("Parts");
        var wiresEl = Child(flgNet, "Wires") ?? new XElement("Wires");

        var accesses = new Dictionary<long, AccessNode>();
        foreach (var el in partsEl.Elements().Where(e => LocalName(e) == "Access"))
        {
            var uid = AttrLong(el, "UId");
            if (uid is null)
            {
                continue;
            }

            accesses[uid.Value] = new AccessNode(uid.Value, el.Attribute("Scope")?.Value ?? "", AccessText(el));
        }

        var parts = new Dictionary<long, PartNode>();
        foreach (var el in partsEl.Elements().Where(e => LocalName(e) == "Part"))
        {
            var uid = AttrLong(el, "UId");
            var name = el.Attribute("Name")?.Value;
            if (uid is null || string.IsNullOrEmpty(name))
            {
                continue;
            }

            var negated = new HashSet<string>();
            var templates = new Dictionary<string, string>();
            string? instance = null;
            foreach (var child in el.Elements())
            {
                switch (LocalName(child))
                {
                    case "Negated":
                        var pin = child.Attribute("Name")?.Value;
                        if (!string.IsNullOrEmpty(pin))
                        {
                            negated.Add(pin);
                        }
                        break;
                    case "TemplateValue":
                        var tvName = child.Attribute("Name")?.Value;
                        if (!string.IsNullOrEmpty(tvName))
                        {
                            templates[tvName] = child.Value.Trim();
                        }
                        break;
                    case "Instance":
                        instance = FirstComponentName(child);
                        break;
                    case "CallInfo":
                        // Call boxes carry the called block under CallInfo; surface it as the "instance".
                        instance ??= FirstComponentName(child);
                        break;
                }
            }

            parts[uid.Value] = new PartNode
            {
                UId = uid.Value,
                Name = name,
                Version = el.Attribute("Version")?.Value,
                Instance = instance,
                Negated = negated,
                TemplateValues = templates,
            };
        }

        var wires = new List<WireNode>();
        var wireUid = 0L;
        foreach (var el in wiresEl.Elements().Where(e => LocalName(e) == "Wire"))
        {
            // Child order carries direction: first child is the source, the rest are targets
            // (verified against the hand-written 启保停/定时器 examples: Powerrail→[in,in],
            // IdentCon→NameCon(operand), out→in1, ET→OpenCon). Powerrail/IdentCon/OpenCon are
            // always sources by construction, so only NameCon order can ever be ambiguous.
            FlgEndpoint? source = null;
            var targets = new List<FlgEndpoint>();
            foreach (var child in el.Elements())
            {
                var end = ParseEndpoint(child);
                if (end is null)
                {
                    continue;
                }

                if (source is null)
                {
                    source = end;
                }
                else
                {
                    targets.Add(end);
                }
            }

            if (source is not null)
            {
                wires.Add(new WireNode(AttrLong(el, "UId") ?? --wireUid, source, targets));
            }
        }

        var model = new FlgNetModel { Accesses = accesses, Parts = parts, Wires = wires };

        // Resolve IdentCon→NameCon(operand) bindings so every part knows its operand.
        foreach (var w in model.Wires)
        {
            if (w.Source is not AccessEnd a || !accesses.TryGetValue(a.AccessUId, out var acc))
            {
                continue;
            }

            foreach (var t in w.Targets)
            {
                if (t is PinEnd { Pin: "operand" or "in1" or "in2" or "en" or "pt" } pin &&
                    parts.TryGetValue(pin.PartUId, out var part))
                {
                    // Contact/coil pins bind to BoundOperand; box inputs keep per-pin bindings.
                    if (pin.Pin == "operand")
                    {
                        part.BoundOperand = acc.Text;
                    }
                }
            }
        }

        return model;
    }

    /// <summary>Operand pins per box part, filled from the actual wires in BOTH directions
    /// (access→pin is a data input; pin→access is a data output, e.g. MOVE out1 → operand). Pin
    /// names come from the source XML, so unknown boxes dump faithfully.</summary>
    public static Dictionary<string, string> BindPins(FlgNetModel model, PartNode part)
    {
        var pins = new Dictionary<string, string>();
        foreach (var w in model.Wires)
        {
            // access -> pin (data input)
            var sourceText = model.AccessText(w.Source);
            if (sourceText is not null)
            {
                foreach (var t in w.Targets)
                {
                    if (t is PinEnd pin && pin.PartUId == part.UId)
                    {
                        pins[pin.Pin] = sourceText;
                    }
                }
            }

            // pin -> access (data output, e.g. Move out1 / Add out feeding an operand directly)
            if (w.Source is PinEnd src && src.PartUId == part.UId)
            {
                foreach (var t in w.Targets)
                {
                    var outText = model.AccessText(t);
                    if (outText is not null)
                    {
                        pins[src.Pin] = outText;
                    }
                }
            }
        }

        if (part.BoundOperand is not null)
        {
            pins.TryAdd("operand", part.BoundOperand);
        }

        return pins;
    }

    private static FlgEndpoint? ParseEndpoint(XElement el) => LocalName(el) switch
    {
        "Powerrail" => new PowerrailEnd(),
        "NameCon" when AttrLong(el, "UId") is { } uid && el.Attribute("Name")?.Value is { } pin =>
            new PinEnd(uid, pin),
        "IdentCon" when AttrLong(el, "UId") is { } uid => new AccessEnd(uid),
        "OpenCon" => new OpenEnd(),
        _ => null,
    };

    private static string AccessText(XElement access)
    {
        var scope = access.Attribute("Scope")?.Value ?? "";
        if (Child(access, "Symbol") is { } symbol && FirstComponentName(symbol) is { } path)
        {
            // Raw operand names everywhere (locals and globals alike): structured fields and
            // render strings stay round-trippable with tia_block_write_code specs, and the
            // interface listing already tells the agent which names are block-local.
            return path;
        }

        if (Child(access, "Constant") is { } constant && Child(constant, "ConstantValue") is { } value)
        {
            return value.Value.Trim();
        }

        var text = access.Value.Trim();
        return text.Length > 0 ? text : scope;
    }

    /// <summary>Dot-joins a Symbol/Instance/CallInfo child's nested Component chain
    /// (e.g. Data.Block → "MyDB.MyTag").</summary>
    internal static string? FirstComponentName(XElement el)
    {
        var comp = DescendComponents(el).FirstOrDefault();
        return comp is null ? null : string.Join(".", DescendComponents(el).Select(c => c.Attribute("Name")?.Value ?? ""));

        static IEnumerable<XElement> DescendComponents(XElement start)
        {
            var current = Child(start, "Component");
            while (current is not null)
            {
                yield return current;
                current = Child(current, "Component");
            }
        }
    }

    internal static string LocalName(XElement el) => el.Name.LocalName;

    internal static XElement? Child(XElement el, string localName) =>
        el.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    internal static long? AttrLong(XElement el, string name) =>
        long.TryParse(el.Attribute(name)?.Value, out var v) ? v : null;
}
