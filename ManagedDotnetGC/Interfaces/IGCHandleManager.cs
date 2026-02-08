namespace ManagedDotnetGC.Interfaces;

[NativeObject]
public unsafe interface IGCHandleManager
{
    bool Initialize();

    void Shutdown();

    nint GetGlobalHandleStore();

    nint CreateHandleStore();

    void DestroyHandleStore(nint store);

    ObjectHandle* CreateGlobalHandleOfType(GCObject* obj, HandleType type);

    ObjectHandle* CreateDuplicateHandle(ObjectHandle* handle);

    void DestroyHandleOfType(ObjectHandle* handle, HandleType type);

    void DestroyHandleOfUnknownType(ObjectHandle* handle);

    void SetExtraInfoForHandle(ObjectHandle* handle, HandleType type, nint extraInfo);

    nint GetExtraInfoFromHandle(ObjectHandle* handle);

    void StoreObjectInHandle(ObjectHandle* handle, GCObject* obj);

    bool StoreObjectInHandleIfNull(ObjectHandle* handle, GCObject* obj);

    void SetDependentHandleSecondary(ObjectHandle* handle, GCObject* obj);

    GCObject* GetDependentHandleSecondary(ObjectHandle* handle);

    GCObject* InterlockedCompareExchangeObjectInHandle(ObjectHandle* handle, GCObject* obj, GCObject* comparandObject);

    HandleType HandleFetchType(ObjectHandle* handle);

    void TraceRefCountedHandles(void* callback, uint* param1, uint* param2);
}