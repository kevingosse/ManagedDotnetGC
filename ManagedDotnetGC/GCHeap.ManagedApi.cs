using ManagedDotnetGC.Api;
using static ManagedDotnetGC.Log;

namespace ManagedDotnetGC;

partial class GCHeap : IGc
{
    private NativeObjects.IGc _managedApiObject;

    private void InitializeManagedApi()
    {
        _managedApiObject = NativeObjects.IGc.Wrap(this);
    }

    public uint GetSyncBlockCacheCount()
    {
        _gcToClr.SuspendEE(SUSPEND_REASON.SUSPEND_FOR_GC_PREP);

        var count = _gcToClr.GetActiveSyncBlockCount();

        _gcToClr.RestartEE(finishedGC: false);

        return count;
    }
}
