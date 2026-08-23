using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcp.Contract;

namespace TiaMcp.Server.Tools;

/// <summary>TIA Portal session lifecycle tools.</summary>
[McpServerToolType]
public sealed class SessionTools
{
    private readonly ITiaBackend _backend;

    public SessionTools(ITiaBackend backend) => _backend = backend;

    [McpServerTool(Name = "tia_connect")]
    [Description(
        "Launch / attach a TIA Portal session and return a session id plus the available " +
        "project names. Pass the returned session id to tia_block_list and other tools. " +
        "mode='attach' connects to an already-running TIA Portal the user opened (recommended — no " +
        "lock contention, no spawned instance); 'interactive'/'headless' spawn a new one.")]
    public Task<object> TiaConnectAsync(
        [Description("'attach' = use the running TIA Portal (must be open); 'interactive' = spawn visible window; 'headless' = spawn background. Default headless.")]
        string mode = "headless",
        CancellationToken ct = default) =>
        ToolErrors.InvokeAsync(() => _backend.ConnectAsync(
            new ConnectRequest(string.IsNullOrWhiteSpace(mode) ? "headless" : mode),
            ct));

    [McpServerTool(Name = "tia_disconnect")]
    [Description(
        "Drop the TIA Portal session: close any open projects and release the (headless) Portal process, " +
        "freeing its memory (~2 GB) WITHOUT killing the worker. Use this to release a headless instance " +
        "mid-session instead of OS-level process kills. The next tia_connect re-spawns a fresh Portal. " +
        "Safe in any access mode; idempotent (no-op if no session is open).")]
    public async Task<object> TiaDisconnectAsync(CancellationToken ct = default)
    {
        await _backend.DisconnectAsync(ct).ConfigureAwait(false);
        return new { status = "disconnected", backend = _backend.Kind.ToString() };
    }
}
