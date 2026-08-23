using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcp.Contract;
using TiaMcp.Server.Safety;

namespace TiaMcp.Server.Tools;

/// <summary>System / capability-discovery tools. Call tia_status first to see what is wired.</summary>
[McpServerToolType]
public sealed class SystemTools
{
    private readonly ITiaBackend _backend;
    private readonly AccessGuard _guard;

    public SystemTools(ITiaBackend backend, AccessGuard guard)
    {
        _backend = backend;
        _guard = guard;
    }

    [McpServerTool(Name = "tia_status")]
    [Description(
        "Report TiaMcp server + TIA backend status: backend kind (Fake/Openness), TIA version, " +
        "access mode (the server's enforced tier), and whether real TIA is available. Always call this first.")]
    public Task<object> TiaStatusAsync(CancellationToken ct) =>
        ToolErrors.InvokeAsync(async () =>
        {
            var s = await _backend.GetStatusAsync(ct).ConfigureAwait(false);
            // The enforced access tier lives in the guard; the backend only knows backend-kind/version.
            return s with { AccessMode = _guard.Mode.ToString() };
        });
}
