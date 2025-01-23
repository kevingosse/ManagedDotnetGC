namespace ManagedDotnetGC.Interfaces;

[NativeObject]
public unsafe interface IGCHandleStore
{
    void Uproot();

    bool ContainsHandle(OBJECTHANDLE handle);

    OBJECTHANDLE CreateHandleOfType2(GCObject* obj, HandleType type, int heapToAffinitizeTo);

    OBJECTHANDLE CreateHandleOfType(GCObject* obj, HandleType type);

    OBJECTHANDLE CreateHandleWithExtraInfo(GCObject* obj, HandleType type, void* pExtraInfo);

    OBJECTHANDLE CreateDependentHandle(GCObject* primary, GCObject* secondary);

    void Destructor();
}