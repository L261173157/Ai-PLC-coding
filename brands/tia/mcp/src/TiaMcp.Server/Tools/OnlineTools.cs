using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcp.Contract;
using TiaMcp.Server.Safety;

namespace TiaMcp.Server.Tools;

/// <summary>Online / real-PLC tools (P4). download / run / stop need --mode Unrestricted AND confirm.
/// V21 LIMITATION: TIA Openness V21 exposes NO go-online / CPU RUN-STOP API — only DOWNLOAD is a real
/// online action. online_status / online_connect / online_disconnect / plc_run / plc_stop are simulated
/// by the Fake backend (for offline surface testing) but return NotSupported on the real Openness
/// backend. They are kept listed for Fake parity; do not rely on them against real hardware.</summary>
[McpServerToolType]
public sealed class OnlineTools
{
    private readonly ITiaBackend _backend;
    private readonly AccessGuard _guard;
    private readonly AuditLog _audit;

    public OnlineTools(ITiaBackend backend, AccessGuard guard, AuditLog audit)
    {
        _backend = backend;
        _guard = guard;
        _audit = audit;
    }

    [McpServerTool(Name = "tia_online_status")]
    [Description("Read a PLC device's online state (online/offline, RUN/STOP). Read-only. " +
        "NOTE: real V21 Openness has no online-state API — returns Unknown on the Openness backend " +
        "(simulated only on Fake).")]
    public Task<object> TiaOnlineStatusAsync(
        [Description("Device path, e.g. .../device:PLC_1.")] string path,
        CancellationToken ct = default) =>
        ToolErrors.InvokeAsync(() => _backend.GetOnlineStatusAsync(path, ct));

    [McpServerTool(Name = "tia_online_connect")]
    [Description("Establish the online connection to a PLC (write; needs --mode ReadWrite).")]
    public Task<MutationResult> TiaOnlineConnectAsync(
        [Description("Device path.")] string path,
        CancellationToken ct = default) =>
        GuardedAsync(TiaOps.OnlineConnect, path, confirm: false,
            () => _backend.ConnectOnlineAsync(path, ct), $"Connect online to '{path}'.");

    [McpServerTool(Name = "tia_online_disconnect")]
    [Description("Drop the online connection to a PLC (write; needs --mode ReadWrite).")]
    public Task<MutationResult> TiaOnlineDisconnectAsync(
        [Description("Device path.")] string path,
        CancellationToken ct = default) =>
        GuardedAsync(TiaOps.OnlineDisconnect, path, confirm: false,
            () => _backend.DisconnectOnlineAsync(path, ct), $"Disconnect '{path}'.");

    [McpServerTool(Name = "tia_download")]
    [Description(
        "Download hardware/software to the PLC (real hardware; needs --mode Unrestricted AND " +
        "confirm=true). Without confirm it returns a preview and does nothing. Requires an existing " +
        "online connection.")]
    public Task<MutationResult> TiaDownloadAsync(
        [Description("Device path.")] string path,
        [Description("Software | Hardware | All (default Software).")] string scope = "Software",
        bool confirm = false,
        CancellationToken ct = default) =>
        GuardedAsync(TiaOps.Download, path, confirm,
            () => _backend.DownloadAsync(path, EnumText.TryParse<CompileMode>(scope) ?? CompileMode.Software, ct),
            $"Download ({scope}) to PLC '{path}'.");

    [McpServerTool(Name = "tia_plc_run")]
    [Description("Set the PLC to RUN (needs --mode Unrestricted AND confirm=true). " +
        "NOT SUPPORTED on real V21 Openness (no CPU RUN/STOP API) — returns NotSupported on the " +
        "Openness backend; simulated only on Fake.")]
    public Task<MutationResult> TiaPlcRunAsync(
        [Description("Device path.")] string path,
        bool confirm = false,
        CancellationToken ct = default) =>
        GuardedAsync(TiaOps.PlcRun, path, confirm,
            () => _backend.PlcRunAsync(path, ct), $"Set PLC '{path}' to RUN.");

    [McpServerTool(Name = "tia_plc_stop")]
    [Description("Set the PLC to STOP (needs --mode Unrestricted AND confirm=true). " +
        "NOT SUPPORTED on real V21 Openness (no CPU RUN/STOP API) — returns NotSupported on the " +
        "Openness backend; simulated only on Fake.")]
    public Task<MutationResult> TiaPlcStopAsync(
        [Description("Device path.")] string path,
        bool confirm = false,
        CancellationToken ct = default) =>
        GuardedAsync(TiaOps.PlcStop, path, confirm,
            () => _backend.PlcStopAsync(path, ct), $"Set PLC '{path}' to STOP.");

    private async Task<MutationResult> GuardedAsync(
        string op, string scope, bool confirm, Func<Task> apply, string plan)
    {
        var decision = _guard.Check(op, confirm);
        if (!decision.Allow)
        {
            return decision.NeedsConfirm
                ? MutationResult.Awaiting(op, plan, $"Re-call {op} with confirm=true to proceed.")
                : MutationResult.Denied(op, decision.DenyReason!);
        }

        try
        {
            await apply();
            _audit.Append(op, scope, success: true);
            return MutationResult.Applied(op, plan);
        }
        catch (Exception ex)
        {
            _audit.Append(op, scope, success: false, error: ex.Message);
            return MutationResult.Failed(op, ex.Message);
        }
    }
}
