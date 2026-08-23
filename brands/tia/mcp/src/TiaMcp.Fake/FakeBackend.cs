using System.IO;
using System.Text;
using TiaMcp.Contract;

namespace TiaMcp.Fake;

/// <summary>
/// In-memory backend with a mutable virtual project, so the entire MCP tool surface — including
/// writes — can be exercised with no TIA Portal installed. All <c>FakeWorld</c> access is
/// serialized under <c>_lock</c> (the MCP SDK dispatches requests concurrently).
/// </summary>
public sealed class FakeBackend : ITiaBackend, ITiaCodeBackend
{
    public const string SessionId = "s-fake";
    public const string ProjectName = "Demo";

    private static readonly string SessionRoot = $"session:{SessionId}";
    private static readonly string PlcPath =
        $"{SessionRoot}/project:{ProjectName}/device:PLC_1/plc:program";
    private const string DefaultTagTable = "Default";

    private readonly FakeWorld _world = new(SessionId, ProjectName);
    private readonly object _lock = new();

    public TiaBackendKind Kind => TiaBackendKind.Fake;

    public string TiaVersion => "Fake-0.1";

    public Task<TiaStatus> GetStatusAsync(CancellationToken ct) =>
        Task.FromResult(new TiaStatus(
            Backend: "Fake",
            TiaVersion: TiaVersion,
            AccessMode: "ReadOnly",
            TiaAvailable: false,
            OpenSessions: 1));

    public Task<SessionInfo> ConnectAsync(ConnectRequest request, CancellationToken ct) =>
        Task.FromResult(new SessionInfo(SessionId, request.Mode, SessionRoot, [ProjectName]));

    // Fake holds no real Portal process; disconnect is a no-op (the surface still round-trips).
    public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;

    // --- P1: projects ---

    public Task<ProjectInfo> OpenProjectAsync(
        string sessionPath, OpenProjectRequest request, CancellationToken ct)
    {
        lock (_lock)
        {
            var proj = _world.FindProject(request.Path)
                       ?? throw new FileNotFoundException($"Fake exposes only project '{ProjectName}'.");
            return Task.FromResult(ToProjectInfo(proj));
        }
    }

    public Task<IReadOnlyList<TargetInfo>> ListTargetsAsync(string projectPath, CancellationToken ct)
    {
        lock (_lock)
        {
            var proj = _world.FindProject(projectPath)
                       ?? throw new FileNotFoundException("Project not found in the fake world.");
            var targets = proj.Devices.Select(d => new TargetInfo(
                d.Name,
                d.Kind,
                TypeName: d.Kind == TargetKind.Plc ? "CPU 1510C" : "TP700 Comfort",
                Path: $"{SessionRoot}/project:{proj.Name}/device:{d.Name}")).ToArray();
            return Task.FromResult<IReadOnlyList<TargetInfo>>(targets);
        }
    }

    public Task<CompileResult> CompileAsync(string scopePath, CompileMode mode, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_world.FindProject(scopePath) is null && _world.FindPlc(scopePath) is null)
            {
                throw new FileNotFoundException("Nothing to compile at the given path.");
            }
        }

        return Task.FromResult(new CompileResult(
            Mode: mode, Success: true, Errors: 0, Warnings: 0,
            Diagnostics: Array.Empty<CompileDiagnostic>(), DurationMs: 1));
    }

    // --- P5: project lifecycle (no real filesystem on Fake; ops are simulated on the Demo project) ---

    public Task<ProjectStatus> GetProjectStatusAsync(string projectPath, CancellationToken ct)
    {
        lock (_lock)
        {
            var proj = _world.FindProject(projectPath)
                       ?? throw new FileNotFoundException("Project not found in the fake world.");
            return Task.FromResult(FakeStatus(proj));
        }
    }

    public Task<ProjectLifecycleResult> SaveProjectAsync(string projectPath, CancellationToken ct) =>
        Task.FromResult(FakeLifecycle("save_project", projectPath));

    public Task<ProjectLifecycleResult> SaveProjectAsAsync(
        string projectPath, string targetDirectory, string targetName, bool rebind, CancellationToken ct)
    {
        lock (_lock)
        {
            var proj = _world.FindProject(projectPath)
                       ?? throw new FileNotFoundException("Project not found in the fake world.");
            var s = FakeStatus(proj) with { Path = Path.Combine(targetDirectory, targetName) };
            return Task.FromResult(new ProjectLifecycleResult("save_project_as", s.Path, s));
        }
    }

    public Task<ProjectLifecycleResult> CreateProjectAsync(
        string sessionPath, string projectDirectory, string projectName, string? author, string? comment, CancellationToken ct) =>
        throw new NotSupportedException(
            "Fake backend exposes only the 'Demo' project; project creation needs real TIA.");

    public Task<ProjectLifecycleResult> ArchiveProjectAsync(
        string projectPath, string archiveDirectory, string archiveName, ArchiveMode mode, bool saveBeforeArchive, CancellationToken ct) =>
        Task.FromResult(FakeLifecycle("archive_project", projectPath));

    public Task<ProjectLifecycleResult> CloseProjectAsync(string projectPath, bool saveBeforeClose, CancellationToken ct)
    {
        lock (_lock)
        {
            var proj = _world.FindProject(projectPath)
                       ?? throw new FileNotFoundException("Project not found in the fake world.");
            var s = FakeStatus(proj) with { IsOpen = false };
            return Task.FromResult(new ProjectLifecycleResult("close_project", s.Path, s));
        }
    }

    private ProjectLifecycleResult FakeLifecycle(string operation, string projectPath)
    {
        lock (_lock)
        {
            var proj = _world.FindProject(projectPath)
                       ?? throw new FileNotFoundException("Project not found in the fake world.");
            var s = FakeStatus(proj);
            return new ProjectLifecycleResult(operation, s.Path, s);
        }
    }

    private static ProjectStatus FakeStatus(FakeWorld.Project proj) => new(
        IsOpen: true,
        Name: proj.Name,
        Path: $"{SessionRoot}/project:{proj.Name}",
        Version: "Fake-0.1",
        Author: proj.Author,
        IsModified: false,
        CreationTime: null,
        LastModified: null,
        LastModifiedBy: null,
        Size: null);

    // --- P1: blocks (read) ---

    public Task<ListBlocksResult> ListBlocksAsync(
        string scopePath, BlockType? filter, int limit, int offset, CancellationToken ct)
    {
        lock (_lock)
        {
            var plc = _world.FindPlc(scopePath)
                      ?? throw new FileNotFoundException("No PLC found at the given path.");
            var filtered = (filter is null
                ? plc.Blocks
                : plc.Blocks.Where(b => b.Type == filter.Value)).ToArray();
            var page = filtered
                .Skip(Math.Max(0, offset))
                .Take(limit <= 0 ? 50 : Math.Min(limit, 500))
                .ToArray();
            return Task.FromResult(new ListBlocksResult(
                PlcPath, Total: filtered.Length, Offset: Math.Max(0, offset),
                Limit: page.Length, Blocks: page.Select(ToBlockInfo).ToArray()));
        }
    }

    public Task<BlockInfo> GetBlockAsync(string blockPath, CancellationToken ct)
    {
        lock (_lock)
        {
            var b = _world.FindBlock(blockPath)
                    ?? throw new FileNotFoundException($"Block not found at '{blockPath}'.");
            return Task.FromResult(ToBlockInfo(b));
        }
    }

    public Task<BlockSource> ReadBlockSourceAsync(string blockPath, CancellationToken ct)
    {
        lock (_lock)
        {
            var b = _world.FindBlock(blockPath)
                    ?? throw new FileNotFoundException($"Block not found at '{blockPath}'.");
            return Task.FromResult(new BlockSource(blockPath, b.Language, b.Source));
        }
    }

    public Task<BlockCode> ReadBlockCodeAsync(string blockPath, ReadCodeOptions options, CancellationToken ct)
    {
        lock (_lock)
        {
            var b = _world.FindBlock(blockPath)
                    ?? throw new FileNotFoundException($"Block not found at '{blockPath}'.");
            return Task.FromResult(SimaticML.SimaticMlCodeReader.Read(b.Source, blockPath, options));
        }
    }

    public Task<string> GenerateBlockCodeAsync(CodeBlockSpec spec, CancellationToken ct) =>
        Task.FromResult(SimaticML.Generator.LadSpecGenerator.Generate(spec));

    public Task<ExportResult> ExportBlockAsync(
        string blockPath, ExportFormat format, string? outDir, CancellationToken ct)
    {
        lock (_lock)
        {
            var b = _world.FindBlock(blockPath)
                    ?? throw new FileNotFoundException($"Block not found at '{blockPath}'.");
            var dir = string.IsNullOrWhiteSpace(outDir)
                ? Path.Combine(Path.GetTempPath(), "tiamcp-export")
                : outDir;
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, b.Name + (format == ExportFormat.Xml ? ".xml" : ".scl"));

            // Real SimaticML (seeded LAD block, write_code output, imported XML) passes through
            // verbatim; only the legacy stub sources fall back to the pseudo-envelope.
            var xmlContent = b.Source.TrimStart().StartsWith('<') ? b.Source : ToSimaticMl(b);
            File.WriteAllText(file, format == ExportFormat.Xml ? xmlContent : b.Source, new UTF8Encoding(false));
            var bytes = (int)new FileInfo(file).Length;
            return Task.FromResult(new ExportResult(blockPath, format, file, bytes));
        }
    }

    // --- P2: tags (read) ---

    public Task<ListTagsResult> ListTagsAsync(string scopePath, int limit, int offset, CancellationToken ct)
    {
        lock (_lock)
        {
            // Parity with Openness: honor a `tagtable:NAME` scope segment. Fake models only the single
            // default table, so any other named table yields an empty page.
            var seg = ScopeSegment(scopePath, "tagtable");
            if (!string.IsNullOrEmpty(seg) && seg != DefaultTagTable)
            {
                var emptyPath = $"{PlcPath}/tagtable:{seg}";
                return Task.FromResult(new ListTagsResult(
                    emptyPath, Total: 0, Offset: Math.Max(0, offset),
                    Limit: 0, Tags: Array.Empty<TagInfo>()));
            }

            var tags = _world.GetTags(scopePath);
            var page = tags
                .Skip(Math.Max(0, offset))
                .Take(limit <= 0 ? 50 : Math.Min(limit, 500))
                .ToArray();
            var tablePath = $"{PlcPath}/tagtable:{DefaultTagTable}";
            return Task.FromResult(new ListTagsResult(
                tablePath, Total: tags.Count, Offset: Math.Max(0, offset),
                Limit: page.Length, Tags: page.Select(t => ToTagInfo(t, tablePath)).ToArray()));
        }
    }

    // --- P2: writes (mechanics only; the guard gates these) ---

    public Task<string> ImportBlockAsync(string plcPath, CreateBlockRequest request, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_world.FindPlc(plcPath) is null)
            {
                throw new FileNotFoundException("No PLC found at the given path.");
            }

            var block = new FakeWorld.Block
            {
                Name = request.Name,
                Type = request.Type,
                Language = string.IsNullOrWhiteSpace(request.Language) ? "SCL" : request.Language,
                Comment = "Imported via MCP",
                Source = request.Source ?? string.Empty,
            };

            // Parity with the real backend: sources that claim to be SimaticML XML must parse as XML.
            // Legacy plain-SCL text (smoke_mcp's TEST_BLOCK_SOURCE) does not start with '<' and passes.
            if (block.Source.TrimStart().StartsWith('<'))
            {
                try
                {
                    System.Xml.Linq.XDocument.Parse(block.Source);
                }
                catch (Exception ex)
                {
                    throw new ArgumentException(
                        $"source looks like SimaticML XML but does not parse: {ex.Message} " +
                        "(raw SCL text belongs in tia_block_generate_from_source).");
                }
            }

            if (!_world.AddBlock(plcPath, block))
            {
                throw new InvalidOperationException($"Block '{request.Name}' already exists.");
            }

            return Task.FromResult($"{PlcPath}/block:{request.Name}");
        }
    }

    public Task<string> WriteBlockCodeAsync(string plcPath, CodeBlockSpec spec, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_world.FindPlc(plcPath) is null)
            {
                throw new FileNotFoundException("No PLC found at the given path.");
            }

            // Deterministic generation is shared with the Openness bridge; validation errors
            // (unsupported subset, missing operands, GRAPH-before-fixture) throw here identically.
            var xml = SimaticML.Generator.LadSpecGenerator.Generate(spec);
            System.Xml.Linq.XDocument.Parse(xml); // parity with the real import path

            var type = BlockTypeParser.TryParse(spec.BlockType) ?? BlockType.FB;
            var path = $"{PlcPath}/block:{spec.Name}";

            // ImportOptions.Override parity with the real backend: same-name rewrites replace in place.
            var language = spec.Language?.Equals("GRAPH", StringComparison.OrdinalIgnoreCase) == true ? "GRAPH" : "LAD";
            var existing = _world.FindBlock(path);
            if (existing is not null)
            {
                existing.Type = type;
                existing.Language = language;
                existing.Comment = spec.Comment ?? existing.Comment;
                existing.Source = xml;
                return Task.FromResult(path);
            }

            if (!_world.AddBlock(plcPath, new FakeWorld.Block
            {
                Name = spec.Name,
                Type = type,
                Language = language,
                Comment = spec.Comment ?? "Written via tia_block_write_code",
                Source = xml,
            }))
            {
                throw new InvalidOperationException($"Block '{spec.Name}' already exists.");
            }

            return Task.FromResult(path);
        }
    }

    public Task DeleteBlockAsync(string blockPath, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_world.RemoveBlock(blockPath))
            {
                throw new FileNotFoundException($"Block not found at '{blockPath}'.");
            }

            return Task.CompletedTask;
        }
    }

    public Task<string> CreateTagAsync(string tagTablePath, CreateTagRequest request, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_world.FindPlc(tagTablePath) is null)
            {
                throw new FileNotFoundException("No PLC / tag table found at the given path.");
            }

            var tag = new FakeWorld.Tag
            {
                Name = request.Name,
                Address = request.Address,
                DataType = string.IsNullOrWhiteSpace(request.DataType) ? "Bool" : request.DataType,
                Comment = request.Comment,
            };

            if (!_world.AddTag(tagTablePath, tag))
            {
                throw new InvalidOperationException($"Tag '{request.Name}' already exists.");
            }

            return Task.FromResult($"{PlcPath}/tagtable:{DefaultTagTable}/tag:{request.Name}");
        }
    }

    public Task DeleteTagAsync(string tagPath, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_world.RemoveTag(tagPath))
            {
                throw new FileNotFoundException($"Tag not found at '{tagPath}'.");
            }

            return Task.CompletedTask;
        }
    }

    // --- P4: device items (hardware visibility; read) ---

    public Task<IReadOnlyList<DeviceItemInfo>> ListDeviceItemsAsync(string devicePath, CancellationToken ct)
    {
        lock (_lock)
        {
            var dev = _world.FindDevice(devicePath)
                      ?? throw new FileNotFoundException($"Device not found at '{devicePath}'.");
            var items = dev.Items.Select(i => new DeviceItemInfo(
                i.Name, i.TypeIdentifier, i.Slot, i.TypeName, $"{devicePath}/item:{i.Name}")).ToArray();
            return Task.FromResult<IReadOnlyList<DeviceItemInfo>>(items);
        }
    }

    // --- P6: hardware catalog / network device provisioning (canned on Fake) ---

    public Task<IReadOnlyList<CatalogEntry>> SearchEquipmentCatalogAsync(
        string scopePath, string query, CancellationToken ct)
    {
        var seed = new[]
        {
            new CatalogEntry("CPU 1516", "6ES7 516-3AN01-0AB0", "V3.0",
                "OrderNumber:6ES7 516-3AN01-0AB0/V3.0", "PLC/S7-1500/CPU1516", "S7-1500 CPU"),
            new CatalogEntry("CPU 1510C", "6ES7 510-1DJ01-0AB0", "V2.0",
                "OrderNumber:6ES7 510-1DJ01-0AB0/V2.0", "PLC/S7-1500/CPU1510C", "S7-1500 CPU compact"),
        };
        var q = (query ?? string.Empty).Trim();
        IEnumerable<CatalogEntry> r = string.IsNullOrEmpty(q)
            ? seed
            : seed.Where(c =>
                (c.TypeName ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (c.ArticleNumber ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult<IReadOnlyList<CatalogEntry>>(r.ToList());
    }

    public Task<AddDeviceResult> AddNetworkDeviceAsync(
        string projectPath, string typeIdentifier, string deviceName, string deviceItemName, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_world.FindProject(projectPath) is null)
            {
                throw new FileNotFoundException("Project not found in the fake world.");
            }
        }
        return Task.FromResult(new AddDeviceResult(deviceName, deviceItemName, typeIdentifier, Array.Empty<string>()));
    }

    public Task<ConfigureNetworkDeviceResult> ConfigureNetworkDeviceAsync(
        string projectPath, string deviceName, string? ipAddress, string? subnetMask,
        string? pnDeviceName, string? subnetName, string? ioSystemName, CancellationToken ct)
    {
        var applied = new Dictionary<string, string>();
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(ipAddress)) applied["Address"] = ipAddress!;
            if (!string.IsNullOrWhiteSpace(subnetMask)) applied["SubnetMask"] = subnetMask!;
            if (!string.IsNullOrWhiteSpace(pnDeviceName)) applied["PnDeviceName"] = pnDeviceName!;

            // Mirror the Openness create-if-missing behavior: record the subnet/IO system on the
            // project so ReadHardwareConfig reflects them.
            var proj = _world.FindProject(projectPath);
            if (proj is not null)
            {
                if (!string.IsNullOrWhiteSpace(subnetName))
                {
                    if (!proj.Subnets.Contains(subnetName!)) proj.Subnets.Add(subnetName!);
                    applied["SubnetName"] = subnetName + " (created)";
                }
                if (!string.IsNullOrWhiteSpace(ioSystemName))
                {
                    if (!proj.IoSystems.Contains(ioSystemName!)) proj.IoSystems.Add(ioSystemName!);
                    applied["IoSystemName"] = ioSystemName + " (created)";
                }
            }
        }
        return Task.FromResult(new ConfigureNetworkDeviceResult(
            deviceName, applied, new Dictionary<string, string>(), Array.Empty<string>()));
    }

    public Task<CpuMemoryConfig> ConfigureCpuMemoryAsync(
        string devicePath, bool? enableSystemMemory, long? systemMemoryByte,
        bool? enableClockMemory, long? clockMemoryByte, CancellationToken ct) =>
        Task.FromResult(new CpuMemoryConfig(
            devicePath,
            enableSystemMemory ?? false,
            systemMemoryByte ?? 0,
            enableClockMemory ?? false,
            clockMemoryByte ?? 0));

    public Task<AddModuleResult> AddModuleAsync(
        string projectPath, string deviceName, string typeIdentifier, int? slot, string? moduleName, CancellationToken ct)
    {
        lock (_lock)
        {
            var dev = _world.FindDevice(projectPath + "/device:" + deviceName)
                      ?? throw new FileNotFoundException($"Device '{deviceName}' not found in the fake world.");
            var targetSlot = slot ?? NextFakeSlot(dev, minSlot: 2);
            var name = string.IsNullOrWhiteSpace(moduleName) ? typeIdentifier : moduleName!;
            dev.Items.Add(new FakeWorld.Item
            {
                Name = name, TypeIdentifier = typeIdentifier, Slot = targetSlot, TypeName = moduleName,
            });
            return Task.FromResult(new AddModuleResult(
                deviceName, name, typeIdentifier, targetSlot,
                projectPath + "/device:" + deviceName + "/item:" + name));
        }
    }

    private static int NextFakeSlot(FakeWorld.Device dev, int minSlot)
    {
        var occupied = new HashSet<int>(dev.Items.Select(i => i.Slot));
        var s = minSlot;
        while (occupied.Contains(s)) s++;
        return s;
    }

    public Task<HardwareConfig> ReadHardwareConfigAsync(string projectPath, CancellationToken ct)
    {
        lock (_lock)
        {
            var proj = _world.FindProject(projectPath)
                       ?? throw new FileNotFoundException("Project not found in the fake world.");
            var devs = proj.Devices.Select(d => new HwDevice(
                d.Name,
                d.Kind == TargetKind.Plc ? "System:Device.S71500" : "System:Device.Hmi",
                d.Items.Select(i => new HwDeviceItem(
                    i.Name, i.TypeIdentifier, i.Slot, null, Array.Empty<HwDeviceItem>())).ToList())).ToList();
            var subnets = proj.Subnets.Select(n => new HwSubnet(
                n, "Ethernet", Array.Empty<string>(),
                proj.IoSystems.Select(io => new HwIoSystem(io, null, Array.Empty<string>())).ToList())).ToList();
            return Task.FromResult(new HardwareConfig(devs, subnets));
        }
    }

    // --- P7: data import / source generation / groups / library reuse ---
    // Fake parses no SimaticML and hosts no real library; it simulates just enough for the surface to
    // round-trip offline. Names are pulled from a Name="..." attribute when present, else synthesized.

    public Task<ImportResult> ImportUdtAsync(string plcPath, string sourceXml, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_world.FindPlc(plcPath) is null)
            {
                throw new FileNotFoundException("No PLC found at the given path.");
            }
            var name = ExtractXmlName(sourceXml) ?? "FakeUdt";
            return Task.FromResult(new ImportResult(
                $"{PlcPath}/type:{name}", new[] { name }, $"Imported UDT '{name}' (fake)."));
        }
    }

    public Task<ImportResult> ImportTagTableAsync(string plcPath, string sourceXml, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_world.FindPlc(plcPath) is null)
            {
                throw new FileNotFoundException("No PLC found at the given path.");
            }
            var name = ExtractXmlName(sourceXml) ?? "FakeTagTable";
            return Task.FromResult(new ImportResult(
                $"{PlcPath}/tagtable:{name}", new[] { name }, $"Imported tag table '{name}' (fake)."));
        }
    }

    public Task<ImportResult> GenerateBlocksFromSourceAsync(
        string plcPath, string sourceName, string sourceText, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_world.FindPlc(plcPath) is null)
            {
                throw new FileNotFoundException("No PLC found at the given path.");
            }
            var name = string.IsNullOrWhiteSpace(sourceName) ? "FakeBlock" : sourceName;
            var block = new FakeWorld.Block
            {
                Name = name, Type = BlockType.FB, Language = "SCL",
                Comment = "Generated from source (fake)", Source = sourceText ?? string.Empty,
            };
            _world.AddBlock(plcPath, block);
            return Task.FromResult(new ImportResult(
                PlcPath, new[] { name }, $"Generated block '{name}' from source (fake)."));
        }
    }

    public Task<string> CreateGroupAsync(string plcPath, string groupKind, string groupName, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_world.FindPlc(plcPath) is null)
            {
                throw new FileNotFoundException("No PLC found at the given path.");
            }
            var kind = NormalizeGroupKind(groupKind);
            return Task.FromResult($"{PlcPath}/{kind}group:{groupName}");
        }
    }

    public Task<LibraryInfo> OpenLibraryAsync(string libraryPath, bool readOnly, CancellationToken ct) =>
        throw new NotSupportedException("Fake backend hosts no global library; needs real TIA (use --backend openness).");

    public Task<IReadOnlyList<MasterCopyInfo>> ListMasterCopiesAsync(string libraryName, CancellationToken ct) =>
        throw new NotSupportedException("Fake backend hosts no global library; needs real TIA (use --backend openness).");

    public Task<string> CreateBlockFromCopyAsync(string plcPath, string libraryName, string masterCopyName, CancellationToken ct) =>
        throw new NotSupportedException("Fake backend hosts no global library; needs real TIA (use --backend openness).");

    // --- #6: enumerate UDTs / tag tables (read; derived from the fake world) ---

    public Task<ListUdtsResult> ListUdtsAsync(string scopePath, CancellationToken ct)
    {
        lock (_lock)
        {
            var plc = _world.FindPlc(scopePath)
                      ?? throw new FileNotFoundException("No PLC found at the given path.");
            var udts = plc.Blocks
                .Where(b => b.Type == BlockType.UDT)
                .Select(b => new UdtInfo(b.Name, "", $"{PlcPath}/block:{b.Name}"))
                .ToArray();
            return Task.FromResult(new ListUdtsResult(PlcPath, udts.Length, udts));
        }
    }

    public Task<ListTagTablesResult> ListTagTablesAsync(string scopePath, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_world.FindPlc(scopePath) is null)
            {
                throw new FileNotFoundException("No PLC found at the given path.");
            }
            var tablePath = $"{PlcPath}/tagtable:{DefaultTagTable}";
            var table = new TagTableInfo(DefaultTagTable, _world.GetTags(scopePath).Count, "", tablePath);
            return Task.FromResult(new ListTagTablesResult(PlcPath, 1, new[] { table }));
        }
    }

    public Task<ExportResult> ExportTagTableAsync(string tagTablePath, string? outDir, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_world.FindPlc(tagTablePath) is null)
            {
                throw new FileNotFoundException("No PLC / tag table found at the given path.");
            }
            var dir = string.IsNullOrWhiteSpace(outDir)
                ? Path.Combine(Path.GetTempPath(), "tiamcp-export")
                : outDir;
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, DefaultTagTable + ".xml");
            File.WriteAllText(file, ToSimaticMlTagTable(DefaultTagTable), new UTF8Encoding(false));
            var bytes = (int)new FileInfo(file).Length;
            return Task.FromResult(new ExportResult(tagTablePath, ExportFormat.Xml, file, bytes));
        }
    }

    private static string ToSimaticMlTagTable(string name)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine($"<!-- Fake SimaticML export for tag table {name} (not importable into TIA) -->");
        sb.AppendLine("<Document>");
        sb.AppendLine($"  <SW.Tags.PlcTagTable Name=\"{name}\"/>");
        sb.AppendLine("</Document>");
        return sb.ToString();
    }

    public Task<InterfaceInfo> ReadInterfaceAsync(string path, CancellationToken ct) =>
        throw new NotSupportedException("Structured interface read needs real TIA (use --backend openness).");

    public Task<CrossRefResult> GetCrossReferencesAsync(string path, CancellationToken ct) =>
        throw new NotSupportedException("Cross-references need real TIA (use --backend openness).");

    public Task<string> DeleteDeviceAsync(string projectPath, string deviceName, CancellationToken ct) =>
        throw new NotSupportedException("Hardware delete needs real TIA (use --backend openness).");

    public Task<string> DeleteModuleAsync(string projectPath, string deviceName, string moduleName, CancellationToken ct) =>
        throw new NotSupportedException("Hardware delete needs real TIA (use --backend openness).");

    public Task<string> DeleteSubnetAsync(string projectPath, string subnetName, CancellationToken ct) =>
        throw new NotSupportedException("Hardware delete needs real TIA (use --backend openness).");

    /// <summary>Best-effort scrape of the first <c>Name="..."</c> attribute from an XML string.</summary>
    private static string? ExtractXmlName(string? xml)
    {
        if (string.IsNullOrEmpty(xml))
        {
            return null;
        }
        var m = System.Text.RegularExpressions.Regex.Match(xml, "Name\\s*=\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string NormalizeGroupKind(string groupKind)
    {
        var k = (groupKind ?? string.Empty).Trim().ToLowerInvariant();
        return k switch
        {
            "type" or "udt" => "type",
            "tag" or "tagtable" => "tagtable",
            _ => "block",
        };
    }

    /// <summary>Extract the value of a <c>key:VALUE</c> segment from a scope path (up to the next
    /// '/'), e.g. <c>ScopeSegment(".../tagtable:Default/tag:x", "tagtable")</c> -> "Default".
    /// Returns null when the segment is absent. netstandard2.0-safe (no range operator).</summary>
    private static string? ScopeSegment(string scope, string key)
    {
        var token = key + ":";
        var i = scope.IndexOf(token, StringComparison.Ordinal);
        if (i < 0) return null;
        var start = i + token.Length;
        var end = scope.IndexOf('/', start);
        return end < 0 ? scope.Substring(start) : scope.Substring(start, end - start);
    }

    // --- P4: online (simulated state machine on Fake) ---

    public Task<OnlineStatus> GetOnlineStatusAsync(string devicePath, CancellationToken ct)
    {
        lock (_lock)
        {
            var dev = _world.FindDevice(devicePath)
                      ?? throw new FileNotFoundException($"Device not found at '{devicePath}'.");
            return Task.FromResult(new OnlineStatus(devicePath, dev.Online, dev.PlcState, null));
        }
    }

    public Task ConnectOnlineAsync(string devicePath, CancellationToken ct)
    {
        lock (_lock)
        {
            var dev = RequireDevice(devicePath);
            dev.Online = true;
            dev.PlcState = "Stop";
            return Task.CompletedTask;
        }
    }

    public Task DisconnectOnlineAsync(string devicePath, CancellationToken ct)
    {
        lock (_lock)
        {
            var dev = RequireDevice(devicePath);
            dev.Online = false;
            dev.PlcState = "Offline";
            return Task.CompletedTask;
        }
    }

    public Task DownloadAsync(string devicePath, CompileMode scope, CancellationToken ct)
    {
        lock (_lock)
        {
            var dev = RequireDevice(devicePath);
            if (!dev.Online)
            {
                throw new InvalidOperationException("PLC is not online; call tia_online_connect first.");
            }

            return Task.CompletedTask;
        }
    }

    public Task PlcRunAsync(string devicePath, CancellationToken ct)
    {
        lock (_lock)
        {
            var dev = RequireDevice(devicePath);
            dev.PlcState = dev.Online ? "Run" : "Unknown";
            return Task.CompletedTask;
        }
    }

    public Task PlcStopAsync(string devicePath, CancellationToken ct)
    {
        lock (_lock)
        {
            var dev = RequireDevice(devicePath);
            dev.PlcState = dev.Online ? "Stop" : "Offline";
            return Task.CompletedTask;
        }
    }

    private FakeWorld.Device RequireDevice(string devicePath) =>
        _world.FindDevice(devicePath)
        ?? throw new FileNotFoundException($"Device not found at '{devicePath}'.");

    // --- mapping ---

    private static BlockInfo ToBlockInfo(FakeWorld.Block b) =>
        new(b.Name, b.Type, b.Number, b.Language, b.Comment, $"{PlcPath}/block:{b.Name}");

    private static TagInfo ToTagInfo(FakeWorld.Tag t, string tablePath) =>
        new(t.Name, t.Address, t.DataType, t.Comment, $"{tablePath}/tag:{t.Name}");

    private static ProjectInfo ToProjectInfo(FakeWorld.Project p) =>
        new(p.Name, $"{SessionRoot}/project:{p.Name}", p.Author, "Fake-0.1");

    private static string ToSimaticMl(FakeWorld.Block b)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine($"<!-- Fake SimaticML export for {b.Name} (not importable into TIA) -->");
        sb.AppendLine("<Document>");
        sb.AppendLine(
            $"  <SW.Blocks.Block Name=\"{b.Name}\" Type=\"{b.Type}\" Number=\"{b.Number}\" Language=\"{b.Language}\"/>");
        sb.AppendLine("</Document>");
        return sb.ToString();
    }
}
