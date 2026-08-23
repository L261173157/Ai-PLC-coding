using System.Diagnostics.CodeAnalysis;
using TiaMcp.Contract;

namespace TiaMcp.SimaticML.FlgNet;

/// <summary>Successful render of one network: the pieces <see cref="NetworkCode"/> needs.</summary>
internal sealed record RenderedNetwork(
    string Render,
    LadExpr? Logic,
    IReadOnlyList<NetworkOutput> Outputs,
    IReadOnlyList<NetworkBox> Boxes);

/// <summary>
/// Traces power flow from the left rail through contacts / O-merges / inline compares to the
/// network's terminals (coils, IEC timers, boxes) and renders a compact expression per terminal.
/// Anything the tracer cannot prove — unknown parts, FBD-style dataflow into energy pins,
/// multi-fed pins, cycles, missing operands — fails the whole network into a structural fallback
/// instead of producing a wrong expression.
/// </summary>
internal static class PowerFlow
{
    public static bool TryRender(
        FlgNetModel model,
        [NotNullWhen(true)] out RenderedNetwork? result,
        [NotNullWhen(false)] out string? reason)
    {
        result = null;

        // --- topology sanity (never guess through violations) ---
        foreach (var part in model.Parts.Values)
        {
            if (!LadPartCatalog.IsKnown(part.Name))
            {
                reason = $"unsupported part '{part.Name}'";
                return false;
            }
        }

        if (model.Wires.Count(w => w.Source is PowerrailEnd) > 1)
        {
            reason = "more than one powerrail wire";
            return false;
        }

        var multiFed = model.Wires
            .SelectMany(w => w.Targets.OfType<PinEnd>())
            .GroupBy(p => p)
            .FirstOrDefault(g => g.Count() > 1);
        if (multiFed is not null)
        {
            reason = $"pin {multiFed.Key.PartUId}.{multiFed.Key.Pin} is fed by multiple wires";
            return false;
        }

        foreach (var part in model.Parts.Values)
        {
            if ((LadPartCatalog.Contacts.Contains(part.Name) || LadPartCatalog.Coils.Contains(part.Name)) &&
                !LadPartCatalog.IsOperandLessCoil(part.Name) &&
                string.IsNullOrEmpty(part.BoundOperand))
            {
                reason = $"part '{part.Name}' (UId {part.UId}) has no operand binding";
                return false;
            }
        }

        // --- render terminals (coils first keep classic rung order by UId) ---
        // A coil wired to a timer's Q is NOT an independent terminal — the timer's branch renders it
        // ("-> TON[...] ; Q = ( ) X"). Tracking its feed directly would cross the timer's Q pin,
        // which is legitimately untraceable as boolean input, and fail the whole network.
        var timerFedCoils = new HashSet<long>();
        foreach (var timer in model.Parts.Values.Where(p => LadPartCatalog.Timers.Contains(p.Name)))
        {
            foreach (var wire in model.WiresFrom(new PinEnd(timer.UId, "Q")))
            {
                foreach (var target in wire.Targets.OfType<PinEnd>())
                {
                    if (model.Parts.TryGetValue(target.PartUId, out var fed) &&
                        LadPartCatalog.Coils.Contains(fed.Name))
                    {
                        timerFedCoils.Add(fed.UId);
                    }
                }
            }
        }

        var terminals = model.Parts.Values
            .Where(p => ((LadPartCatalog.Coils.Contains(p.Name) || LadPartCatalog.GraphCoils.Contains(p.Name)) && !timerFedCoils.Contains(p.UId)) ||
                        LadPartCatalog.Timers.Contains(p.Name) ||
                        LadPartCatalog.Boxes.Contains(p.Name))
            .OrderBy(p => p.UId)
            .ToList();

        var outputs = new List<NetworkOutput>();
        var boxes = new List<NetworkBox>();
        var pieces = new List<string>();
        LadExpr? singleCoilLogic = null;
        var coilCount = 0;

        foreach (var term in terminals)
        {
            var isCoil = LadPartCatalog.Coils.Contains(term.Name) || LadPartCatalog.GraphCoils.Contains(term.Name);
            var isTimer = LadPartCatalog.Timers.Contains(term.Name);

            var pins = FlgNetParser.BindPins(model, term);
            var feedPin = isCoil ? "in" : isTimer ? "IN" : FirstFedInputPin(model, term);
            var feed = feedPin is null ? null : EvalPin(model, new HashSet<FlgEndpoint>(), new PinEnd(term.UId, feedPin));
            if (feedPin is not null && feed is null)
            {
                reason = $"cannot trace the boolean feed of part '{term.Name}' (UId {term.UId})";
                return false;
            }

            var feedText = feed is null || feed.Text == "TRUE" ? "" : feed.Text + " -> ";

            if (isCoil)
            {
                coilCount++;
                var kind = LadPartCatalog.CoilKind(term.Name);
                var operand = term.BoundOperand;
                if (operand is not null)
                {
                    outputs.Add(new NetworkOutput(kind, operand));
                }

                // Coils read as assignment ("expr = ( ) X"); boxes/timers keep "-> " (feedText).
                // GRAPH subnet coils (SvCoil/IlCoil/TrCoil) carry no operand — the enclosing
                // step/transition supplies the meaning, rendered as the bare (SV)/(IL)/(TR) symbol.
                var coilFeed = feed is null || feed.Text == "TRUE" ? "" : feed.Text + " = ";
                var suffix = operand is null ? "" : " " + operand;
                pieces.Add($"{coilFeed}{LadPartCatalog.CoilSymbol(kind)}{suffix}");
                singleCoilLogic = coilCount == 1 ? feed?.Expr : null;
            }
            else if (isTimer)
            {
                var pt = pins.TryGetValue("pt", out var ptLower) ? ptLower
                    : pins.TryGetValue("PT", out var ptUpper) ? ptUpper
                    : null;
                boxes.Add(new NetworkBox(term.Name, term.Instance, pins));
                pieces.Add($"{feedText}{term.Name}[{term.Instance ?? ""}{(pt is null ? "" : ", PT=" + pt)}]");

                // A coil wired to the timer's Q rides the same rung (verified legal).
                foreach (var wire in model.WiresFrom(new PinEnd(term.UId, "Q")))
                {
                    foreach (var target in wire.Targets.OfType<PinEnd>())
                    {
                        if (model.Parts.TryGetValue(target.PartUId, out var qPart) &&
                            LadPartCatalog.Coils.Contains(qPart.Name))
                        {
                            var kind = LadPartCatalog.CoilKind(qPart.Name);
                            outputs.Add(new NetworkOutput(kind, qPart.BoundOperand!));
                            pieces.Add($"Q = {LadPartCatalog.CoilSymbol(kind)} {qPart.BoundOperand}");
                        }
                    }
                }
            }
            else
            {
                boxes.Add(new NetworkBox(term.Name, term.Instance, pins));
                pieces.Add(feedText + BoxSummary(term, pins));
            }
        }

        if (pieces.Count == 0)
        {
            reason = "network has no coil / timer / box terminal to render";
            return false;
        }

        // The editable logic tree is only exposed for provably-pure single-coil boolean networks:
        // a term rendered across an inline compare carries text but no tree node, which leaves
        // singleCoilLogic null (rail-fed coils DO carry a tree — the TRUE contact), so null here
        // with exactly one coil and no boxes means "readable but not tree-editable".
        var logic = coilCount == 1 && boxes.Count == 0 ? singleCoilLogic : null;
        result = new RenderedNetwork(string.Join(" ; ", pieces), logic, outputs, boxes);
        reason = null;
        return true;
    }

    private static string BoxSummary(PartNode part, Dictionary<string, string> pins)
    {
        switch (part.Name)
        {
            case "Move":
                var src = pins.TryGetValue("in", out var moveIn) ? moveIn : "?";
                var dst = pins.TryGetValue("out1", out var moveOut) ? moveOut
                    : pins.TryGetValue("out", out var moveOut2) ? moveOut2
                    : "?";
                return $"MOVE({src} -> {dst})";
            case "Add": case "Sub": case "Mul": case "Div":
                var x = pins.TryGetValue("in1", out var arA) ? arA : "?";
                var y = pins.TryGetValue("in2", out var arB) ? arB : "?";
                var res = pins.TryGetValue("out", out var arOut) ? " -> " + arOut : "";
                return $"{x} {ArithSymbol(part.Name)} {y}{res}";
            default:
                var args = string.Join(", ", pins
                    .Where(kv => kv.Key is not ("operand" or "en" or "eno"))
                    .Select(kv => $"{kv.Key}={kv.Value}"));
                return string.IsNullOrEmpty(args) ? part.Name : $"{part.Name}({args})";
        }
    }

    private static string ArithSymbol(string name) => name switch
    {
        "Add" => "+", "Sub" => "-", "Mul" => "*", _ => "/",
    };

    internal static string CompareSymbol(string name) => name switch
    {
        "Eq" => "==", "Ne" => "<>", "Ge" => ">=", "Gt" => ">", "Le" => "<=", _ => "<",
    };

    /// <summary>First input-ish pin of a generic box that actually has a feeding wire (en → in → in1).</summary>
    private static string? FirstFedInputPin(FlgNetModel model, PartNode part) =>
        new[] { "en", "in", "in1" }.FirstOrDefault(pin => model.WiresTo(new PinEnd(part.UId, pin)).Any());

    private sealed record Term(string Text, LadExpr? Expr);

    private static Term? EvalPin(FlgNetModel model, HashSet<FlgEndpoint> visited, FlgEndpoint pin)
    {
        if (!visited.Add(pin))
        {
            return null; // cycle
        }

        var feeds = model.WiresTo(pin).ToList();
        if (feeds.Count != 1)
        {
            return null;
        }

        switch (feeds[0].Source)
        {
            case PowerrailEnd:
                return new Term("TRUE", new LadExpr("contact", "TRUE"));

            case PinEnd p when model.Parts.TryGetValue(p.PartUId, out var part):
                if (LadPartCatalog.Contacts.Contains(part.Name))
                {
                    return EvalContact(model, visited, part);
                }
                if (LadPartCatalog.Merges.Contains(part.Name))
                {
                    return EvalMerge(model, visited, part);
                }
                if (LadPartCatalog.Compares.Contains(part.Name))
                {
                    return EvalCompare(model, visited, part);
                }
                return null; // box/timer output crossing the boolean chain

            default:
                return null; // access/open wired to an energy pin (FBD-style)
        }
    }

    private static Term? EvalContact(FlgNetModel model, HashSet<FlgEndpoint> visited, PartNode part)
    {
        var operand = part.BoundOperand!;
        var negated = part.Negated.Contains("operand");
        var edge = part.Name switch
        {
            "PContact" => "rising",
            "NContact" => "falling",
            _ => null,
        };

        var prefix = negated ? "NOT " : "";
        var edgePrefix = edge == "rising" ? "P " : edge == "falling" ? "F " : "";
        var self = new Term($"{prefix}{edgePrefix}{operand}", new LadExpr("contact", operand, negated, edge));

        // Plain Contact uses "in"; edge contacts (S7-1200 dialect) use "pre". Try in that order.
        Term? upstream = null;
        foreach (var pinName in LadPartCatalog.PowerInCandidates(part.Name))
        {
            upstream = EvalPin(model, visited, new PinEnd(part.UId, pinName));
            if (upstream is not null)
            {
                break;
            }
        }

        if (upstream is null)
        {
            return null;
        }

        if (upstream.Text == "TRUE")
        {
            return self;
        }

        return new Term(
            $"{upstream.Text} AND {self.Text}",
            upstream.Expr is null ? null : new LadExpr("and", Args: new[] { upstream.Expr, self.Expr! }));
    }

    private static Term? EvalCompare(FlgNetModel model, HashSet<FlgEndpoint> visited, PartNode part)
    {
        var pins = FlgNetParser.BindPins(model, part);
        var a = pins.TryGetValue("in1", out var cmpA) ? cmpA : "?";
        var b = pins.TryGetValue("in2", out var cmpB) ? cmpB : "?";
        var self = new Term($"{a} {CompareSymbol(part.Name)} {b}", null);

        var upstream = EvalPin(model, visited, new PinEnd(part.UId, "pre"));
        if (upstream is null)
        {
            return null;
        }

        if (upstream.Text == "TRUE")
        {
            return self;
        }

        return new Term($"{upstream.Text} AND {self.Text}", null);
    }

    private static Term? EvalMerge(FlgNetModel model, HashSet<FlgEndpoint> visited, PartNode part)
    {
        var card = part.TemplateValues.TryGetValue("Card", out var cardText) && int.TryParse(cardText, out var c) && c > 0
            ? c
            : 2;

        var terms = new List<Term>();
        for (var k = 1; k <= card; k++)
        {
            var t = EvalPin(model, visited, new PinEnd(part.UId, $"in{k}"));
            if (t is null)
            {
                return null;
            }

            terms.Add(t);
        }

        if (terms.Count == 1)
        {
            return terms[0];
        }

        var allTrees = terms.All(t => t.Expr is not null);
        return new Term(
            "(" + string.Join(" OR ", terms.Select(t => t.Text)) + ")",
            allTrees ? new LadExpr("or", Args: terms.Select(t => t.Expr!).ToArray()) : null);
    }
}
