using System;
using System.Threading.Tasks;
using TiaMcp.Contract;

namespace TiaMcp.Server.Tools;

/// <summary>
/// Error transparency for the read-only MCP tools. The write tools already surface backend failures
/// via a structured <c>MutationResult</c>; the read-only tools previously let backend exceptions bubble,
/// which the MCP client masks as a generic "An error occurred invoking …" — hiding the real cause (an
/// Openness worker error, a not-found, a V21 NotSupportedException, …).
/// <para>Fix: read-only tools RETURN a result object instead of throwing. <see cref="InvokeAsync{T}"/>
/// returns the typed DTO on success or a <see cref="ToolError"/> on failure, so the actual message
/// reaches the agent (a tool result is then either its DTO or a <c>ToolError</c> — check the
/// <c>Error</c>/<c>Message</c> field).</para>
/// </summary>
internal static class ToolErrors
{
    public static async Task<object> InvokeAsync<T>(Func<Task<T>> run)
    {
        try
        {
            return (await run().ConfigureAwait(false))!;
        }
        catch (Exception ex)
        {
            return ToError(ex);
        }
    }

    /// <summary>Walk to the innermost non-empty message — unwraps the bridge's
    /// "Openness worker error (Type): msg" and Siemens Engineering* inner exceptions so the agent sees
    /// the real cause, not a wrapper.</summary>
    private static ToolError ToError(Exception ex)
    {
        string? message = null;
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(e.Message))
            {
                message = e.Message;
            }
        }

        return new ToolError(message ?? ex.GetType().Name, ex.GetType().FullName);
    }
}
