using TiaMcp.Contract;
using TiaMcp.Fake;
using TiaMcp.Openness;

namespace TiaMcp.Server;

/// <summary>
/// Offline smoke harness: drives a backend directly (no MCP, no guard) so the pipeline can be
/// verified in one command, even on a machine without TIA. Invoke with <c>--selftest</c>.
/// </summary>
internal static class SelfTest
{
    public static async Task RunAsync(string[] args)
    {
        // The self-test drives a backend directly with no MCP / no guard, so it only makes sense for
        // the in-memory Fake backend. The Openness backend now spawns a net48 worker that needs real
        // TIA V21 + a project; exercise it through the MCP protocol (mcp/tests/smoke_mcp.py / an agent) instead.
        var useOpenness = ContainsArg(args, "--backend=openness") || ContainsArg(args, "openness");
        if (useOpenness)
        {
            Console.WriteLine(
                "self-test runs offline against the Fake backend only. The Openness backend is driven " +
                "via the MCP protocol (it spawns a net48 worker that needs TIA V21). Use: " +
                "dotnet run --project mcp/src/TiaMcp.Server -- --backend openness, then drive it with an agent " +
                "or mcp/tests/smoke_mcp.py. Running the Fake self-test now.");
        }

        // FakeBackend implements both seams (ITiaBackend + the net10-only ITiaCodeBackend), so the
        // self-test can exercise the structured code read on the same instance.
        var backend = new FakeBackend();

        Console.WriteLine($"== TiaMcp self-test (backend={backend.Kind}, tia={backend.TiaVersion}) ==");

        var status = await backend.GetStatusAsync(CancellationToken.None);
        Console.WriteLine(
            $"status   : backend={status.Backend} tia={status.TiaVersion} " +
            $"available={status.TiaAvailable} mode={status.AccessMode}");

        SessionInfo session;
        try
        {
            session = await backend.ConnectAsync(new ConnectRequest("headless"), CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"connect   : FAILED ({ex.GetType().Name}) — backend not wired. Use --backend fake.");
            return;
        }

        Console.WriteLine(
            $"connect   : session={session.SessionId} root={session.Path} " +
            $"projects=[{string.Join(", ", session.Projects)}]");

        // --- P1 read flow ---
        var proj = await backend.OpenProjectAsync(
            session.Path, new OpenProjectRequest("Demo", Visible: false), CancellationToken.None);
        Console.WriteLine($"open      : {proj.Name} @ {proj.Path}");

        var targets = await backend.ListTargetsAsync(proj.Path, CancellationToken.None);
        foreach (var t in targets)
        {
            Console.WriteLine($"  target   : [{t.Kind}] {t.Name} ({t.TypeName}) -> {t.Path}");
        }

        var blocks = await backend.ListBlocksAsync(session.Path, null, 100, 0, CancellationToken.None);
        Console.WriteLine($"blocks    : total={blocks.Total} @ {blocks.ScopePath}");

        var fbPath = $"{blocks.ScopePath}/block:FB_Motor";
        var src = await backend.ReadBlockSourceAsync(fbPath, CancellationToken.None);
        Console.WriteLine($"source    : {src.Language}, {src.Source.Length} chars (FB_Motor)");

        // --- #7: structured body read (LAD expression rendering + SCL passthrough) ---
        var ladCode = await backend.ReadBlockCodeAsync(
            $"{blocks.ScopePath}/block:FC_MotorLAD", new ReadCodeOptions(null, null, true), CancellationToken.None);
        var ladRung = ladCode.Networks.FirstOrDefault();
        Console.WriteLine($"read_code : {ladCode.Name} lang={ladCode.Language} networks={ladCode.Networks.Count} render='{ladRung?.Render}'");
        if (ladCode.Language != "LAD" || ladCode.Networks.Count != 1 ||
            ladRung?.Render != "(Start_PB OR Motor) AND NOT Stop_PB = ( ) Motor")
        {
            throw new Exception(
                $"self-test: FC_MotorLAD did not render as the start-hold-stop expression (got " +
                $"'{ladRung?.Render}', lang={ladCode.Language}, networks={ladCode.Networks.Count}).");
        }

        var sclCode = await backend.ReadBlockCodeAsync(fbPath, new ReadCodeOptions(null, null, false), CancellationToken.None);
        Console.WriteLine($"read_code : {sclCode.Name} kind={sclCode.Networks.FirstOrDefault()?.Kind} text-chars={sclCode.Networks.FirstOrDefault()?.Text?.Length}");
        if (sclCode.Networks.FirstOrDefault()?.Text?.Contains("#Running :=") != true)
        {
            throw new Exception("self-test: FB_Motor SCL body did not pass through read_code.");
        }

        var compile = await backend.CompileAsync(session.Path, CompileMode.Software, CancellationToken.None);
        Console.WriteLine($"compile   : success={compile.Success} errors={compile.Errors} warnings={compile.Warnings}");

        // --- P2 write flow (backend mechanics; the guard is bypassed in the self-test) ---
        Console.WriteLine("--- writes ---");
        var valve = new CreateBlockRequest(blocks.ScopePath, "FB_Valve", BlockType.FB, ValveSource, "SCL");
        var valvePath = await backend.ImportBlockAsync(blocks.ScopePath, valve, CancellationToken.None);
        Console.WriteLine($"import    : {valvePath}");

        var afterImport = await backend.ListBlocksAsync(session.Path, null, 100, 0, CancellationToken.None);
        Console.WriteLine($"blocks now: total={afterImport.Total}");

        await backend.DeleteBlockAsync(valvePath, CancellationToken.None);
        var afterDelete = await backend.ListBlocksAsync(session.Path, null, 100, 0, CancellationToken.None);
        Console.WriteLine($"delete    : total back to {afterDelete.Total}");

        var tagTable = $"{blocks.ScopePath}/tagtable:Default";
        var tagReq = new CreateTagRequest(tagTable, "Valve_Open", "%Q0.5", "Bool", "Valve open command");
        var tagPath = await backend.CreateTagAsync(tagTable, tagReq, CancellationToken.None);
        Console.WriteLine($"tag create: {tagPath}");

        var tags = await backend.ListTagsAsync(session.Path, 50, 0, CancellationToken.None);
        Console.WriteLine($"tags      : total={tags.Total} @ {tags.TagTablePath}");

        await backend.DeleteTagAsync(tagPath, CancellationToken.None);
        var tagsAfter = await backend.ListTagsAsync(session.Path, 50, 0, CancellationToken.None);
        Console.WriteLine($"tag delete: total back to {tagsAfter.Total}");

        Console.WriteLine("\nOK");
    }

    private const string ValveSource = """
FUNCTION_BLOCK "FB_Valve"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
   VAR_INPUT
      Open : Bool;
   END_VAR
   VAR_OUTPUT
      Out : Bool;
   END_VAR
BEGIN
    #Out := #Open;
END_FUNCTION_BLOCK
""";

    private static bool ContainsArg(string[] args, string value)
    {
        foreach (var a in args)
        {
            if (a == value)
            {
                return true;
            }
        }

        return false;
    }
}
