// Polyfill. Records and init-only setters require the marker type
// System.Runtime.CompilerServices.IsExternalInit, which only ships on .NET 5+. Defining it here
// lets the record DTOs in this assembly compile unchanged on netstandard2.0 (consumed by the net48
// Openness worker). Excluded on modern TFMs where the type already exists, so net10.0 is unaffected.
#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
