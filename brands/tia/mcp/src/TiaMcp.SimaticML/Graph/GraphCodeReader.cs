using System.Xml.Linq;
using TiaMcp.Contract;
using TiaMcp.SimaticML.FlgNet;

namespace TiaMcp.SimaticML.Graph;

/// <summary>
/// GRAPH body reader, written against a REAL machine-exported reference
/// (tests/fixtures/FB_GraphDemo.xml, produced and compile-verified on TIA V21 / S7-1511, 2026-08):
/// the sequence lives in one CompileUnit's NetworkSource under a Graph element
/// (…/SW/NetworkSource/Graph/v6); steps carry Actions (qualifier + Token/access operands),
/// per-step Supervision (…SvCoil) and Interlock (…IlCoil) subnetworks; transitions carry their
/// condition FlgNet ending in TrCoil; the chain is wired by Step/Transition refs and terminates
/// in an EndConnection — OR, for closed sequencers, in a Jump connection back to the initial
/// step (emitted by write_code's `loop: true`; surfaced here as <c>Loop</c>). Step and transition
/// numbers are PAIRED (1, 21, 32, … — TIA numbers each step/transition pair with the same id).
/// </summary>
internal static class GraphCodeReader
{
    public static GraphCode Read(XDocument doc)
    {
        var graphEl = doc.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "Graph" && e.Name.NamespaceName.Contains("/NetworkSource/Graph", StringComparison.Ordinal));
        if (graphEl is null)
        {
            return new GraphCode(
                Array.Empty<GraphStep>(), Array.Empty<GraphTransition>(),
                "no Graph element found in source — not a GRAPH block body?");
        }

        var steps = new List<GraphStep>();
        var transitions = new List<(int Number, string Name, XElement Flg)>();

        foreach (var seq in graphEl.Descendants().Where(e => e.Name.LocalName == "Sequence"))
        {
            foreach (var stepEl in seq.Elements().First(e => e.Name.LocalName == "Steps").Elements()
                         .Where(e => e.Name.LocalName == "Step"))
            {
                steps.Add(ParseStep(stepEl));
            }

            foreach (var trEl in seq.Elements().First(e => e.Name.LocalName == "Transitions").Elements()
                         .Where(e => e.Name.LocalName == "Transition"))
            {
                transitions.Add((
                    AttrInt(trEl, "Number") ?? 0,
                    trEl.Attribute("Name")?.Value ?? "",
                    trEl.Descendants().FirstOrDefault(e => e.Name.LocalName == "FlgNet")!));
            }
        }

        // Order transitions alongside their paired steps (same number = the step's exit).
        var parsedTransitions = new List<GraphTransition>();
        foreach (var (number, name, flg) in transitions)
        {
            parsedTransitions.Add(new GraphTransition(number, name, RenderCondition(flg)));
        }

        // Closed sequencer: a Connection whose LinkType is Jump and whose target is the initial
        // step (the machine-verified shape write_code's `loop: true` emits; read/write symmetric).
        var initNumber = steps.FirstOrDefault(s => s.Init)?.Number ?? steps.FirstOrDefault()?.Number;
        var loop = initNumber is not null
            && graphEl.Descendants()
                .Where(e => e.Name.LocalName == "Connection")
                .Select(conn => new
                {
                    Jump = (conn.Elements().FirstOrDefault(e => e.Name.LocalName == "LinkType")?.Value ?? "")
                        .Equals("Jump", StringComparison.OrdinalIgnoreCase),
                    Target = conn.Elements().FirstOrDefault(e => e.Name.LocalName == "NodeTo")?
                        .Descendants().FirstOrDefault(e => e.Name.LocalName == "StepRef")?
                        .Attribute("Number")?.Value,
                })
                .Any(c => c.Jump && c.Target is not null
                    && int.TryParse(c.Target, out var n) && n == initNumber);

        var note = steps.Count == 0 && parsedTransitions.Count == 0
            ? "Graph element present but carries no steps/transitions."
            : null;
        return new GraphCode(steps, parsedTransitions, note, loop);
    }

    private static GraphStep ParseStep(XElement stepEl)
    {
        var name = stepEl.Attribute("Name")?.Value ?? "";
        var number = AttrInt(stepEl, "Number") ?? 0;
        var init = bool.TryParse(stepEl.Attribute("Init")?.Value, out var b) && b;

        var actions = new List<GraphAction>();
        var actionsEl = stepEl.Elements().FirstOrDefault(e => e.Name.LocalName == "Actions");
        if (actionsEl is not null)
        {
            foreach (var actionEl in actionsEl.Elements().Where(e => e.Name.LocalName == "Action"))
            {
                var qualifier = actionEl.Attribute("Qualifier")?.Value ?? "N";
                // Real exports carry the action operand as a Token whose text may be "#Op" (local)
                // or a plain/global name; an Access form (older exports) resolves via its symbol.
                var text = actionEl.Descendants().FirstOrDefault(e => e.Name.LocalName == "Token")?.Attribute("Text")?.Value;
                if (string.IsNullOrEmpty(text))
                {
                    var access = actionEl.Descendants()
                        .FirstOrDefault(e => e.Name.LocalName == "Access")?
                        .Descendants().FirstOrDefault(e => e.Name.LocalName == "Component")?.Attribute("Name")?.Value;
                    text = access;
                }

                if (!string.IsNullOrEmpty(text))
                {
                    actions.Add(new GraphAction(string.IsNullOrEmpty(qualifier) ? "N" : qualifier, text.TrimStart('#')));
                }
            }
        }

        var supervision = RenderSubnet(stepEl, "Supervisions", "Supervision");
        var interlock = RenderSubnet(stepEl, "Interlocks", "Interlock");
        return new GraphStep(number, name, init, actions, interlock, supervision);
    }

    /// <summary>Renders a per-step supervision/interlock subnet: rail → condition → SvCoil/IlCoil.
    /// The coil itself is implicit in the field name; only the condition text is interesting.</summary>
    private static string? RenderSubnet(XElement stepEl, string containerName, string itemName)
    {
        var subnet = stepEl.Elements().FirstOrDefault(e => e.Name.LocalName == containerName)?
            .Elements().FirstOrDefault(e => e.Name.LocalName == itemName)?
            .Descendants().FirstOrDefault(e => e.Name.LocalName == "FlgNet");
        return RenderCondition(subnet);
    }

    /// <summary>Renders a GRAPH condition subnet as its boolean expression (e.g. "Cond1"). The
    /// trailing SvCoil/IlCoil/TrCoil is the terminator, not part of the expression.</summary>
    private static string? RenderCondition(XElement? flgNet)
    {
        if (flgNet is null)
        {
            return null;
        }

        var model = FlgNetParser.Parse(flgNet);
        if (!PowerFlow.TryRender(model, out var rendered, out var reason))
        {
            return $"(unrendered: {reason})";
        }

        // The render ends with "= (SV)/(IL)/(TR)" — strip the terminator symbol, keep the condition.
        var text = rendered.Render;
        foreach (var symbol in new[] { " = (SV)", " = (IL)", " = (TR)", "(SV)", "(IL)", "(TR)" })
        {
            var idx = text.IndexOf(symbol, StringComparison.Ordinal);
            if (idx >= 0)
            {
                text = text[..idx];
                break;
            }
        }

        return text.Trim().Length == 0 ? "TRUE" : text.Trim();
    }

    private static int? AttrInt(XElement el, string name) =>
        int.TryParse(el.Attribute(name)?.Value, out var v) ? v : null;
}
