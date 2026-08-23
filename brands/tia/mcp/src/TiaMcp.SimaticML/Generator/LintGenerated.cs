using System.Xml.Linq;

namespace TiaMcp.SimaticML.Generator;

/// <summary>
/// Post-generation self-check of the FlgNet iron rules, run before the XML is handed to the import
/// path: exactly one powerrail wire per network, no source pin driving two wires, a Namespace
/// element present, and per-network UId uniqueness. Cheap insurance against generator drift.
/// </summary>
internal static class LintGenerated
{
    public static void Lint(string xml)
    {
        var doc = XDocument.Parse(xml);

        if (doc.Descendants().All(e => e.Name.LocalName != "Namespace"))
        {
            throw new InvalidOperationException("lint: generated XML is missing the Namespace element (import would fail with 'Missing Namespace identifier').");
        }

        foreach (var flg in doc.Descendants().Where(e => e.Name.LocalName == "FlgNet"))
        {
            var wires = flg.Descendants().Where(e => e.Name.LocalName == "Wire").ToList();

            var railWires = wires.Count(w => w.Elements().Any(c => c.Name.LocalName == "Powerrail"));
            if (railWires > 1)
            {
                throw new InvalidOperationException($"lint: network has {railWires} powerrail wires (max 1).");
            }

            // One wire per source pin: a (part,pin) may appear as a wire source at most once.
            var sources = wires
                .SelectMany(w => w.Elements().Where(c => c.Name.LocalName == "NameCon" && ReferenceEquals(c, w.Elements().First())))
                .GroupBy(c => c.Attribute("UId")?.Value + "." + c.Attribute("Name")?.Value)
                .FirstOrDefault(g => g.Count() > 1);
            if (sources is not null)
            {
                throw new InvalidOperationException($"lint: pin {sources.Key} drives more than one wire.");
            }

            // UId uniqueness applies to IDENTITY-carrying elements only — NameCon/IdentCon/OpenCon
            // carry UIds that REFERENCE other elements, so counting them would false-positive.
            var dupUids = flg.Descendants()
                .Where(e => e.Attribute("UId") is not null &&
                            e.Name.LocalName is "Access" or "Part" or "Wire" or "Instance")
                .GroupBy(e => e.Attribute("UId")!.Value)
                .FirstOrDefault(g => g.Count() > 1);
            if (dupUids is not null)
            {
                throw new InvalidOperationException($"lint: UId {dupUids.Key} is used more than once in a network.");
            }
        }
    }
}
