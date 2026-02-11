using ManagedDotnetGC.Api;
using static ManagedDotnetGC.Log;

namespace ManagedDotnetGC;

internal class ManagedApi : IGc
{
    private readonly NativeObjects.IGc _nativeObject;
    private readonly GCHeap _gcHeap;

    public ManagedApi(GCHeap gcHeap)
    {
        _nativeObject = NativeObjects.IGc.Wrap(this);
        _gcHeap = gcHeap;
    }

    public IntPtr IGcObject => _nativeObject;

    public void Test()
    {
        Write("============= API initialization successful ===============");
    }
}
