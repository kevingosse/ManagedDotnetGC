using ManagedDotnetGC.Interfaces;
using static ManagedDotnetGC.Log;

namespace ManagedDotnetGC;

internal unsafe class GCHandleManager : IGCHandleManager
{
    private readonly NativeObjects.IGCHandleManager _nativeObject;
    private readonly GCHandleStore _gcHandleStore;

    public GCHandleManager()
    {
        _gcHandleStore = new GCHandleStore();
        _nativeObject = NativeObjects.IGCHandleManager.Wrap(this);
    }

    public IntPtr IGCHandleManagerObject => _nativeObject;

    public GCHandleStore Store => _gcHandleStore;

    public bool Initialize()
    {
        return true;
    }

    public void Shutdown()
    {
    }

    public IntPtr GetGlobalHandleStore()
    {
        return _gcHandleStore.IGCHandleStoreObject;
    }

    public IntPtr CreateHandleStore()
    {
        Write("GCHandleManager CreateHandleStore");
        return default;
    }

    public void DestroyHandleStore(IntPtr store)
    {
        Write("GCHandleManager DestroyHandleStore");
    }

    public ref ObjectHandle CreateGlobalHandleOfType(GCObject* obj, HandleType type)
    {
        return ref _gcHandleStore.CreateHandleOfType(obj, type);
    }

    public ref ObjectHandle CreateDuplicateHandle(ref ObjectHandle handle)
    {
        ref var newHandle = ref _gcHandleStore.CreateHandleOfType((GCObject*)handle.Object, handle.Type);
        newHandle.ExtraInfo = handle.ExtraInfo;
        return ref newHandle;
    }

    public void DestroyHandleOfType(ref ObjectHandle handle, HandleType type)
    {
        Write("GCHandleManager DestroyHandleOfType");
    }

    public void DestroyHandleOfUnknownType(ref ObjectHandle handle)
    {
        Write("GCHandleManager DestroyHandleOfUnknownType");
    }

    public void SetExtraInfoForHandle(ref ObjectHandle handle, HandleType type, nint extraInfo)
    {
        handle.ExtraInfo = extraInfo;
    }

    public nint GetExtraInfoFromHandle(ref ObjectHandle handle)
    {
        return handle.ExtraInfo;
    }

    public void StoreObjectInHandle(ref ObjectHandle handle, GCObject* obj)
    {
        handle.Object = (nint)obj;
    }

    public bool StoreObjectInHandleIfNull(ref ObjectHandle handle, GCObject* obj)
    {
        var result = InterlockedCompareExchangeObjectInHandle(ref handle, obj, null);        
        return result == null;
    }

    public void SetDependentHandleSecondary(ref ObjectHandle handle, GCObject* obj)
    {
        handle.ExtraInfo = (nint)obj;
    }

    public GCObject* GetDependentHandleSecondary(ref ObjectHandle handle)
    {
        return (GCObject*)handle.ExtraInfo;
    }

    public GCObject* InterlockedCompareExchangeObjectInHandle(ref ObjectHandle handle, GCObject* obj, GCObject* comparandObject)
    {
        return (GCObject*)Interlocked.CompareExchange(ref handle.Object, (nint)obj, (nint)comparandObject);
    }

    public HandleType HandleFetchType(ref ObjectHandle handle)
    {
        return handle.Type;
    }

    public void TraceRefCountedHandles(void* callback, uint* param1, uint* param2)
    {
        Write("GCHandleManager TraceRefCountedHandles");
    }
}
