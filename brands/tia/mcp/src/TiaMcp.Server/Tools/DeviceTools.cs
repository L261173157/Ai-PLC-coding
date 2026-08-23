using System.ComponentModel;
using ModelContextProtocol.Server;
using TiaMcp.Contract;
using TiaMcp.Server.Safety;

namespace TiaMcp.Server.Tools;

/// <summary>Hardware tools: P4 device-item read; P6 catalog search / hardware read / device add / network configure.</summary>
[McpServerToolType]
public sealed class DeviceTools
{
    private readonly ITiaBackend _backend;
    private readonly AccessGuard _guard;
    private readonly AuditLog _audit;

    public DeviceTools(ITiaBackend backend, AccessGuard guard, AuditLog audit)
    {
        _backend = backend;
        _guard = guard;
        _audit = audit;
    }

    [McpServerTool(Name = "tia_device_item_list")]
    [Description(
        "List a device's hardware items (rack / modules / submodules) with slot, type identifier and " +
        "order number. Read-only. Use tia_project_list to enumerate devices first.")]
    public Task<object> TiaDeviceItemListAsync(
        [Description("Device path, e.g. session:s-fake/project:Demo/device:PLC_1.")] string path,
        CancellationToken ct = default) =>
        ToolErrors.InvokeAsync(() => _backend.ListDeviceItemsAsync(path, ct));

    // --- P6: hardware catalog / network provisioning ---

    [McpServerTool(Name = "tia_catalog_search")]
    [Description(
        "Search the TIA hardware equipment catalog for a query (order number / type name, e.g. '1516' " +
        "or '6ES7 510'). Returns matching entries with their typeIdentifier, which tia_device_add needs. " +
        "Read-only.")]
    public Task<object> TiaCatalogSearchAsync(
        [Description("Any session/project path (to select the connected TIA instance).")] string scopePath,
        [Description("Search text (article/order number or type name).")] string query,
        CancellationToken ct = default) =>
        ToolErrors.InvokeAsync(() => _backend.SearchEquipmentCatalogAsync(scopePath, query, ct));

    [McpServerTool(Name = "tia_hardware_read")]
    [Description(
        "Read the project's hardware configuration: devices (with rack/modules), subnets, and IO systems. " +
        "Read-only.")]
    public Task<object> TiaHardwareReadAsync(
        [Description("Project path, e.g. session:s-openness/project:Project1.")] string projectPath,
        CancellationToken ct = default) =>
        ToolErrors.InvokeAsync(() => _backend.ReadHardwareConfigAsync(projectPath, ct));

    [McpServerTool(Name = "tia_device_add")]
    [Description(
        "Create a device from a catalog typeIdentifier (write; needs --mode ReadWrite). " +
        "Use tia_catalog_search first to obtain the exact typeIdentifier.")]
    public async Task<MutationResult> TiaDeviceAddAsync(
        [Description("Project path.")] string projectPath,
        [Description("Catalog typeIdentifier, e.g. 'OrderNumber:6ES7 510-1DJ01-0AB0/V2.0'.")] string typeIdentifier,
        [Description("New device name.")] string deviceName,
        [Description("Root device-item name (default PLC_1).")] string deviceItemName = "PLC_1",
        CancellationToken ct = default)
    {
        const string op = TiaOps.AddNetworkDevice;
        var decision = _guard.Check(op, confirm: false);
        if (!decision.Allow)
        {
            return MutationResult.Denied(op, decision.DenyReason!);
        }

        try
        {
            var r = await _backend.AddNetworkDeviceAsync(projectPath, typeIdentifier, deviceName, deviceItemName, ct)
                .ConfigureAwait(false);
            _audit.Append(op, projectPath, success: true);
            var w = r.Warnings.Count > 0 ? " warnings: " + string.Join("; ", r.Warnings) : string.Empty;
            return MutationResult.Applied(op,
                $"Created device '{r.DeviceName}' ({r.TypeIdentifier}), root='{r.RootItemName}'.{w}");
        }
        catch (Exception ex)
        {
            _audit.Append(op, projectPath, success: false, error: ex.Message);
            return MutationResult.Failed(op, ex.Message);
        }
    }

    [McpServerTool(Name = "tia_network_configure")]
    [Description(
        "Configure a device's network interface (write; needs --mode ReadWrite): address, subnet mask, " +
        "device name, subnet, and IO system. Works for PROFINET (ipAddress = IPv4, ioSystem = IO-System) " +
        "and PROFIBUS (ipAddress = the integer station address as a string, ioSystem = DP-MasterSystem). " +
        "The subnet and IO system are CONNECTED if they already exist, or CREATED if not — so this stands " +
        "up an IO/DP controller on a fresh project. Returns which settings applied vs skipped.")]
    public async Task<MutationResult> TiaNetworkConfigureAsync(
        [Description("Project path.")] string projectPath,
        [Description("Device name to configure.")] string deviceName,
        [Description("PROFINET: IPv4 (e.g. 192.168.0.1). PROFIBUS: station address as a string (e.g. 5).")] string? ipAddress = null,
        [Description("PROFINET subnet mask (e.g. 255.255.255.0). Ignored for PROFIBUS.")] string? subnetMask = null,
        [Description("PROFINET device name (PnDeviceName).")] string? pnDeviceName = null,
        [Description("Subnet name to connect (created if it does not exist).")] string? subnetName = null,
        [Description("IO system name to connect/create (IO-System for PROFINET, DP-MasterSystem for PROFIBUS).")] string? ioSystemName = null,
        CancellationToken ct = default)
    {
        const string op = TiaOps.ConfigureNetworkDevice;
        var decision = _guard.Check(op, confirm: false);
        if (!decision.Allow)
        {
            return MutationResult.Denied(op, decision.DenyReason!);
        }

        try
        {
            var r = await _backend.ConfigureNetworkDeviceAsync(
                projectPath, deviceName, ipAddress, subnetMask, pnDeviceName, subnetName, ioSystemName, ct)
                .ConfigureAwait(false);
            _audit.Append(op, projectPath, success: true);
            var applied = r.AppliedSettings.Count > 0
                ? "applied: " + string.Join(", ", r.AppliedSettings.Keys) : "no settings applied";
            var skipped = r.SkippedSettings.Count > 0
                ? "; skipped: " + string.Join(", ", r.SkippedSettings.Select(kv => kv.Key + "(" + kv.Value + ")"))
                : string.Empty;
            return MutationResult.Applied(op, $"Configured '{deviceName}' — {applied}{skipped}");
        }
        catch (Exception ex)
        {
            _audit.Append(op, projectPath, success: false, error: ex.Message);
            return MutationResult.Failed(op, ex.Message);
        }
    }

    [McpServerTool(Name = "tia_cpu_system_clock_memory")]
    [Description(
        "Read or configure the CPU's System and clock memory. READING (omit all four params) works in " +
        "any access mode; WRITING (any one param set) needs --mode ReadWrite. " +
        "enableSystemMemory/systemMemoryByte -> FirstScan(bit0)/Diag(bit1)/AlwaysTRUE(bit2)/AlwaysFALSE(bit3); " +
        "enableClockMemory/clockMemoryByte -> 10/5/2.5/2Hz clock bits. Returns the resulting configuration. " +
        "Note: on S7-1200 Openness does NOT expose these attributes (EngineeringNotSupportedException); " +
        "the tool reports that clearly. S7-1500 reads/writes normally.")]
    public async Task<MutationResult> TiaCpuSystemClockMemoryAsync(
        [Description("Device path, e.g. .../device:PLC_1.")] string devicePath,
        [Description("Enable system memory byte (provides FirstScan/AlwaysTRUE/AlwaysFALSE).")] bool? enableSystemMemory = null,
        [Description("System memory byte address (e.g. 1 for MB1).")] long? systemMemoryByte = null,
        [Description("Enable clock memory byte (provides 10/5/2.5/2Hz pulses).")] bool? enableClockMemory = null,
        [Description("Clock memory byte address (e.g. 0 for MB0).")] long? clockMemoryByte = null,
        CancellationToken ct = default)
    {
        // The read (all params omitted) is not a mutation — gate it as a read so ReadOnly can read it;
        // any param present makes this a write that needs ReadWrite.
        bool isWrite = enableSystemMemory.HasValue || systemMemoryByte.HasValue
                    || enableClockMemory.HasValue || clockMemoryByte.HasValue;
        var op = isWrite ? TiaOps.ConfigureCpuMemory : TiaOps.ReadCpuMemory;
        var decision = _guard.Check(op, confirm: false);
        if (!decision.Allow)
        {
            return MutationResult.Denied(op, decision.DenyReason!);
        }

        try
        {
            var r = await _backend.ConfigureCpuMemoryAsync(
                devicePath, enableSystemMemory, systemMemoryByte, enableClockMemory, clockMemoryByte, ct)
                .ConfigureAwait(false);
            _audit.Append(op, devicePath, success: true);
            return MutationResult.Applied(op,
                $"CPU memory: SystemByte={r.SystemMemoryByte}(en={r.SystemMemoryEnabled}), " +
                $"ClockByte={r.ClockMemoryByte}(en={r.ClockMemoryEnabled})");
        }
        catch (Exception ex)
        {
            _audit.Append(op, devicePath, success: false, error: ex.Message);
            return MutationResult.Failed(op, ex.Message);
        }
    }

    [McpServerTool(Name = "tia_module_add")]
    [Description(
        "Plug a signal/communication module into a device's rack slot (write; needs --mode ReadWrite). " +
        "Use tia_catalog_search first to obtain the exact typeIdentifier. The slot defaults to the next " +
        "free rack slot (>=2; slot 1 is the CPU).")]
    public async Task<MutationResult> TiaModuleAddAsync(
        [Description("Project path.")] string projectPath,
        [Description("Device (station) to plug into, e.g. PLC_1.")] string deviceName,
        [Description("Catalog typeIdentifier, e.g. 'OrderNumber:6ES7 521-1BL00-0AB0/V2.0'.")] string typeIdentifier,
        [Description("Explicit rack slot; omit for next free slot (>=2).")] int? slot = null,
        [Description("Optional module name.")] string? moduleName = null,
        CancellationToken ct = default)
    {
        const string op = TiaOps.AddModule;
        var decision = _guard.Check(op, confirm: false);
        if (!decision.Allow)
        {
            return MutationResult.Denied(op, decision.DenyReason!);
        }

        try
        {
            var r = await _backend.AddModuleAsync(projectPath, deviceName, typeIdentifier, slot, moduleName, ct)
                .ConfigureAwait(false);
            _audit.Append(op, projectPath, success: true);
            return MutationResult.Applied(op,
                $"Plugged module '{r.ModuleName}' ({r.TypeIdentifier}) into {r.DeviceName} slot {r.Slot}.");
        }
        catch (Exception ex)
        {
            _audit.Append(op, projectPath, success: false, error: ex.Message);
            return MutationResult.Failed(op, ex.Message);
        }
    }

    // --- #5: hardware deletes (destructive; need ReadWrite AND confirm=true) ---

    [McpServerTool(Name = "tia_device_delete")]
    [Description(
        "Delete a device/station (destructive; needs --mode ReadWrite AND confirm=true). " +
        "Without confirm it returns a preview and does nothing.")]
    public Task<MutationResult> TiaDeviceDeleteAsync(
        [Description("Project path.")] string projectPath,
        [Description("Device (station) name to delete.")] string deviceName,
        [Description("Set true to actually delete; default false returns a preview.")] bool confirm = false,
        CancellationToken ct = default) =>
        DeleteAsync(TiaOps.DeleteDevice, projectPath, confirm,
            $"Delete device '{deviceName}'.",
            "tia_device_delete",
            () => _backend.DeleteDeviceAsync(projectPath, deviceName, ct),
            n => $"Deleted device '{n}'.");

    [McpServerTool(Name = "tia_module_delete")]
    [Description(
        "Delete a plugged module from a device's rack (destructive; needs --mode ReadWrite AND confirm=true). " +
        "Without confirm it returns a preview and does nothing.")]
    public Task<MutationResult> TiaModuleDeleteAsync(
        [Description("Project path.")] string projectPath,
        [Description("Device (station) the module is plugged into.")] string deviceName,
        [Description("Module (device-item) name to delete, as shown by tia_hardware_read / tia_device_item_list.")] string moduleName,
        [Description("Set true to actually delete; default false returns a preview.")] bool confirm = false,
        CancellationToken ct = default) =>
        DeleteAsync(TiaOps.DeleteModule, projectPath, confirm,
            $"Delete module '{moduleName}' from '{deviceName}'.",
            "tia_module_delete",
            () => _backend.DeleteModuleAsync(projectPath, deviceName, moduleName, ct),
            n => $"Deleted module '{n}'.");

    [McpServerTool(Name = "tia_subnet_delete")]
    [Description(
        "Delete a subnet (destructive; needs --mode ReadWrite AND confirm=true). " +
        "Without confirm it returns a preview and does nothing.")]
    public Task<MutationResult> TiaSubnetDeleteAsync(
        [Description("Project path.")] string projectPath,
        [Description("Subnet name to delete, e.g. PN/IE_1.")] string subnetName,
        [Description("Set true to actually delete; default false returns a preview.")] bool confirm = false,
        CancellationToken ct = default) =>
        DeleteAsync(TiaOps.DeleteSubnet, projectPath, confirm,
            $"Delete subnet '{subnetName}'.",
            "tia_subnet_delete",
            () => _backend.DeleteSubnetAsync(projectPath, subnetName, ct),
            n => $"Deleted subnet '{n}'.");

    /// <summary>Shared destructive-delete flow: guard (ReadWrite + confirm) -> backend -> audit -> result.</summary>
    private async Task<MutationResult> DeleteAsync(
        string op, string auditPath, bool confirm, string previewText, string recallTool,
        Func<Task<string>> deleteAsync, Func<string, string> appliedMessage)
    {
        var decision = _guard.Check(op, confirm);
        if (!decision.Allow)
        {
            return decision.NeedsConfirm
                ? MutationResult.Awaiting(op, previewText, $"Re-call {recallTool} with confirm=true to proceed.")
                : MutationResult.Denied(op, decision.DenyReason!);
        }

        try
        {
            var name = await deleteAsync().ConfigureAwait(false);
            _audit.Append(op, auditPath, success: true);
            return MutationResult.Applied(op, appliedMessage(name));
        }
        catch (Exception ex)
        {
            _audit.Append(op, auditPath, success: false, error: ex.Message);
            return MutationResult.Failed(op, ex.Message);
        }
    }
}
