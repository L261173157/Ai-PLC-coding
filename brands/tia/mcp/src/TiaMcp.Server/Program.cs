using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using TiaMcp.Contract;
using TiaMcp.Fake;
using TiaMcp.Openness;
using TiaMcp.Server;
using TiaMcp.Server.Safety;

// --selftest runs the backend directly (no MCP plumbing) and exits. Handy for a quick smoke.
if (ArgsContains(args, "--selftest"))
{
    await SelfTest.RunAsync(args);
    return 0;
}

// Transport: default stdio (agent launches the server as a child process). Use --transport http
// for a long-lived shared TIA session that multiple agent clients connect to at /mcp.
if (HasTransport(args, "http"))
{
    await RunHttpAsync(args);
}
else
{
    await RunStdioAsync(args);
}

return 0;

static async Task RunStdioAsync(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.ClearProviders(); // stdout is sacred under the stdio transport
    RegisterServices(builder.Services, builder.Configuration);
    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly();
    await builder.Build().RunAsync();
}

static async Task RunHttpAsync(string[] args)
{
    // WebApplication binds from --urls (e.g. --urls http://127.0.0.1:5270) and serves MCP at /mcp.
    var builder = WebApplication.CreateBuilder(args);
    builder.Logging.ClearProviders();
    RegisterServices(builder.Services, builder.Configuration);
    builder.Services
        .AddMcpServer()
        .WithHttpTransport()
        .WithToolsFromAssembly();
    var app = builder.Build();
    app.MapMcp("/mcp");
    await app.RunAsync();
}

static void RegisterServices(IServiceCollection services, IConfiguration cfg)
{
    var backend = cfg["backend"] ?? cfg["TIA_MCP_BACKEND"];
    var kind = backend?.ToLowerInvariant() switch
    {
        "openness" => TiaBackendKind.Openness,
        _ => TiaBackendKind.Fake,
    };
    services.AddSingleton<ITiaBackend>(kind switch
    {
        // openness: BridgeBackend spawns the net48 worker (the only process that can load
        // Siemens.Engineering.* on V21) and forwards ITiaBackend calls over stdin/stdout JSON-RPC.
        // workerPath points at the worker exe; defaults to a dev-layout path if unset.
        TiaBackendKind.Openness => new BridgeBackend(cfg["workerPath"] ?? cfg["TIA_MCP_WORKER"]),
        _ => new FakeBackend(),
    });

    // Structured LAD/GRAPH code seam (tia_block_read_code / write_code): net10-side only, so the
    // net48 worker's ITiaBackend implementation never sees these members. Same instance as above.
    services.AddSingleton(sp => (ITiaCodeBackend)sp.GetRequiredService<ITiaBackend>());

    // Default ReadWrite so deployed setups can create/edit blocks, tags and hardware out of the box.
    // Pass --mode ReadOnly to lock the server down (e.g. a shared read-only inspection endpoint).
    var mode = EnumText.TryParse<AccessMode>(cfg["mode"] ?? cfg["TIA_MCP_MODE"]) ?? AccessMode.ReadWrite;
    services.AddSingleton(new AccessGuard(mode));
    services.AddSingleton(new AuditLog(cfg["auditDir"]));
}

static bool HasTransport(string[] args, string value)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--transport" && i + 1 < args.Length && args[i + 1] == value)
        {
            return true;
        }

        if (args[i] == $"--transport={value}")
        {
            return true;
        }
    }

    return false;
}

static bool ArgsContains(string[] args, string value)
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
