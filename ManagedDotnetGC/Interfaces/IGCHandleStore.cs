namespace ManagedDotnetGC.Interfaces;

[NativeObject]
public unsafe interface IGCHandleStore
{
    void Uproot();

    bool ContainsHandle(ObjectHandle* handle);

    ObjectHandle* CreateHandleOfType2(GCObject* obj, HandleType type, int heapToAffinitizeTo);

    ObjectHandle* CreateHandleOfType(GCObject* obj, HandleType type);

    ObjectHandle* CreateHandleWithExtraInfo(GCObject* obj, HandleType type, void* pExtraInfo);

    ObjectHandle* CreateDependentHandle(GCObject* primary, GCObject* secondary);

    void Destructor();
}