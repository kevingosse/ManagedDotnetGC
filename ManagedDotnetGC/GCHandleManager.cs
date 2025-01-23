using ManagedDotnetGC.Interfaces;
using static ManagedDotnetGC.Log;

namespace ManagedDotnetGC;

internal unsafe class GCHandleManager : IGCHandleManager
{
    private readonly NativeObjects.IGCToCLRInvoker _gcToClr;
    private readonly NativeObjects.IGCHandleManager _nativeObject;
    private readonly GCHandleStore _gcHandleStore;


    public GCHandleManager(NativeObjects.IGCToCLRInvoker gcToClr)
    {
        _gcHandleStore = new GCHandleStore();
        _gcToClr = gcToClr;
        _nativeObject = NativeObjects.IGCHandleManager.Wrap(this);
    }

    public IntPtr IGCHandleManagerObject => _nativeObject;

    public GCHandleStore Store => _gcHandleStore;

    public bool Initialize()
    {
        Write("GCHandleManager Initialize");
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

    public unsafe OBJECTHANDLE CreateGlobalHandleOfType(GCObject* obj, HandleType type)
    {
        return _gcHandleStore.CreateHandleOfType(obj, type);
    }

    public OBJECTHANDLE CreateDuplicateHandle(OBJECTHANDLE handle)
    {
        Write("GCHandleManager CreateDuplicateHandle");

        return handle;
    }

    public void DestroyHandleOfType(OBJECTHANDLE handle, HandleType type)
    {
        Write("GCHandleManager DestroyHandleOfType");
    }

    public void DestroyHandleOfUnknownType(OBJECTHANDLE handle)
    {
        Write("GCHandleManager DestroyHandleOfUnknownType");
    }

    public unsafe void SetExtraInfoForHandle(OBJECTHANDLE handle, HandleType type, void* pExtraInfo)
    {
        Write("GCHandleManager SetExtraInfoForHandle");
    }

    public unsafe void* GetExtraInfoFromHandle(OBJECTHANDLE handle)
    {
        Write("GCHandleManager GetExtraInfoFromHandle");
        return null;
    }

    public unsafe void StoreObjectInHandle(OBJECTHANDLE handle, GCObject* obj)
    {
        Write($"GCHandleManager StoreObjectInHandle {handle} {(IntPtr)obj:x2}");
        handle.SetObject((nint)obj);
    }

    public unsafe bool StoreObjectInHandleIfNull(OBJECTHANDLE handle, GCObject* obj)
    {
        Write("GCHandleManager StoreObjectInHandleIfNull");
        return false;
    }

    public unsafe void SetDependentHandleSecondary(OBJECTHANDLE handle, GCObject* obj)
    {
        Write("GCHandleManager SetDependentHandleSecondary");
    }

    public unsafe GCObject* GetDependentHandleSecondary(OBJECTHANDLE handle)
    {
        Write("GCHandleManager GetDependentHandleSecondary");
        return null;
    }

    public unsafe GCObject* InterlockedCompareExchangeObjectInHandle(OBJECTHANDLE handle, GCObject* obj, GCObject* comparandObject)
    {
        Write("GCHandleManager InterlockedCompareExchangeObjectInHandle");
        return null;
    }

    public HandleType HandleFetchType(OBJECTHANDLE handle)
    {
        Write("GCHandleManager HandleFetchType");
        return HandleType.HNDTYPE_WEAK_SHORT;
    }

    public unsafe void TraceRefCountedHandles(void* callback, uint* param1, uint* param2)
    {
        Write("GCHandleManager TraceRefCountedHandles");
    }
}
