using TiaMcp.Contract;

namespace TiaMcp.Fake;

/// <summary>
/// An in-memory virtual TIA project tree that the Fake backend resolves paths against and
/// mutates. One session -> one project -> one PLC device -> one PLC ("program") with blocks
/// and a default tag table.
/// </summary>
internal sealed class FakeWorld
{
    public sealed class Block
    {
        public string Name = "";
        public BlockType Type;
        public int Number;
        public string Language = "SCL";
        public string? Comment;
        public string Source = "";
    }

    public sealed class Plc
    {
        public string Name = "program";
        public List<Block> Blocks = new();
        public List<Tag> Tags = new();
    }

    public sealed class Tag
    {
        public string Name = "";
        public string? Address;
        public string DataType = "Bool";
        public string? Comment;
    }

    public sealed class Item
    {
        public string Name = "";
        public string? TypeIdentifier;
        public int Slot;
        public string? TypeName;
    }

    public sealed class Device
    {
        public string Name = "";
        public TargetKind Kind;
        public Plc? Plc;
        public List<Item> Items = new();
        public bool Online;
        public string PlcState = "Unknown";
    }

    public sealed class Project
    {
        public string Name = "";
        public string Author = "TiaMcp";
        public List<Device> Devices = new();
        public List<string> Subnets = new();
        public List<string> IoSystems = new();
    }

    public sealed class Session
    {
        public string Id = "";
        public List<Project> Projects = new();
    }

    public Session Root { get; }

    public FakeWorld(string sessionId, string projectName)
    {
        Root = new Session { Id = sessionId };

        var plc = new Plc
        {
            Blocks =
            {
                new Block
                {
                    Name = "FB_Motor", Type = BlockType.FB, Number = 1,
                    Comment = "Motor start / stop / overload control",
                    Source = MotorSource,
                },
                new Block
                {
                    Name = "FC_Stop", Type = BlockType.FC, Number = 10,
                    Comment = "Safe stop sequence",
                    Source = StopSource,
                },
                new Block
                {
                    Name = "FC_MotorLAD", Type = BlockType.FC, Number = 11, Language = "LAD",
                    Comment = "Motor start-hold-stop (启保停), one LAD rung",
                    Source = MotorLadSource,
                },
                new Block
                {
                    Name = "FB_GraphDemo", Type = BlockType.FB, Number = 2, Language = "GRAPH",
                    Comment = "GRAPH demo sequence (3 steps), machine-verified shape",
                    Source = GraphDemoSource(),
                },
                new Block
                {
                    Name = "DB_Motor", Type = BlockType.DB, Number = 1, Language = "DB",
                    Comment = "Instance DB for FB_Motor",
                    Source = DbSource,
                },
                new Block
                {
                    Name = "UDT_MotorParams", Type = BlockType.UDT, Number = 0, Language = "UDT",
                    Comment = "Motor parameter struct",
                    Source = UdtSource,
                },
            },
            Tags =
            {
                new Tag { Name = "Start", Address = "%I0.0", DataType = "Bool", Comment = "Start pushbutton" },
                new Tag { Name = "Stop", Address = "%I0.1", DataType = "Bool", Comment = "Stop pushbutton" },
                new Tag { Name = "Running", Address = "%Q0.0", DataType = "Bool", Comment = "Motor running feedback" },
            },
        };

        var device = new Device
        {
            Name = "PLC_1",
            Kind = TargetKind.Plc,
            Plc = plc,
            Items =
            {
                new Item { Name = "Rack", TypeIdentifier = "System.Rack", Slot = -1, TypeName = "Rack" },
                new Item { Name = "CPU", TypeIdentifier = "6ES7 510-1SK00-0AB0", Slot = 1, TypeName = "CPU 1510C" },
                new Item { Name = "DI", TypeIdentifier = "6ES7 521-1BL00-0AB0", Slot = 2, TypeName = "DI 16x24VDC HF" },
                new Item { Name = "DQ", TypeIdentifier = "6ES7 522-1BL00-0AB0", Slot = 3, TypeName = "DQ 16x24VDC HF" },
            },
        };
        Root.Projects.Add(new Project { Name = projectName, Devices = { device } });
    }

    // --- lookups ---

    public Project? FindProject(string path)
    {
        var name = Segment(path, "project");
        return string.IsNullOrEmpty(name)
            ? Root.Projects.FirstOrDefault()
            : Root.Projects.FirstOrDefault(p => Eq(p.Name, name));
    }

    public Device? FindDevice(string path)
    {
        var proj = FindProject(path);
        if (proj is null)
        {
            return null;
        }

        var name = Segment(path, "device");
        return string.IsNullOrEmpty(name)
            ? proj.Devices.FirstOrDefault()
            : proj.Devices.FirstOrDefault(d => Eq(d.Name, name));
    }

    /// <summary>Resolve a scope path down to a PLC. Auto-descends via "first child" for missing levels.</summary>
    public Plc? FindPlc(string scopePath)
    {
        var proj = FindProject(scopePath);
        if (proj is null)
        {
            return null;
        }

        var devName = Segment(scopePath, "device");
        var dev = string.IsNullOrEmpty(devName)
            ? proj.Devices.FirstOrDefault()
            : proj.Devices.FirstOrDefault(d => Eq(d.Name, devName));

        return dev?.Plc ?? proj.Devices.FirstOrDefault(d => d.Plc is not null)?.Plc;
    }

    public Block? FindBlock(string blockPath)
    {
        var blockName = Segment(blockPath, "block");
        if (string.IsNullOrEmpty(blockName))
        {
            return null;
        }

        return FindPlc(blockPath)?.Blocks.FirstOrDefault(b => Eq(b.Name, blockName));
    }

    public Tag? FindTag(string tagPath)
    {
        var name = Segment(tagPath, "tag");
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        return FindPlc(tagPath)?.Tags.FirstOrDefault(t => Eq(t.Name, name));
    }

    // --- mutations (return false on missing target / name conflict; never throw) ---

    public bool AddBlock(string plcPath, Block b)
    {
        var plc = FindPlc(plcPath);
        if (plc is null || plc.Blocks.Any(x => Eq(x.Name, b.Name)))
        {
            return false;
        }

        b.Number = NextBlockNumber(plc, b.Type);
        plc.Blocks.Add(b);
        return true;
    }

    public bool RemoveBlock(string blockPath)
    {
        var plc = FindPlc(blockPath);
        if (plc is null)
        {
            return false;
        }

        var name = Segment(blockPath, "block");
        var b = plc.Blocks.FirstOrDefault(x => Eq(x.Name, name));
        return b is not null && plc.Blocks.Remove(b);
    }

    public IReadOnlyList<Tag> GetTags(string plcPath)
    {
        var plc = FindPlc(plcPath);
        return plc is null ? Array.Empty<Tag>() : plc.Tags;
    }

    public bool AddTag(string plcPath, Tag t)
    {
        var plc = FindPlc(plcPath);
        if (plc is null || plc.Tags.Any(x => Eq(x.Name, t.Name)))
        {
            return false;
        }

        plc.Tags.Add(t);
        return true;
    }

    public bool RemoveTag(string tagPath)
    {
        var plc = FindPlc(tagPath);
        if (plc is null)
        {
            return false;
        }

        var name = Segment(tagPath, "tag");
        var t = plc.Tags.FirstOrDefault(x => Eq(x.Name, name));
        return t is not null && plc.Tags.Remove(t);
    }

    private static int NextBlockNumber(Plc plc, BlockType type)
    {
        var max = plc.Blocks.Where(b => b.Type == type).Select(b => b.Number).DefaultIfEmpty(0).Max();
        return max == 0 && type != BlockType.UDT ? 1 : max + 1;
    }

    private static string? Segment(string path, string kind)
    {
        foreach (var seg in TiaPathParser.Parse(path))
        {
            if (Eq(seg.Kind, kind))
            {
                return seg.Name;
            }
        }

        return null;
    }

    private static bool Eq(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Seeds the GRAPH demo block by running the real spec generator — the same
    /// machine-verified shape tia_block_write_code emits (imports + compiles 0 errors on real
    /// TIA V21), so the offline smoke exercises the actual GRAPH parse path end-to-end.</summary>
    private static string GraphDemoSource() =>
        SimaticML.Generator.LadSpecGenerator.Generate(new Contract.CodeBlockSpec(
            "FB_GraphDemo", "FB", "GRAPH",
            "GRAPH demo: 3 steps, N actions, contact transition conditions",
            new[]
            {
                new Contract.SpecSection("Input", new[]
                {
                    new Contract.SpecMember("Cond1", "Bool", "转移条件1", null),
                    new Contract.SpecMember("Cond2", "Bool", "转移条件2", null),
                }),
                new Contract.SpecSection("Output", new[]
                {
                    new Contract.SpecMember("Out1", "Bool", "步1输出", null),
                    new Contract.SpecMember("Out2", "Bool", "步2输出", null),
                    new Contract.SpecMember("Out3", "Bool", "步3输出", null),
                }),
            },
            Networks: null,
            Sequence: new[]
            {
                new Contract.GraphStepSpec("Init", new[] { new Contract.GraphActionSpec("N", "Out1") }, "Cond1"),
                new Contract.GraphStepSpec("Step2", new[] { new Contract.GraphActionSpec("N", "Out2") }, "Cond2"),
                new Contract.GraphStepSpec("Step3", new[] { new Contract.GraphActionSpec("N", "Out3") }, "Cond1"),
            }));

    private const string MotorSource = """
FUNCTION_BLOCK "FB_Motor"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1

   VAR_INPUT
      Cmd_Start : Bool;
      Cmd_Stop  : Bool;
      Overload  : Bool;
   END_VAR
   VAR_OUTPUT
      Running : Bool;
      Fault   : Bool;
   END_VAR

BEGIN
    // Motor start / stop / overload control
    #Running := #Cmd_Start AND NOT (#Cmd_Stop OR #Overload);
    #Fault   := #Overload;
END_FUNCTION_BLOCK
""";

    private const string StopSource = """
FUNCTION "FC_Stop" : Void
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
   VAR_INPUT
      Trigger : Bool;
   END_VAR
BEGIN
    // Safe stop sequence (placeholder)
    ;
END_FUNCTION
""";

    // Real, import-verified FlgNet for the classic start-hold-stop rung (启保停):
    //   Motor := (Start_PB OR Motor) AND NOT Stop_PB
    // Verbatim from brands/tia/skills/write-program/examples/启保停LAD范例.md ("导入 + 编译 0 错误").
    // Seeding the Fake with real SimaticML (not a stub) lets the offline smoke exercise the actual
    // FlgNet parser end-to-end: tia_block_read_code must render it as the expression above.
    private const string MotorLadSource = """
<?xml version="1.0" encoding="utf-8"?>
<Document>
  <Engineering version="V21" />
  <SW.Blocks.FC ID="0">
    <AttributeList>
      <AutoNumber>true</AutoNumber>
      <HeaderVersion>0.1</HeaderVersion>
      <Interface>
        <Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
          <Section Name="Input" />
          <Section Name="Output" />
          <Section Name="InOut" />
          <Section Name="Temp" />
          <Section Name="Constant" />
          <Section Name="Return">
            <Member Name="Ret_Val" Datatype="Void" />
          </Section>
        </Sections>
      </Interface>
      <MemoryLayout>Optimized</MemoryLayout>
      <Name>FC_MotorLAD</Name>
      <Namespace />
      <Number>0</Number>
      <ProgrammingLanguage>LAD</ProgrammingLanguage>
    </AttributeList>
    <ObjectList>
      <SW.Blocks.CompileUnit ID="1" CompositionName="CompileUnits">
        <AttributeList>
          <NetworkSource>
            <FlgNet xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5">
              <Parts>
                <Access Scope="GlobalVariable" UId="21"><Symbol><Component Name="Start_PB" /></Symbol></Access>
                <Access Scope="GlobalVariable" UId="22"><Symbol><Component Name="Motor" /></Symbol></Access>
                <Access Scope="GlobalVariable" UId="23"><Symbol><Component Name="Stop_PB" /></Symbol></Access>
                <Access Scope="GlobalVariable" UId="24"><Symbol><Component Name="Motor" /></Symbol></Access>
                <Part Name="Contact" UId="25" />
                <Part Name="Contact" UId="26" />
                <Part Name="O" UId="27">
                  <TemplateValue Name="Card" Type="Cardinality">2</TemplateValue>
                </Part>
                <Part Name="Contact" UId="28"><Negated Name="operand" /></Part>
                <Part Name="Coil" UId="29" />
              </Parts>
              <Wires>
                <Wire UId="30"><IdentCon UId="21" /><NameCon UId="25" Name="operand" /></Wire>
                <Wire UId="31"><IdentCon UId="22" /><NameCon UId="26" Name="operand" /></Wire>
                <Wire UId="32"><IdentCon UId="23" /><NameCon UId="28" Name="operand" /></Wire>
                <Wire UId="33"><IdentCon UId="24" /><NameCon UId="29" Name="operand" /></Wire>
                <Wire UId="34"><Powerrail /><NameCon UId="25" Name="in" /><NameCon UId="26" Name="in" /></Wire>
                <Wire UId="35"><NameCon UId="25" Name="out" /><NameCon UId="27" Name="in1" /></Wire>
                <Wire UId="36"><NameCon UId="26" Name="out" /><NameCon UId="27" Name="in2" /></Wire>
                <Wire UId="37"><NameCon UId="27" Name="out" /><NameCon UId="28" Name="in" /></Wire>
                <Wire UId="38"><NameCon UId="28" Name="out" /><NameCon UId="29" Name="in" /></Wire>
              </Wires>
            </FlgNet>
          </NetworkSource>
          <ProgrammingLanguage>LAD</ProgrammingLanguage>
        </AttributeList>
      </SW.Blocks.CompileUnit>
    </ObjectList>
  </SW.Blocks.FC>
</Document>
""";

    private const string DbSource = """
DATA_BLOCK "DB_Motor"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
"FB_Motor"

BEGIN
END_DATA_BLOCK
""";

    private const string UdtSource = """
TYPE "UDT_MotorParams"
VERSION : 0.1
STRUCT
   RatedCurrent : Real;
   PoleCount    : Int;
END_STRUCT
END_TYPE
""";
}
