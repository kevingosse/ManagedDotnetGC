namespace ManagedDotnetGC;

unsafe partial class GCHeap
{
    private uint _gcCount;
    private bool _gcInProgress;
    private nint _lastGCStartTime;
    private nint _lastGCDuration;
    private long _totalPauseDuration;

    public nint GetLOHThreshold() => nint.MaxValue;

    public uint GetGcCount() => _gcCount;

    public void SetGCInProgress(bool inProgress)
    {
        if (inProgress)
        {
            _lastGCStartTime = GetNow();
        }
        else
        {
            _lastGCDuration = GetNow() - _lastGCStartTime;
            _totalPauseDuration += _lastGCDuration;
        }

        _gcInProgress = inProgress;
    }

    public uint GetMaxGeneration() => 0;

    public int CollectionCount(int generation, int get_bgc_fgc_coutn) => (int)_gcCount;

    public bool IsGCInProgressHelper(bool bConsiderGCStart = false) => _gcInProgress;

    public nint GetCurrentObjSize() => 0;

    public nint GetLastGCStartTime(int generation) => _lastGCStartTime;

    public nint GetLastGCDuration(int generation) => _lastGCDuration;

    public nint GetNow() => IntPtr.Size == 4 ? Environment.TickCount : (nint)Environment.TickCount64;

    public long GetTotalPauseDuration() => _totalPauseDuration;

    public nint GetTotalBytesInUse() => 0;

    public ulong GetTotalAllocatedBytes() => 0;

    public int GetLastGCPercentTimeInGC() => 0;

    public nint GetLastGCGenerationSize(int gen) => 0;

    public uint GetMemoryLoad() => 0;

    public bool IsConcurrentGCEnabled() => false;

    public bool IsConcurrentGCInProgress() => false;

    public void EnumerateConfigurationValues(void* context, nint configurationValueFunc)
    {
        // void (*)(void* context, void* name, void* publicKey, GCConfigurationType type, int64_t data);
        var callback = (delegate* unmanaged<void*, void*, void*, GCConfigurationType, long, void>)configurationValueFunc;

        fixed (byte* name = "FreeObjectMethodTable"u8)
        fixed (byte* publicKey = "internal"u8)
        {
            callback(context, name, publicKey, GCConfigurationType.Int64, (long)_freeObjectMethodTable);
        }
    }

    private enum GCConfigurationType
    {
        Int64,
        StringUtf8,
        Boolean
    }
}
