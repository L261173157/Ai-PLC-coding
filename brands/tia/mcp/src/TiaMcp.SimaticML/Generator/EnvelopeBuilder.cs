using System.Xml.Linq;
using TiaMcp.Contract;

namespace TiaMcp.SimaticML.Generator;

/// <summary>
/// Builds the SimaticML document envelope (AttributeList with Interface sections, ObjectList with
/// one CompileUnit per network). Byte-modeled on the two compile-verified skill examples:
/// an FB carries AutoNumber + Input/Output/InOut/Static/Temp/Constant (no HeaderVersion, no
/// Return section); an FC additionally carries HeaderVersion 0.1 and a trailing Return section
/// with Ret_Val : Void.
/// </summary>
internal static class EnvelopeBuilder
{
    private const string InterfaceNs = "http://www.siemens.com/automation/Openness/SW/Interface/v5";

    public static XDocument Build(
        CodeBlockSpec spec, IReadOnlyList<SpecSection> effectiveInterface, IReadOnlyList<XElement> flgNets)
    {
        var isFc = spec.BlockType.Equals("FC", StringComparison.OrdinalIgnoreCase);

        var attributeList = new XElement("AttributeList", new XElement("AutoNumber", "true"));
        if (isFc)
        {
            attributeList.Add(new XElement("HeaderVersion", "0.1"));
        }

        attributeList.Add(BuildInterface(isFc, effectiveInterface));
        attributeList.Add(new XElement("MemoryLayout", "Optimized"));
        attributeList.Add(new XElement("Name", spec.Name));
        attributeList.Add(new XElement("Namespace"));
        attributeList.Add(new XElement("Number", "0"));
        attributeList.Add(new XElement("ProgrammingLanguage", "LAD"));

        var objectList = new XElement("ObjectList");
        // Block comment first (matches export order); IDs 1000+ keep clear of CompileUnit IDs.
        // Live-verified 2026-08-23: cultures absent from the project are SILENTLY STRIPPED on
        // block import (zh-CN vanished on a default en-US project), so comments pin en-US.
        if (!string.IsNullOrEmpty(spec.Comment))
        {
            objectList.Add(new XElement("MultilingualText",
                new XAttribute("ID", 1000), new XAttribute("CompositionName", "Comment"),
                new XElement("ObjectList",
                    new XElement("MultilingualTextItem",
                        new XAttribute("ID", 1001), new XAttribute("CompositionName", "Items"),
                        new XElement("AttributeList",
                            new XElement("Culture", "en-US"),
                            new XElement("Text", spec.Comment!))))));
        }

        var unitId = 1;
        foreach (var flg in flgNets)
        {
            objectList.Add(new XElement("SW.Blocks.CompileUnit",
                new XAttribute("ID", unitId++), new XAttribute("CompositionName", "CompileUnits"),
                new XElement("AttributeList",
                    new XElement("NetworkSource", flg),
                    new XElement("ProgrammingLanguage", "LAD"))));
        }

        var blockEl = new XElement(isFc ? "SW.Blocks.FC" : "SW.Blocks.FB", new XAttribute("ID", 0),
            attributeList, objectList);

        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("Document",
                new XElement("Engineering", new XAttribute("version", "V21")),
                blockEl));
    }

    private static XElement BuildInterface(bool isFc, IReadOnlyList<SpecSection> sections)
    {
        var byName = new Dictionary<string, List<SpecMember>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sections)
        {
            if (string.IsNullOrEmpty(s.Section))
            {
                continue;
            }

            if (!byName.TryGetValue(s.Section, out var list))
            {
                byName[s.Section] = list = new List<SpecMember>();
            }

            list.AddRange(s.Members ?? Array.Empty<SpecMember>());
        }

        // Canonical section order from the verified examples; every section is emitted, even empty.
        var order = new[] { "Input", "Output", "InOut", "Static", "Temp", "Constant" };
        var sectionsEl = new XElement(XName.Get("Sections", InterfaceNs));
        foreach (var name in order)
        {
            var sec = new XElement("Section", new XAttribute("Name", name));
            foreach (var m in byName.TryGetValue(name, out var members) ? members : new List<SpecMember>())
            {
                var member = new XElement("Member",
                    new XAttribute("Name", m.Name ?? ""), new XAttribute("Datatype", m.Datatype ?? ""));
                if (!string.IsNullOrEmpty(m.Comment))
                {
                    // en-US, not zh-CN: cultures missing from the project are silently stripped on
                    // import (live-verified 2026-08-23) — Chinese text inside en-US survives fine.
                    member.Add(new XElement("Comment",
                        new XElement("MultiLanguageText", new XAttribute("Lang", "en-US"), m.Comment)));
                }

                if (!string.IsNullOrEmpty(m.StartValue))
                {
                    member.Add(new XElement("StartValue", m.StartValue));
                }

                sec.Add(member);
            }

            sectionsEl.Add(sec);
        }

        if (isFc)
        {
            sectionsEl.Add(new XElement("Section", new XAttribute("Name", "Return"),
                new XElement("Member", new XAttribute("Name", "Ret_Val"), new XAttribute("Datatype", "Void"))));
        }

        return new XElement("Interface", sectionsEl);
    }
}
