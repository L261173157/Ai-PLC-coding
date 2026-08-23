using System.Text.RegularExpressions;
using System.Xml.Linq;
using TiaMcp.Contract;

namespace TiaMcp.SimaticML.Generator;

/// <summary>
/// Compiles a <see cref="CodeBlockSpec"/> into a complete SimaticML XML document ready for the
/// existing block-import path (ImportOptions.Override, idempotent). Validates the spec with clear
/// messages, auto-appends missing IEC-timer instances to Static, and lints the result against the
/// FlgNet iron rules before returning it. GRAPH specs are refused with bootstrap instructions
/// until the fixture lands (S3).
/// </summary>
public static partial class LadSpecGenerator
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierRegex();

    public static string Generate(CodeBlockSpec spec)
    {
        Validate(spec);

        // GRAPH compiles from the Sequence (machine-verified from-scratch shape); LAD from Networks.
        if (spec.Language?.Equals("GRAPH", StringComparison.OrdinalIgnoreCase) == true)
        {
            return GraphSpecGenerator.Generate(spec);
        }

        var effectiveInterface = AutoAddTimerInstances(spec);
        var locals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var datatypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in effectiveInterface)
        {
            foreach (var m in s.Members ?? Array.Empty<SpecMember>())
            {
                if (!string.IsNullOrEmpty(m.Name))
                {
                    locals.Add(m.Name!);
                    datatypes[m.Name!] = m.Datatype ?? "";
                }
            }
        }

        var flgNets = new List<XElement>();
        foreach (var network in spec.Networks ?? Array.Empty<SpecNetwork>())
        {
            var writer = new FlgNetWriter(locals, datatypes);
            flgNets.Add(writer.BuildNetwork(network.Rungs ?? Array.Empty<SpecRung>()));
        }

        var doc = EnvelopeBuilder.Build(spec, effectiveInterface, flgNets);
        var xml = doc.ToString(); // XDocument.ToString() includes the explicit declaration
        LintGenerated.Lint(xml);
        return xml;
    }

    private static void Validate(CodeBlockSpec spec)
    {
        if (string.IsNullOrEmpty(spec.Name) || !IdentifierRegex().IsMatch(spec.Name))
        {
            throw new ArgumentException($"spec name '{spec.Name}' is not a valid block identifier ([A-Za-z_][A-Za-z0-9_]*).");
        }

        if (spec.BlockType is not ("FB" or "FC"))
        {
            throw new ArgumentException($"spec blockType '{spec.BlockType}' must be FB or FC (OB/DB are not writable via write_code v1).");
        }

        if (spec.Language?.Equals("GRAPH", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (spec.BlockType.Equals("FB", StringComparison.OrdinalIgnoreCase))
            {
                return; // validated inside GraphSpecGenerator (sequence rules, qualifiers, …)
            }
        }

        if (!(spec.Language?.Equals("LAD", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            throw new ArgumentException($"spec language '{spec.Language}' must be LAD or GRAPH (FBD bodies are read-only in v1).");
        }

        if (spec.BlockType.Equals("FC", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var n in spec.Networks ?? Array.Empty<SpecNetwork>())
            {
                foreach (var r in n.Rungs ?? Array.Empty<SpecRung>())
                {
                    if (r.Output?.Instance is not null)
                    {
                        throw new ArgumentException(
                            "FC blocks cannot hold multi-instances — IEC timers (ton/tof/tp) need an FB's Static " +
                            "section for their instance. Use blockType FB, or SCL with a global instance DB.");
                    }
                }
            }
        }
    }

    /// <summary>Appends missing TON_TIME/TOF_TIME/TP_TIME instance members to the Static section so
    /// timer specs stay terse (with the instance then resolvable as a LocalVariable operand).</summary>
    private static IReadOnlyList<SpecSection> AutoAddTimerInstances(CodeBlockSpec spec)
    {
        var needed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // instance -> type
        foreach (var n in spec.Networks ?? Array.Empty<SpecNetwork>())
        {
            foreach (var r in n.Rungs ?? Array.Empty<SpecRung>())
            {
                var o = r.Output;
                if (o is null || o.Instance is null)
                {
                    continue;
                }

                var type = o.Kind.ToLowerInvariant() switch
                {
                    "ton" => "TON_TIME",
                    "tof" => "TOF_TIME",
                    "tp" => "TP_TIME",
                    _ => null,
                };
                if (type is not null)
                {
                    needed[o.Instance] = type;
                }
            }
        }

        if (needed.Count == 0)
        {
            return spec.Interface ?? Array.Empty<SpecSection>();
        }

        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sections = (spec.Interface ?? Array.Empty<SpecSection>()).ToList();
        foreach (var s in sections)
        {
            foreach (var m in s.Members ?? Array.Empty<SpecMember>())
            {
                if (!string.IsNullOrEmpty(m.Name))
                {
                    declared.Add(m.Name!);
                }
            }
        }

        var missing = needed.Where(kv => !declared.Contains(kv.Key)).ToList();
        if (missing.Count == 0)
        {
            return sections;
        }

        var staticSection = sections.FirstOrDefault(s => s.Section.Equals("Static", StringComparison.OrdinalIgnoreCase));
        var newMembers = missing.Select(kv => new SpecMember(kv.Key, kv.Value, null, null)).ToList();
        if (staticSection is null)
        {
            sections.Add(new SpecSection("Static", newMembers));
        }
        else
        {
            var replaced = new SpecSection("Static", (staticSection.Members ?? Array.Empty<SpecMember>()).Concat(newMembers).ToArray());
            sections[sections.IndexOf(staticSection)] = replaced;
        }

        return sections;
    }
}
