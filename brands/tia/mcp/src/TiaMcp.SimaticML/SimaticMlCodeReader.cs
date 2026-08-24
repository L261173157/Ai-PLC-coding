using System.Xml.Linq;
using TiaMcp.Contract;
using TiaMcp.SimaticML.FlgNet;
using TiaMcp.SimaticML.Graph;
using TiaMcp.SimaticML.StructuredText;

namespace TiaMcp.SimaticML;

/// <summary>
/// Parses a SimaticML block export (the XML that <c>ReadBlockSourceAsync</c> /
/// <c>tia_block_export format=Xml</c> produce) into a compact <see cref="BlockCode"/>: interface
/// member tree + per-network expressions / boxes / flattened SCL, or a GRAPH step view. Runs
/// net10-side so the net48 worker is not involved beyond the existing block-source RPC.
/// </summary>
public static class SimaticMlCodeReader
{
    public static BlockCode Read(string xml, string path, ReadCodeOptions options)
    {
        var warnings = new List<string>();

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception)
        {
            // Not XML at all (e.g. the Fake world's legacy plain-SCL sources): pass through as text.
            return new BlockCode(
                path, "", "", "SCL?",
                null,
                new[] { new NetworkCode(0, "scl", null, null, null, null, Array.Empty<NetworkOutput>(),
                    Array.Empty<NetworkBox>(), null, xml) },
                null,
                new[] { "source is not SimaticML XML — returned verbatim (flattened display only)" });
        }

        var blockEl = doc.Descendants().FirstOrDefault(e =>
            e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal) ||
            e.Name.LocalName == "SW.Types.PlcStruct") ??
            throw new InvalidOperationException("no SW.Blocks.* / SW.Types.PlcStruct element in source");

        var attr = blockEl.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
        var name = AttrValue(attr, "Name") ?? "";
        var number = AttrValue(attr, "Number");
        var language = AttrValue(attr, "ProgrammingLanguage") ?? "";
        var blockType = blockEl.Name.LocalName switch
        {
            "SW.Blocks.GlobalDB" => "DB",
            "SW.Types.PlcStruct" => "UDT",
            var n when n.StartsWith("SW.Blocks.", StringComparison.Ordinal) => n["SW.Blocks.".Length..],
            _ => "",
        };

        if (language.Length == 0)
        {
            // DBs / UDTs carry no ProgrammingLanguage; derive from the type.
            language = blockType is "DB" or "UDT" ? blockType : "";
        }

        IReadOnlyList<InterfaceSection>? interfaceSections = null;
        if (options.IncludeInterface)
        {
            interfaceSections = ParseInterfaceSections(attr);
        }

        var networks = new List<NetworkCode>();
        GraphCode? graph = null;
        var index = 0;
        // NB: "SW.Blocks.CompileUnit" is a LITERAL element name containing dots (SimaticML
        // convention), not a namespace prefix — match on the suffix, not on a bare "CompileUnit".
        foreach (var unit in doc.Descendants().Where(e => e.Name.LocalName.EndsWith("CompileUnit", StringComparison.Ordinal)))
        {
            if (IsOutsideNetworkRange(index, options))
            {
                index++;
                continue;
            }

            var (title, comment) = ReadTitleComment(unit);
            var networkSource = unit.Descendants().FirstOrDefault(e => e.Name.LocalName == "NetworkSource");
            var body = networkSource?.Elements().FirstOrDefault();

            if (language == "GRAPH")
            {
                // GRAPH bodies are not plain CompileUnits; GraphCodeReader handles them below.
                index++;
                continue;
            }

            networks.Add(ParseNetworkBody(index, language, title, comment, body, warnings));
            index++;
        }

        if (language == "GRAPH")
        {
            graph = GraphCodeReader.Read(doc);
            if (graph.FallbackNote is not null)
            {
                warnings.Add(graph.FallbackNote);
            }
        }

        return new BlockCode(path, name, blockType, language, interfaceSections, networks, graph, warnings);
    }

    private static NetworkCode ParseNetworkBody(
        int index, string language, string? title, string? comment, XElement? body, List<string> warnings)
    {
        if (body is null)
        {
            return new NetworkCode(index, "empty", title, comment, null, null,
                Array.Empty<NetworkOutput>(), Array.Empty<NetworkBox>(), null, null);
        }

        switch (body.Name.LocalName)
        {
            case "StructuredText":
            {
                var text = StTokenFlattener.Flatten(body);
                warnings.Add($"network {index}: SCL flattened from token stream (display-grade; authoritative text via tia_block_export format=SclSource)");
                return new NetworkCode(index, "scl", title, comment, null, null,
                    Array.Empty<NetworkOutput>(), Array.Empty<NetworkBox>(), null, text);
            }

            case "FlgNet":
            {
                var kind = language == "FBD" ? "fbd" : "lad";
                var model = FlgNetParser.Parse(body);
                if (PowerFlow.TryRender(model, out var rendered, out var reason))
                {
                    return new NetworkCode(index, kind, title, comment, rendered.Render, rendered.Logic,
                        rendered.Outputs, rendered.Boxes, null, null);
                }

                return new NetworkCode(index, kind, title, comment, null, null,
                    ExtractOutputs(model), ExtractBoxes(model), BuildFallback(model, reason), null);
            }

            default:
                return new NetworkCode(index, language.ToLowerInvariant(), title, comment, null, null,
                    Array.Empty<NetworkOutput>(), Array.Empty<NetworkBox>(),
                    new NetworkFallback(
                        $"unknown NetworkSource body '{body.Name.LocalName}'",
                        Array.Empty<FallbackPart>(), Array.Empty<FallbackWire>()), null);
        }
    }

    /// <summary>Best-effort output listing even when full rendering falls back.</summary>
    private static IReadOnlyList<NetworkOutput> ExtractOutputs(FlgNetModel model) =>
        model.Parts.Values
            .Where(p => LadPartCatalog.Coils.Contains(p.Name) && !string.IsNullOrEmpty(p.BoundOperand))
            .OrderBy(p => p.UId)
            .Select(p => new NetworkOutput(LadPartCatalog.CoilKind(p.Name), p.BoundOperand!))
            .ToList();

    private static IReadOnlyList<NetworkBox> ExtractBoxes(FlgNetModel model) =>
        model.Parts.Values
            .Where(p => LadPartCatalog.Timers.Contains(p.Name) || LadPartCatalog.Boxes.Contains(p.Name))
            .OrderBy(p => p.UId)
            .Select(p => new NetworkBox(p.DisplayName, p.Instance, FlgNetParser.BindPins(model, p)))
            .ToList();

    private static NetworkFallback BuildFallback(FlgNetModel model, string reason)
    {
        const int cap = 200;
        var parts = model.Parts.Values.OrderBy(p => p.UId).Take(cap).Select(p => new FallbackPart(
            p.UId, p.DisplayName, p.BoundOperand, p.Instance, p.Negated.ToArray())).ToList();
        if (model.Parts.Count > cap)
        {
            parts.Add(new FallbackPart(0, $"…+{model.Parts.Count - cap} more parts", null, null, Array.Empty<string>()));
        }

        var wires = model.Wires.SelectMany(w => w.Targets.Select(t => new FallbackWire(EndpointText(w.Source), EndpointText(t)))).ToList();
        return new NetworkFallback(reason, parts, wires);
    }

    private static string EndpointText(FlgEndpoint ep) => ep switch
    {
        PowerrailEnd => "powerrail",
        PinEnd p => $"{p.PartUId}.{p.Pin}",
        AccessEnd a => $"access:{a.AccessUId}",
        _ => "open",
    };

    private static bool IsOutsideNetworkRange(int index, ReadCodeOptions options)
    {
        if (options.NetworkFrom is { } from && index < from)
        {
            return true;
        }

        if (options.NetworkTo is { } to && index > to)
        {
            return true;
        }

        return false;
    }

    private static (string? Title, string? Comment) ReadTitleComment(XElement unit)
    {
        // Titles/comments live in the CompileUnit's ObjectList as MultilingualText with
        // CompositionName="Title"/"Comment"; absent on hand-written minimal XML — stay tolerant.
        string? title = null;
        string? comment = null;
        foreach (var ml in unit.Descendants().Where(e => e.Name.LocalName == "MultilingualText"))
        {
            var role = ml.Attributes().FirstOrDefault(a => a.Name.LocalName == "CompositionName")?.Value;
            var text = ml.Descendants().FirstOrDefault(e => e.Name.LocalName == "Text")?.Value;
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            switch (role)
            {
                case "Title":
                    title = text;
                    break;
                case "Comment":
                    comment = text;
                    break;
            }
        }

        return (title, comment);
    }

    /// <summary>net10-side port of the worker's interface parser (deliberate duplication: keeping the
    /// worker binary untouched is worth a second 30-line local-name walker here).</summary>
    private static IReadOnlyList<InterfaceSection> ParseInterfaceSections(XElement? attributeList)
    {
        var sections = new List<InterfaceSection>();
        var interfaceEl = attributeList?.Elements().FirstOrDefault(e => e.Name.LocalName == "Interface");
        if (interfaceEl is null)
        {
            return sections;
        }

        foreach (var sec in interfaceEl.Descendants().Where(e => e.Name.LocalName == "Section"))
        {
            var secName = sec.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value ?? string.Empty;
            sections.Add(new InterfaceSection(secName, ParseMembers(sec)));
        }

        return sections;
    }

    private static IReadOnlyList<InterfaceMember> ParseMembers(XElement parent)
    {
        var members = new List<InterfaceMember>();
        foreach (var m in parent.Elements().Where(e => e.Name.LocalName == "Member"))
        {
            var nm = m.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value ?? string.Empty;
            var dt = m.Attributes().FirstOrDefault(a => a.Name.LocalName == "Datatype")?.Value ?? string.Empty;
            var start = m.Elements().FirstOrDefault(e => e.Name.LocalName == "StartValue")?.Value;
            var comment = m.Elements().FirstOrDefault(e => e.Name.LocalName == "Comment")
                ?.Descendants().FirstOrDefault(e => e.Name.LocalName == "MultiLanguageText")?.Value;
            members.Add(new InterfaceMember(nm, dt, start, comment, ParseMembers(m)));
        }

        return members;
    }

    private static string? AttrValue(XElement? attributeList, string localName) =>
        attributeList?.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim();
}
