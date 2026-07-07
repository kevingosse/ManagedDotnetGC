using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace ManagedDotnetGC;

/// <summary>
/// Bulk zeroing with non-temporal stores (M7 census). Span.Clear's temporal stores
/// measured ~10.8 GB/s on the census run and RFO-read every stale line into cache just
/// to overwrite it — 61% of the mutator-felt window handout (wzero 1140 of win 1878 ms
/// per soh run) plus the background zeroer's whole regions. NT stores skip the
/// ownership read and the cache pollution; the WC-buffer drain (sfence) before
/// returning keeps the zeros ordered ahead of the caller's header writes and
/// publication, so no other thread can observe stale bytes through a published object.
/// </summary>
internal static unsafe class Zeroing
{
    // Below this, Span.Clear wins: alignment edges + sfence dominate, and a small
    // extent is re-written immediately (block carves) so cache-warm zeros pay off
    private const nint NonTemporalThreshold = 4 * 1024;

    private static readonly bool HwSupported = Avx.IsSupported;
    private static readonly bool HwSupported512 = Vector512.IsHardwareAccelerated && Avx512F.IsSupported;

    /// <summary>DOTNET_GCNtZero=0 opts out (bisect insurance: zeroing correctness is
    /// type safety, and NT-store bugs would smear like the card-lookback incident).</summary>
    public static bool Enabled = true;

    /// <summary>DOTNET_GCCarveTemporal=1: mutator-inline carve zeroing uses temporal
    /// stores. NT stores invalidate the very lines the mutator is about to write —
    /// temporal zeroing of an imminently-allocated window doubles as a prefetch (the
    /// stock GC's adjust_limit_clr model). Background zeroing stays non-temporal
    /// either way. (M8.3 web-workload profile.)</summary>
    public static bool CarveTemporal = false;

    /// <summary>Zeroing for memory the calling thread is about to allocate from.</summary>
    public static void ClearCarve(nint start, nint length)
    {
        if (CarveTemporal)
        {
            ClearTemporal(start, length);
            return;
        }

        Clear(start, length);
    }

    public static void Clear(nint start, nint length)
    {
        if (length < NonTemporalThreshold || !HwSupported || !Enabled)
        {
            ClearTemporal(start, length);
            return;
        }

        // Temporal edges up to the 64-byte boundaries; ≥ 4 KB guarantees a body
        var alignedStart = (start + 63) & ~(nint)63;
        var alignedEnd = (start + length) & ~(nint)63;

        ClearTemporal(start, alignedStart - start);
        ClearTemporal(alignedEnd, start + length - alignedEnd);

        if (HwSupported512)
        {
            var zero = Vector512<byte>.Zero;

            for (var p = alignedStart; p < alignedEnd; p += 64)
            {
                Avx512F.StoreAlignedNonTemporal((byte*)p, zero);
            }
        }
        else
        {
            var zero = Vector256<byte>.Zero;

            for (var p = alignedStart; p < alignedEnd; p += 64)
            {
                Avx.StoreAlignedNonTemporal((byte*)p, zero);
                Avx.StoreAlignedNonTemporal((byte*)(p + 32), zero);
            }
        }

        // NT stores are weakly ordered: drain the write-combining buffers before any
        // subsequent store (object header, publishing write) can become visible
        Sse.StoreFence();
    }

    private static void ClearTemporal(nint start, nint length)
    {
        while (length > 0)
        {
            var chunk = (int)Math.Min(length, int.MaxValue & ~7);
            new Span<byte>((void*)start, chunk).Clear();
            start += chunk;
            length -= chunk;
        }
    }
}
