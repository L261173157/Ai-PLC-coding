namespace TiaMcp.Contract;

/// <summary>
/// Structured block-BODY read/write seam for <c>tia_block_read_code</c> / <c>tia_block_write_code</c>.
/// Deliberately SEPARATE from <see cref="ITiaBackend"/>: the net48 worker's OpennessEngine also
/// implements ITiaBackend, and these members live net10-side only (pure SimaticML parsing/generation
/// in TiaMcp.SimaticML, composed onto the existing ReadBlockSource / ImportBlock RPCs in the bridge).
/// Putting them on ITiaBackend would break the worker build with unimplementable members.
/// Implementations: <c>TiaMcp.Fake.FakeBackend</c> and <c>TiaMcp.Openness.BridgeBackend</c>.
/// </summary>
public interface ITiaCodeBackend
{
    /// <summary>
    /// Read a block's BODY as a compact structured view (per-network boolean expressions / boxes /
    /// flattened SCL text, or a GRAPH step list) instead of raw SimaticML XML. Under the hood this
    /// reuses the block-source export (with its compile-recovery) and parses it net10-side — the
    /// net48 worker is not involved beyond the existing ReadBlockSource RPC.
    /// </summary>
    Task<BlockCode> ReadBlockCodeAsync(string blockPath, ReadCodeOptions options, CancellationToken ct);

    /// <summary>
    /// Compile a structured <see cref="CodeBlockSpec"/> into SimaticML XML and import it (Override,
    /// idempotent — same semantics as block import on the real backend). Returns the new/updated
    /// block path. Generation is deterministic net10-side work; only the import itself goes through
    /// the existing ImportBlock RPC, so the net48 worker is untouched.
    /// </summary>
    Task<string> WriteBlockCodeAsync(string plcPath, CodeBlockSpec spec, CancellationToken ct);

    /// <summary>
    /// Compile a spec to SimaticML XML WITHOUT importing it (the dryRun half of
    /// <c>tia_block_write_code</c>). Behind the seam because the tool layer depends on Contract only;
    /// both backends implement it as the same deterministic net10-side generator call — no TIA, no RPC.
    /// </summary>
    Task<string> GenerateBlockCodeAsync(CodeBlockSpec spec, CancellationToken ct);
}
