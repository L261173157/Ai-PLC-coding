using System.Text.RegularExpressions;
using System.Xml.Linq;
using TiaMcp.Contract;

namespace TiaMcp.SimaticML.Generator;

/// <summary>
/// Compiles a GRAPH <see cref="CodeBlockSpec"/> (its <c>Sequence</c>) into a complete SimaticML
/// document. The emitted shape is byte-modeled on the machine-verified bootstrap (2026-08, real
/// TIA V21 / S7-1511): a minimal-interface GRAPH FB that imports cleanly and compiles 0 errors —
/// TIA auto-appends the full GRAPH runtime interface (RT_DATA, step flags, offsets, …) on import,
/// so the generator stays minimal. Machine-verified facts baked in here:
/// MemoryLayout must be ReadOnly="true"/Standard; step+transition numbers are PAIRED
/// (1, 21, 32, … = 11*i+10); the sequence terminates Transition→EndConnection; every step needs
/// Supervision (…SvCoil) and Interlock (…IlCoil) subnets as rail→contact→coil; transition
/// conditions end in TrCoil; action operands are Token elements ("#Local"/"Global"); names must
/// be unique; the interface needs OFF_SQ/INIT_SQ/ACK_EF inputs.
/// </summary>
public static partial class GraphSpecGenerator
{
    private const string InterfaceNs = "http://www.siemens.com/automation/Openness/SW/Interface/v5";
    private const string GraphNs = "http://www.siemens.com/automation/Openness/SW/NetworkSource/Graph/v6";

    private static readonly HashSet<string> ValidQualifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "N", "R", "S", "D", "L", "ON", "OFF", "TD", "TF", "TL", "TR", "CD", "CR", "CS", "CU",
    };

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierRegex();

    public static string Generate(CodeBlockSpec spec)
    {
        var sequence = (spec.Sequence ?? Array.Empty<GraphStepSpec>()).Where(s => s is not null).ToList();
        if (sequence.Count == 0)
        {
            throw new ArgumentException("GRAPH spec needs a non-empty sequence (list of steps).");
        }

        if (!spec.BlockType.Equals("FB", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("GRAPH blocks must be FB (blockType=FB).");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var locals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in spec.Interface ?? Array.Empty<SpecSection>())
        {
            foreach (var m in s.Members ?? Array.Empty<SpecMember>())
            {
                if (!string.IsNullOrEmpty(m.Name))
                {
                    locals.Add(m.Name!);
                }
            }
        }

        // Validate + collect operands up front so failures happen before any XML is built.
        foreach (var (i, step) in sequence.Select((s, i) => (i, s)))
        {
            if (string.IsNullOrEmpty(step.Name) || !IdentifierRegex().IsMatch(step.Name))
            {
                throw new ArgumentException($"sequence[{i}].name '{step.Name}' is not a valid identifier.");
            }

            if (!names.Add(step.Name))
            {
                throw new ArgumentException($"sequence[{i}].name '{step.Name}' is not unique.");
            }

            foreach (var a in step.Actions ?? Array.Empty<GraphActionSpec>())
            {
                if (string.IsNullOrEmpty(a?.Operand))
                {
                    throw new ArgumentException($"sequence[{i}] ('{step.Name}') has an action without an operand.");
                }

                if (!string.IsNullOrEmpty(a.Qualifier) && !ValidQualifiers.Contains(a.Qualifier!))
                {
                    throw new ArgumentException(
                        $"action qualifier '{a.Qualifier}' is not a GRAPH qualifier (N/R/S/D/L/ON/OFF/TD/TF/TL/CD/CR/CS/CU).");
                }
            }
        }

        // First declared non-standard input doubles as the supervision/interlock condition operand
        // (subnets are mandatory; a real condition keeps them out of the compile-warning path).
        var conditionOperand = (spec.Interface ?? Array.Empty<SpecSection>())
            .Where(s => s.Section.Equals("Input", StringComparison.OrdinalIgnoreCase))
            .SelectMany(s => s.Members ?? Array.Empty<SpecMember>())
            .Select(m => m.Name)
            .FirstOrDefault(n => n is not ("OFF_SQ" or "INIT_SQ" or "ACK_EF"))
            ?? throw new ArgumentException(
                "GRAPH spec needs at least one Input member besides OFF_SQ/INIT_SQ/ACK_EF — the first one " +
                "doubles as the mandatory supervision/interlock condition.");

        var graph = BuildGraph(spec, sequence, locals, conditionOperand, locals);
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("Document",
                new XElement("Engineering", new XAttribute("version", "V21")),
                new XElement("SW.Blocks.FB", new XAttribute("ID", 0),
                    BuildAttributeList(spec),
                    new XElement("ObjectList",
                        new XElement("SW.Blocks.CompileUnit",
                            new XAttribute("ID", 1), new XAttribute("CompositionName", "CompileUnits"),
                            new XElement("AttributeList",
                                new XElement("NetworkSource", graph),
                                new XElement("ProgrammingLanguage", "GRAPH")))))));
        var xml = doc.ToString();
        LintGenerated.Lint(xml);
        return xml;
    }

    private static XElement BuildAttributeList(CodeBlockSpec spec)
    {
        var sections = new XElement(XName.Get("Sections", InterfaceNs));

        var input = new XElement("Section", new XAttribute("Name", "Input"));
        foreach (var m in new[] { "OFF_SQ", "INIT_SQ", "ACK_EF" })
        {
            input.Add(new XElement("Member", new XAttribute("Name", m), new XAttribute("Datatype", "Bool")));
        }

        foreach (var s in spec.Interface ?? Array.Empty<SpecSection>())
        {
            if (!s.Section.Equals("Input", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var m in s.Members ?? Array.Empty<SpecMember>())
            {
                input.Add(MemberEl(m));
            }
        }

        sections.Add(input);

        var output = new XElement("Section", new XAttribute("Name", "Output"));
        foreach (var s in spec.Interface ?? Array.Empty<SpecSection>())
        {
            if (s.Section.Equals("Output", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var m in s.Members ?? Array.Empty<SpecMember>())
                {
                    output.Add(MemberEl(m));
                }
            }
        }

        sections.Add(output);
        sections.Add(new XElement("Section", new XAttribute("Name", "InOut")));
        sections.Add(new XElement("Section", new XAttribute("Name", "Static")));
        sections.Add(new XElement("Section", new XAttribute("Name", "Temp")));
        sections.Add(new XElement("Section", new XAttribute("Name", "Constant")));

        return new XElement("AttributeList",
            new XElement("AutoNumber", "true"),
            new XElement("Interface", sections),
            // Machine-verified: GRAPH FBs require a FIXED (ReadOnly) Standard memory layout.
            new XElement("MemoryLayout", new XAttribute("ReadOnly", "true"), "Standard"),
            new XElement("Name", spec.Name),
            new XElement("Namespace"),
            new XElement("Number", "0"),
            new XElement("ProgrammingLanguage", "GRAPH"));
    }

    private static XElement MemberEl(SpecMember m)
    {
        var el = new XElement("Member", new XAttribute("Name", m.Name ?? ""), new XAttribute("Datatype", m.Datatype ?? ""));
        if (!string.IsNullOrEmpty(m.Comment))
        {
            el.Add(new XElement("Comment",
                new XElement("MultiLanguageText", new XAttribute("Lang", "zh-CN"), m.Comment)));
        }

        return el;
    }

    private static XElement BuildGraph(CodeBlockSpec spec, IReadOnlyList<GraphStepSpec> sequence,
        HashSet<string> locals, string conditionOperand, HashSet<string> operandLocals)
    {
        var stepsEl = new XElement("Steps");
        var transEl = new XElement("Transitions");
        var connsEl = new XElement("Connections");
        var stepNumbers = new List<int>();

        for (var i = 0; i < sequence.Count; i++)
        {
            var number = i == 0 ? 1 : 11 * i + 10; // machine-verified pairing: 1, 21, 32, 43, …
            stepNumbers.Add(number);
            var step = sequence[i];
            var uid = (i + 2) * 100; // per-step uid base; transitions use (i+2)*100+50

            var actions = new XElement("Actions");
            foreach (var a in step.Actions ?? Array.Empty<GraphActionSpec>())
            {
                actions.Add(new XElement("Action",
                    new XAttribute("Qualifier", string.IsNullOrEmpty(a.Qualifier) ? "N" : a.Qualifier),
                    new XElement("Token", new XAttribute("Text", OperandText(a.Operand!, locals)))));
            }

            stepsEl.Add(new XElement("Step",
                new XAttribute("Number", number),
                new XAttribute("Init", i == 0 ? "true" : "false"),
                new XAttribute("Name", step.Name),
                actions,
                Subnet(uid, "Supervisions", "Supervision", "SvCoil", conditionOperand, operandLocals),
                Subnet(uid + 10, "Interlocks", "Interlock", "IlCoil", conditionOperand, operandLocals)));

            transEl.Add(new XElement("Transition",
                new XAttribute("Number", number),
                new XAttribute("Name", $"Trans{number}"),
                new XAttribute("ProgrammingLanguage", "LAD"),
                ConditionNet(uid + 20, step.TransitionOperand ?? conditionOperand, "TrCoil", operandLocals)));
        }

        // Linear chain S0 -> T0 -> S1 -> … -> Slast -> Tlast -> End (machine-verified shape).
        for (var i = 0; i < sequence.Count; i++)
        {
            connsEl.Add(Conn("Step", stepNumbers[i], "Transition", stepNumbers[i]));
            connsEl.Add(Conn("Transition", stepNumbers[i], "Step", i + 1 < sequence.Count ? stepNumbers[i + 1] : stepNumbers[i]));
        }

        // The last transition terminates the sequence (its target was itself above only to keep
        // the loop uniform); repoint the final NodeTo's CONTENT at the EndConnection terminator.
        var lastNodeTo = connsEl.Elements().Last().Element("NodeTo")!;
        lastNodeTo.ReplaceNodes(new XElement("EndConnection"));

        return new XElement(XName.Get("Graph", GraphNs),
            new XElement("PreOperations"),
            new XElement("Sequence", stepsEl, transEl, new XElement("Branches"), connsEl),
            new XElement("PostOperations"),
            AlarmsSettings());
    }

    private static XElement AlarmsSettings() => new("AlarmsSettings",
        new XElement("AlarmSupervisionCategories"),
        new XElement("AlarmInterlockCategory", new XAttribute("Id", 0)),
        new XElement("AlarmSubcategory1Interlock", new XAttribute("Id", 0)),
        new XElement("AlarmSubcategory2Interlock", new XAttribute("Id", 0)),
        new XElement("AlarmCategorySupervision", new XAttribute("Id", 0)),
        new XElement("AlarmSubcategory1Supervision", new XAttribute("Id", 0)),
        new XElement("AlarmSubcategory2Supervision", new XAttribute("Id", 0)),
        new XElement("AlarmWarningCategory", new XAttribute("Id", 0)),
        new XElement("AlarmSubcategory1Warning", new XAttribute("Id", 0)),
        new XElement("AlarmSubcategory2Warning", new XAttribute("Id", 0)));

    /// <summary>Per-step mandatory supervision/interlock subnet: rail → contact(cond) → Sv/IlCoil
    /// (machine-verified conditional form; the operand-less direct-rail form is rejected).</summary>
    private static XElement Subnet(int uid, string container, string item, string coil, string operand,
        HashSet<string> locals) =>
        new(container,
            new XElement(item, new XAttribute("ProgrammingLanguage", "LAD"),
                ConditionNet(uid, operand, coil, locals)));

    /// <summary>Condition subnet: rail → contact(operand) → terminator coil. Serves transitions
    /// (TrCoil) and per-step supervision (SvCoil) / interlock (IlCoil) alike. Operands declared in
    /// the spec interface resolve as LocalVariable — TIA validates global symbols AT IMPORT, so an
    /// undeclared GlobalVariable operand makes the whole import fail.</summary>
    private static XElement ConditionNet(int uid, string operand, string coil, HashSet<string> locals)
    {
        var name = operand.TrimStart('#');
        var scope = locals.Contains(name) ? "LocalVariable" : "GlobalVariable";
        return new XElement("FlgNet",
            new XElement("Parts",
                new XElement("Access", new XAttribute("Scope", scope), new XAttribute("UId", uid + 1),
                    new XElement("Symbol", new XElement("Component", new XAttribute("Name", name)))),
                new XElement("Part", new XAttribute("Name", "Contact"), new XAttribute("UId", uid + 2)),
                new XElement("Part", new XAttribute("Name", coil), new XAttribute("UId", uid + 3))),
            new XElement("Wires",
                new XElement("Wire", new XAttribute("UId", uid + 4),
                    new XElement("Powerrail"),
                    new XElement("NameCon", new XAttribute("UId", uid + 2), new XAttribute("Name", "in"))),
                new XElement("Wire", new XAttribute("UId", uid + 5),
                    new XElement("IdentCon", new XAttribute("UId", uid + 1)),
                    new XElement("NameCon", new XAttribute("UId", uid + 2), new XAttribute("Name", "operand"))),
                new XElement("Wire", new XAttribute("UId", uid + 6),
                    new XElement("NameCon", new XAttribute("UId", uid + 2), new XAttribute("Name", "out")),
                    new XElement("NameCon", new XAttribute("UId", uid + 3), new XAttribute("Name", "in")))));
    }

    private static string OperandText(string operand, HashSet<string> locals) =>
        locals.Contains(operand) ? "#" + operand : operand;

    private static XElement Conn(string fromKind, int fromNumber, string toKind, int toNumber) => new("Connection",
        new XElement("NodeFrom", new XElement($"{fromKind}Ref", new XAttribute("Number", fromNumber))),
        new XElement("NodeTo", new XElement($"{toKind}Ref", new XAttribute("Number", toNumber))),
        new XElement("LinkType", "Direct"));
}
