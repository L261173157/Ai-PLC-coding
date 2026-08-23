using TiaMcp.Contract;

namespace TiaMcp.Server.Safety;

/// <summary>Stable mutation-operation identifiers shared by the tools and the guard.</summary>
public static class TiaOps
{
    // P2 — PLC project writes
    public const string ImportBlock = "import_block";
    public const string DeleteBlock = "delete_block";
    public const string CreateTag = "create_tag";
    public const string DeleteTag = "delete_tag";

    // P5 — project lifecycle (disk-writing mutations)
    public const string SaveProject = "save_project";
    public const string SaveProjectAs = "save_project_as";
    public const string CreateProject = "create_project";
    public const string ArchiveProject = "archive_project";
    public const string CloseProject = "close_project";

    // P1: compile (reconciles block versions and writes to disk — treat as a write)
    public const string CompileProject = "compile_project";

    // P6 — hardware catalog / network device provisioning
    public const string AddNetworkDevice = "add_network_device";
    public const string ConfigureNetworkDevice = "configure_network_device";
    public const string ConfigureCpuMemory = "configure_cpu_memory";
    /// <summary>Read-only twin of <see cref="ConfigureCpuMemory"/>: reading the system/clock-memory
    /// configuration (all params omitted) is not a mutation, so it is allowed in every access mode.</summary>
    public const string ReadCpuMemory = "read_cpu_memory";
    public const string AddModule = "add_module";

    // #5 — hardware deletes (destructive)
    public const string DeleteDevice = "delete_device";
    public const string DeleteModule = "delete_module";
    public const string DeleteSubnet = "delete_subnet";

    // P7 — data import / source generation / groups / library reuse
    public const string ImportUdt = "import_udt";
    public const string ImportTagTable = "import_tag_table";
    public const string GenerateBlocks = "generate_blocks";
    public const string CreateGroup = "create_group";
    public const string OpenLibrary = "open_library";
    public const string CreateBlockFromCopy = "create_block_from_copy";

    // #7 — structured block-body write (compiles a spec to SimaticML and imports it)
    public const string WriteBlockCode = "write_block_code";

    // P4 — online / real hardware
    public const string OnlineConnect = "online_connect";
    public const string OnlineDisconnect = "online_disconnect";
    public const string Download = "download";
    public const string PlcRun = "plc_run";
    public const string PlcStop = "plc_stop";
}

public sealed record GuardDecision(bool Allow, bool NeedsConfirm, string? DenyReason);

/// <summary>
/// Enforces the server's access policy before a write tool touches the backend. Pure policy: it
/// performs no mutation and writes no audit entry. Tiers:
/// <list type="bullet">
/// <item><b>Unrestricted ops</b> (download, plc_run, plc_stop): need <c>Unrestricted</c> mode AND <c>confirm</c>.</item>
/// <item><b>Write ops</b> (import/create, online connect/disconnect): need <c>ReadWrite+</c>.</item>
/// <item><b>Destructive project ops</b> (delete_block/tag): need <c>ReadWrite+</c> AND <c>confirm</c>.</item>
/// </list>
/// </summary>
public sealed class AccessGuard
{
    public AccessMode Mode { get; }

    public AccessGuard(AccessMode mode) => Mode = mode;

    public GuardDecision Check(string operation, bool confirm)
    {
        if (IsUnrestricted(operation))
        {
            if (Mode != AccessMode.Unrestricted)
            {
                return new GuardDecision(
                    Allow: false, NeedsConfirm: false,
                    DenyReason: "Needs --mode Unrestricted (real-hardware interaction).");
            }

            return confirm
                ? new GuardDecision(true, false, null)
                : new GuardDecision(false, true, null);
        }

        if (IsWrite(operation) && Mode == AccessMode.ReadOnly)
        {
            return new GuardDecision(
                Allow: false, NeedsConfirm: false,
                DenyReason: "Server is in ReadOnly mode. Restart the server with --mode ReadWrite to allow writes.");
        }

        if (IsDestructive(operation) && !confirm)
        {
            return new GuardDecision(Allow: false, NeedsConfirm: true, DenyReason: null);
        }

        return new GuardDecision(Allow: true, NeedsConfirm: false, DenyReason: null);
    }

    /// <summary>Affects real hardware (download, run/stop) — highest tier.</summary>
    public static bool IsUnrestricted(string op) =>
        op is TiaOps.Download or TiaOps.PlcRun or TiaOps.PlcStop;

    /// <summary>Mutates project/connection state — needs ReadWrite+.</summary>
    public static bool IsWrite(string op) =>
        op is TiaOps.ImportBlock or TiaOps.DeleteBlock or TiaOps.CreateTag or TiaOps.DeleteTag
            or TiaOps.OnlineConnect or TiaOps.OnlineDisconnect
            or TiaOps.SaveProject or TiaOps.SaveProjectAs or TiaOps.CreateProject
            or TiaOps.ArchiveProject or TiaOps.CloseProject
            or TiaOps.AddNetworkDevice or TiaOps.ConfigureNetworkDevice or TiaOps.ConfigureCpuMemory or TiaOps.AddModule
            or TiaOps.CompileProject
            or TiaOps.ImportUdt or TiaOps.ImportTagTable or TiaOps.GenerateBlocks
            or TiaOps.CreateGroup or TiaOps.OpenLibrary or TiaOps.CreateBlockFromCopy
            or TiaOps.WriteBlockCode
            or TiaOps.DeleteDevice or TiaOps.DeleteModule or TiaOps.DeleteSubnet;

    /// <summary>Irreversibly removes a project object — additionally needs confirm.</summary>
    public static bool IsDestructive(string op) =>
        op is TiaOps.DeleteBlock or TiaOps.DeleteTag
            or TiaOps.DeleteDevice or TiaOps.DeleteModule or TiaOps.DeleteSubnet;
}
