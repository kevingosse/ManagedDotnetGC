namespace ManagedDotnetGC.Interfaces;

[NativeObject]
public unsafe interface IGCHandleManager
{
    bool Initialize();

    void Shutdown();

    nint GetGlobalHandleStore();

    nint CreateHandleStore();

    void DestroyHandleStore(nint store);

    ref ObjectHandle CreateGlobalHandleOfType(GCObject* obj, HandleType type);

    ref ObjectHandle CreateDuplicateHandle(ref ObjectHandle handle);

    void DestroyHandleOfType(ref ObjectHandle handle, HandleType type);

    void DestroyHandleOfUnknownType(ref ObjectHandle handle);

    void SetExtraInfoForHandle(ref ObjectHandle handle, HandleType type, nint extraInfo);

    nint GetExtraInfoFromHandle(ref ObjectHandle handle);

    void StoreObjectInHandle(ref ObjectHandle handle, GCObject* obj);

    bool StoreObjectInHandleIfNull(ref ObjectHandle handle, GCObject* obj);

    void SetDependentHandleSecondary(ref ObjectHandle handle, GCObject* obj);

    GCObject* GetDependentHandleSecondary(ref ObjectHandle handle);

    GCObject* InterlockedCompareExchangeObjectInHandle(ref ObjectHandle handle, GCObject* obj, GCObject* comparandObject);

    HandleType HandleFetchType(ref ObjectHandle handle);

    void TraceRefCountedHandles(void* callback, uint* param1, uint* param2);
}