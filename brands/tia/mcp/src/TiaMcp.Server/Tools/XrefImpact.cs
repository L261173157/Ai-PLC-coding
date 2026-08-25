using TiaMcp.Contract;

namespace TiaMcp.Server.Tools;

/// <summary>
/// Shared extraction of deletion-impact facts from a <see cref="CrossRefResult"/>: what a delete
/// breaks (callers / instance DBs / reading-writing blocks). Used by the block and tag delete
/// previews so a destructive change never reports blind.
/// </summary>
internal static class XrefImpact
{
    /// <summary>Direct dependents: callers (UsedBy/*) and instance DBs (TypeInstance/*), one line
    /// per referencing object with per-referenceType/access counts.</summary>
    public static List<string> ExtractDependents(CrossRefResult xref) =>
        xref.References
            .Select(e => (Entry: e, Kinds: e.Locations
                .Where(l => l.ReferenceType is "UsedBy" or "TypeInstance")
                .GroupBy(l => $"{l.ReferenceType}/{l.Access}")
                .Select(g => $"{g.Key} x{g.Count()}")))
            .Where(t => t.Kinds.Any())
            .Select(t => $"{t.Entry.Name} ({t.Entry.TypeName ?? "block"}): {string.Join(", ", t.Kinds)}")
            .ToList();

    /// <summary>Fetch a path's dependents, degrading to empty on a flaky cross-reference service —
    /// but NOT on a missing target: a nonexistent block/tag must never preview as "safe to delete"
    /// (live-verified 2026-08-25: deleting a nonexistent FB previewed as AwaitingConfirmation with
    /// "No dependents found"). Not-found errors propagate to the caller.</summary>
    public static async Task<List<string>> DependentsOfAsync(ITiaBackend backend, string path, CancellationToken ct)
    {
        try
        {
            return ExtractDependents(await backend.GetCrossReferencesAsync(path, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (!IsNotFound(ex))
        {
            return new List<string>();
        }
    }

    /// <summary>True when the exception means the target (or its PLC/project) does not exist. Worker
    /// errors arrive with the type embedded in the message ("Openness worker error
    /// (System.IO.FileNotFoundException): block not found at ..."), the Fake throws
    /// FileNotFoundException directly — match both shapes.</summary>
    internal static bool IsNotFound(Exception ex) =>
        ex is FileNotFoundException
        || (ex.Message ?? string.Empty).Contains("FileNotFoundException", StringComparison.Ordinal)
        || (ex.Message ?? string.Empty).Contains("not found", StringComparison.OrdinalIgnoreCase);

    /// <summary>One-line impact summary appended to a preview plan / applied message; the structured
    /// detail rides in <see cref="MutationResult.Dependents"/>. <paramref name="usedTypes"/> (block
    /// deletes only) names the user PLC data types the object declares with, which may orphan.</summary>
    public static string DependentsSuffix(List<string> dependents, List<string>? usedTypes, bool orphansNow)
    {
        var s = dependents.Count == 0
            ? " No dependents found (nothing calls it, no instance DBs) — safe to delete."
            : $" {dependents.Count} dependent(s) {(orphansNow ? "now broken/orphaned" : "will break (compile errors) or be orphaned")}: {string.Join("; ", dependents)}.";
        if (usedTypes is { Count: > 0 })
        {
            s += $" Types it uses (may orphan {(orphansNow ? "them" : "them too")}): {string.Join(", ", usedTypes)}.";
        }
        return s;
    }
}
