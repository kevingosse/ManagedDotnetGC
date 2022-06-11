namespace ManagedDotnetGC;

[GenerateNativeStub]
public unsafe interface IGCHandleManager
{
    bool Initialize();

    void Shutdown();

    IntPtr GetGlobalHandleStore();

    IntPtr CreateHandleStore();

    void DestroyHandleStore(IntPtr store);

    OBJECTHANDLE CreateGlobalHandleOfType(GCObject* obj, HandleType type);

    OBJECTHANDLE CreateDuplicateHandle(OBJECTHANDLE handle);

    void DestroyHandleOfType(OBJECTHANDLE handle, HandleType type);

    void DestroyHandleOfUnknownType(OBJECTHANDLE handle);

    void SetExtraInfoForHandle(OBJECTHANDLE handle, HandleType type, void* pExtraInfo);

    void* GetExtraInfoFromHandle(OBJECTHANDLE handle);

    void StoreObjectInHandle(OBJECTHANDLE handle, GCObject* obj);

    bool StoreObjectInHandleIfNull(OBJECTHANDLE handle, GCObject* obj);

    void SetDependentHandleSecondary(OBJECTHANDLE handle, GCObject* obj);

    GCObject* GetDependentHandleSecondary(OBJECTHANDLE handle);

    GCObject* InterlockedCompareExchangeObjectInHandle(OBJECTHANDLE handle, GCObject* obj, GCObject* comparandObject);

    HandleType HandleFetchType(OBJECTHANDLE handle);

    void TraceRefCountedHandles(void* callback, uint* param1, uint* param2);
}