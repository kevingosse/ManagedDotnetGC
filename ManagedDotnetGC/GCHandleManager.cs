using ManagedDotnetGC.Interfaces;
using System.Runtime.CompilerServices;
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
        throw new NotImplementedException();
    }

    public void DestroyHandleStore(IntPtr store)
    {
        Write("GCHandleManager DestroyHandleStore");
        throw new NotImplementedException();
    }

    public ObjectHandle* CreateGlobalHandleOfType(GCObject* obj, HandleType type)
    {
        return _gcHandleStore.CreateHandleOfType(obj, type);
    }

    public ObjectHandle* CreateDuplicateHandle(ObjectHandle* handle)
    {
        var newHandle = _gcHandleStore.CreateHandleOfType(handle->Object, handle->Type);
        newHandle->ExtraInfo = handle->ExtraInfo;
        return newHandle;
    }

    public void DestroyHandleOfType(ObjectHandle* handle, HandleType type)
    {
        Write("GCHandleManager DestroyHandleOfType");
        _gcHandleStore.DestroyHandle(handle);
    }

    public void DestroyHandleOfUnknownType(ObjectHandle* handle)
    {
        Write("GCHandleManager DestroyHandleOfUnknownType");
        _gcHandleStore.DestroyHandle(handle);
    }

    public void SetExtraInfoForHandle(ObjectHandle* handle, HandleType type, nint extraInfo)
    {
        handle->ExtraInfo = extraInfo;
    }

    public nint GetExtraInfoFromHandle(ObjectHandle* handle)
    {
        return handle->ExtraInfo;
    }

    public void StoreObjectInHandle(ObjectHandle* handle, GCObject* obj)
    {
        handle->Object = obj;
    }

    public bool StoreObjectInHandleIfNull(ObjectHandle* handle, GCObject* obj)
    {
        var result = InterlockedCompareExchangeObjectInHandle(handle, obj, null);        
        return result == null;
    }

    public void SetDependentHandleSecondary(ObjectHandle* handle, GCObject* obj)
    {
        handle->ExtraInfo = (nint)obj;
    }

    public GCObject* GetDependentHandleSecondary(ObjectHandle* handle)
    {
        return (GCObject*)handle->ExtraInfo;
    }

    public GCObject* InterlockedCompareExchangeObjectInHandle(ObjectHandle* handle, GCObject* obj, GCObject* comparandObject)
    {
        return (GCObject*)Interlocked.CompareExchange(ref Unsafe.AsRef<nint>(handle->Object), (nint)obj, (nint)comparandObject);
    }

    public HandleType HandleFetchType(ObjectHandle* handle)
    {
        return handle->Type;
    }

    public void TraceRefCountedHandles(void* callback, uint* param1, uint* param2)
    {
        Write("GCHandleManager TraceRefCountedHandles");
    }
}
