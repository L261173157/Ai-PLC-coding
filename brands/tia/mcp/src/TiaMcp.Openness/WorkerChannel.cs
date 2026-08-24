using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Win32;
using TiaMcp.Contract;

namespace TiaMcp.Openness;

/// <summary>
/// Owns the net48 worker child process and the stdin/stdout JSON-RPC pipe. Lazily spawns the worker
/// on first use; one outstanding line exchange at a time (the bridge serializes ITiaBackend calls,
/// and the pipe itself is single-request/single-response).
/// </summary>
internal sealed class WorkerChannel : IDisposable
{
    // MUST match the worker's options (Program.cs) so the JSON round-trips into the shared types.
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _workerExe;
    private readonly SemaphoreSlim _io = new(1, 1);
    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;

    public WorkerChannel(string workerExe)
    {
        _workerExe = workerExe;
    }

    public async Task<RpcResponse> SendAsync(RpcRequest req, CancellationToken ct)
    {
        await _io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Spawn (or respawn, if the previous worker died) under the IO lock so the process
            // handle and the exchange that uses it stay consistent.
            await EnsureStartedAsync().ConfigureAwait(false);

            try
            {
                var json = JsonSerializer.Serialize(req, Json);
                await _stdin!.WriteLineAsync(json).ConfigureAwait(false);

                // Per-op watchdog: a worker stuck in a native Siemens call (e.g. Attach() blocked
                // by the Portal's Openness authorization dialog, or a modal dialog in the GUI)
                // NEVER returns on its own — without a timeout the hang poisons the whole server:
                // every later call queues on the IO lock and even other worker sessions die with
                // it. On timeout the catch below kills the worker tree so the next call respawns.
                var timeout = TimeoutFor(req.Op);
                using var watchdog = new CancellationTokenSource(timeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, watchdog.Token);
                await _stdin.FlushAsync(linked.Token).ConfigureAwait(false);

                string? line;
                try
                {
                    line = await _stdout!.ReadLineAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Openness worker did not respond to '{req.Op}' within {timeout.TotalSeconds:0}s. " +
                        "The TIA Portal is probably blocked — most often the Openness authorization dialog " +
                        "is waiting for a click (answer Yes/allow in the Portal GUI), or a modal dialog or " +
                        "project lock is holding it. The stuck worker was killed and the next call respawns " +
                        "a fresh one; any headless Portal it had spawned goes down too (unsaved changes lost). " +
                        "Unattended recovery: reconnect with mode=headless, which never shows the dialog.");
                }

                if (line is null)
                {
                    throw new IOException(
                        "Openness worker closed its output before responding (likely crashed). " +
                        "Check the worker stderr, that the Windows user is in the 'Siemens TIA Openness' group, " +
                        "and that Siemens.Engineering.* loaded from the GAC.");
                }

                return JsonSerializer.Deserialize<RpcResponse>(line, Json)
                       ?? throw new IOException("Malformed response line from worker: " + line);
            }
            catch
            {
                // Poison the channel: a broken pipe / crashed worker / cancelled read leaves the
                // process unusable. Tear it down so the NEXT call respawns a fresh worker instead
                // of reusing a half-dead one (which would hang or throw forever).
                TearDownProcess();
                throw;
            }
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>Per-op RPC deadline. The default covers normal reads/writes; slow-but-legitimate
    /// operations on real TIA get headroom (compiling/downloading a whole PLC, archiving, saving,
    /// spawning a headless Portal, generating blocks from external sources).</summary>
    private static TimeSpan TimeoutFor(string op) => op switch
    {
        RpcOp.Compile or RpcOp.Download or RpcOp.ArchiveProject => TimeSpan.FromMinutes(15),
        RpcOp.Connect or RpcOp.OpenProject or RpcOp.CreateProject or RpcOp.SaveProject
            or RpcOp.SaveProjectAs or RpcOp.GenerateBlocksFromSource or RpcOp.ImportUdt
            or RpcOp.ImportTagTable or RpcOp.ImportBlock or RpcOp.CreateBlockFromCopy
            => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromMinutes(2),
    };

    public void Dispose() => TearDownProcess();

    private void TearDownProcess()
    {
        try { _stdin?.Dispose(); } catch { /* ignore */ }
        try { _stdout?.Dispose(); } catch { /* ignore */ }
        try
        {
            if (_process is { HasExited: false })
            {
                // Kill the TREE, not just the exe: a worker that spawned a headless TIA Portal owns
                // it as a child, and an orphaned Portal (~2 GB) survives a bare Kill. Attach-mode
                // Portals (the user's GUI) are NOT children, so they are untouched.
                _process.Kill(entireProcessTree: true);
            }
        }
        catch { /* ignore */ }
        try { _process?.Dispose(); } catch { /* ignore */ }
        _process = null;
        _stdin = null;
        _stdout = null;
    }

    private async Task EnsureStartedAsync()
    {
        // Reuse only a LIVE worker. A dead _process (crash / killed / TIA took it down) must be
        // cleared and respawned — otherwise we'd forward calls into a broken pipe forever.
        if (_process is { HasExited: false })
        {
            return;
        }
        if (_process is not null)
        {
            TearDownProcess();
        }

        if (!File.Exists(_workerExe))
        {
            throw new FileNotFoundException(
                "Openness worker not found at '" + _workerExe + "'. Build it on the TIA machine:\n" +
                "  dotnet build src/TiaMcp.Openness.Worker/TiaMcp.Openness.Worker.csproj\n" +
                "Or point at it via --workerPath <exe> / TIA_MCP_WORKER.", _workerExe);
        }

        // The worker's <codeBase> must point at THIS machine's TIA net48 API dir, but the path is baked
        // in at the worker's build time and the build machine often differs from the deploy machine. We
        // can't fix it from inside the worker (the CLR reads codeBase at process start), so we do it HERE,
        // before spawning: discover the real dir from the registry and rewrite the worker's .exe.config.
        PatchWorkerCodeBase();

        var psi = new ProcessStartInfo
        {
            FileName = _workerExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_workerExe)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        // Surface worker stderr (its only non-protocol channel) on the server's stderr for debugging.
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                Console.Error.WriteLine(e.Data);
            }
        };

        if (!process.Start())
        {
            throw new IOException("Failed to start Openness worker: " + _workerExe);
        }

        process.BeginErrorReadLine();

        _process = process;
        _stdin = process.StandardInput;
        _stdout = process.StandardOutput;

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Rewrite the worker's <c>&lt;codeBase&gt;</c> hrefs to point at the local TIA net48 API dir,
    /// discovered from the registry. This makes one published bundle run on ANY machine with TIA V21,
    /// regardless of where TIA is installed or what path was baked in at the worker's build time.
    /// Best-effort and silent on failure: if discovery or the rewrite can't happen, the worker keeps
    /// its built-in config (and surfaces a clear DLL-load error itself if that path is also wrong).
    /// </summary>
    private void PatchWorkerCodeBase()
    {
        try
        {
            var net48Dir = ResolveNet48DirFromRegistry();
            if (net48Dir is null)
            {
                return; // No registry entry — trust the build-time config / the worker's own fallback.
            }

            var configPath = _workerExe + ".config"; // <exe>.config lives next to the worker exe.
            if (!File.Exists(configPath))
            {
                return;
            }

            XNamespace ns = "urn:schemas-microsoft-com:asm.v1";
            var doc = XDocument.Load(configPath);
            var changed = false;

            foreach (var dep in doc.Descendants(ns + "dependentAssembly"))
            {
                var name = dep.Element(ns + "assemblyIdentity")?.Attribute("name")?.Value;
                if (name != "Siemens.Engineering.Base" && name != "Siemens.Engineering.Step7")
                {
                    continue;
                }

                var codeBase = dep.Element(ns + "codeBase");
                if (codeBase is null)
                {
                    continue;
                }

                var href = ToFileUri(Path.Combine(net48Dir, name + ".dll"));
                if (codeBase.Attribute("href")?.Value != href)
                {
                    codeBase.SetAttributeValue("href", href);
                    changed = true;
                }
            }

            if (changed)
            {
                doc.Save(configPath);
                Console.Error.WriteLine("[bridge] patched worker codeBase -> " + net48Dir);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "[bridge] could not auto-patch worker codeBase (using its built-in config): " + ex.Message);
        }
    }

    /// <summary>
    /// The TIA installer records the exact net48 API DLL paths under
    /// <c>HKLM\SOFTWARE\Siemens\Automation\Openness\&lt;ver&gt;\PublicAPI\&lt;asmver&gt;\net48</c>
    /// (value <c>Siemens.Engineering.Base</c>). This is Siemens' own discovery mechanism, so it is the
    /// authoritative source for where TIA actually lives — no need to know it ahead of time. Returns the
    /// directory containing the net48 DLLs, or null if no V21 entry is found. Always reads the 64-bit view.
    /// </summary>
    private static string? ResolveNet48DirFromRegistry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null; // No TIA / no registry off-Windows; the worker path is Windows-only anyway.
        }

        using var openness = RegistryKey
            .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            .OpenSubKey(@"SOFTWARE\Siemens\Automation\Openness");
        if (openness is null)
        {
            return null;
        }

        // Prefer the known V21 layout; fall back to scanning version subkeys for resilience.
        foreach (var versionName in OrderedVersionKeys(openness))
        {
            using var version = openness.OpenSubKey(versionName + @"\PublicAPI");
            if (version is null)
            {
                continue;
            }

            foreach (var asmVer in version.GetSubKeyNames())
            {
                using var net48 = version.OpenSubKey(asmVer + @"\net48");
                var basePath = net48?.GetValue("Siemens.Engineering.Base") as string;
                if (!string.IsNullOrWhiteSpace(basePath) && File.Exists(basePath))
                {
                    return Path.GetDirectoryName(basePath);
                }
            }
        }

        return null;
    }

    // "21.0" first (this server is V21), then any other version keys as a fallback.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static System.Collections.Generic.IEnumerable<string> OrderedVersionKeys(RegistryKey openness)
    {
        var names = openness.GetSubKeyNames();
        foreach (var n in names)
        {
            if (n == "21.0") yield return n;
        }
        foreach (var n in names)
        {
            if (n != "21.0" && n != "AllowList") yield return n;
        }
    }

    // file:/// URI with spaces percent-encoded, matching the form codeBase expects (e.g. Program%20Files).
    private static string ToFileUri(string path) =>
        "file:///" + path.Replace('\\', '/').Replace(" ", "%20");
}
