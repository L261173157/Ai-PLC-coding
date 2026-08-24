using System.Globalization;
using System.Text.Json;

namespace TiaMcp.Server.Safety;

/// <summary>
/// Append-only JSONL audit trail of every mutation the guard allowed through (and any that
/// failed at the backend). Auditing never throws — a log failure must not break a tool call.
/// </summary>
public sealed class AuditLog
{
    private readonly string _dir;

    public AuditLog(string? dir = null) =>
        _dir = dir ?? Path.Combine(Path.GetTempPath(), "tiamcp-audit");

    public string DirectoryPath => _dir;

    public void Append(string operation, string scope, bool success, string? error = null, string? details = null)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var file = Path.Combine(_dir, DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
            var entry = new
            {
                t = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                op = operation,
                scope,
                success,
                error,
                details,
            };
            File.AppendAllText(file, JsonSerializer.Serialize(entry) + "\n");
        }
        catch
        {
            // Intentionally swallowed.
        }
    }
}
