using System.Text;
using System.Xml.Linq;

namespace TiaMcp.SimaticML.StructuredText;

/// <summary>
/// Flattens a StructuredText/v4 token-stream body (how SCL blocks are stored inside SimaticML XML)
/// into display-grade plain SCL text. Not byte-identical to the official ExternalSources
/// GenerateSource output — callers surface a warning pointing there for authoritative text.
/// </summary>
internal static class StTokenFlattener
{
    public static string Flatten(XElement structuredText)
    {
        var sb = new StringBuilder();
        foreach (var el in structuredText.Elements())
        {
            switch (el.Name.LocalName)
            {
                case "Token":
                    sb.Append(el.Attribute("Text")?.Value ?? el.Value);
                    break;
                case "Blank":
                    sb.Append(' ');
                    break;
                case "NewLine":
                    sb.Append('\n');
                    break;
                case "Access":
                    // Access in the token stream carries the symbol inline (Symbol/Component or Text).
                    sb.Append(el.Attribute("Text")?.Value ?? FlgNet.FlgNetParser.ComponentPath(el) ?? el.Value.Trim());
                    break;
                case "Comment":
                    sb.Append(el.Value);
                    break;
                case "Keyword":
                    sb.Append(el.Attribute("Text")?.Value ?? el.Value);
                    break;
                default:
                    // Unknown token kinds (future schema additions) emit their text content raw.
                    sb.Append(el.Value);
                    break;
            }
        }

        return sb.ToString();
    }
}
