using NativeObjects;

namespace ManagedDotnetGC.Api;

public class GcApi : IGc
{
    private readonly IGcInvoker _gc;

    private GcApi(IGcInvoker gc)
    {
        _gc = gc;
    }

    public static GcApi? TryCreate()
    {
        if (!GC.GetConfigurationVariables().TryGetValue("GC API", out var address))
        {
            return null;
        }

        return new GcApi(new((nint)(long)address));
    }

    public uint GetSyncBlockCacheCount() => _gc.GetSyncBlockCacheCount();

    public nint GetContainingObject(nint address) => _gc.GetContainingObject(address);

    public int IsHeapPointer(nint address) => _gc.IsHeapPointer(address);

    public int IsPromoted(nint address) => _gc.IsPromoted(address);

    public int GetGcCallbackCount(GcCallbackKind kind) => _gc.GetGcCallbackCount(kind);

    public ulong GetWriteBarrierParameter(WriteBarrierParameterKind kind) => _gc.GetWriteBarrierParameter(kind);
}
