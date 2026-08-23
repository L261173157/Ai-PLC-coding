namespace TiaMcp.Contract;

/// <summary>One segment of a <see cref="TiaPath"/>, e.g. ("block", "FB_Motor").</summary>
public readonly record struct TiaSegment(string Kind, string Name)
{
    public override string ToString() => $"{Kind}:{Name}";
}

/// <summary>
/// Human-readable hierarchical path to a TIA object, e.g.
/// <c>session:s1/project:Demo/device:PLC_1/plc:program/block:FB_Motor</c>.
/// Parsed into <see cref="Segments"/> by <see cref="TiaPathParser"/>.
/// </summary>
public readonly record struct TiaPath(string Value)
{
    public IReadOnlyList<TiaSegment> Segments => TiaPathParser.Parse(Value);

    public override string ToString() => Value;

    public static implicit operator string(TiaPath p) => p.Value;

    public static implicit operator TiaPath(string s) => new(s);
}

/// <summary>
/// Parse/encode <see cref="TiaPath"/> segments. Known kinds: session, project, device, plc,
/// block (more added in later phases). Malformed segments are skipped so a bad path yields
/// "not found" rather than a protocol error.
/// </summary>
public static class TiaPathParser
{
    public static IReadOnlyList<TiaSegment> Parse(string? value)
    {
        var segments = new List<TiaSegment>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return segments;
        }

        // value is non-null here (we early-returned on null/whitespace above); the ! silences the
        // netstandard2.0 nullable warning. Substring (not range [..idx]) keeps this netstandard2.0-clean
        // (System.Range/Index are .NET Core 3+/5+ only).
        foreach (var raw in value!.Split('/'))
        {
            var part = raw.Trim();
            if (part.Length == 0)
            {
                continue;
            }

            var idx = part.IndexOf(':');
            if (idx <= 0 || idx >= part.Length - 1)
            {
                continue; // no "kind:name"
            }

            segments.Add(new TiaSegment(part.Substring(0, idx).Trim(), part.Substring(idx + 1).Trim()));
        }

        return segments;
    }
}
