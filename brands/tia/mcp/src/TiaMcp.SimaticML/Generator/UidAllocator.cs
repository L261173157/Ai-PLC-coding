namespace TiaMcp.SimaticML.Generator;

/// <summary>Per-network monotonic UId allocator. TIA normalizes UIds on import — only per-network
/// uniqueness matters (verified note in _reference/simaticml-reference.md), so a simple counter works.</summary>
internal sealed class UidAllocator
{
    private long _next = 21;

    public long Next() => _next++;
}
