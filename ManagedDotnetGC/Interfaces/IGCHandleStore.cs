namespace ManagedDotnetGC.Interfaces;

[NativeObject]
public unsafe interface IGCHandleStore
{
    void Uproot();

    bool ContainsHandle(ref ObjectHandle handle);

    ref ObjectHandle CreateHandleOfType2(GCObject* obj, HandleType type, int heapToAffinitizeTo);

    ref ObjectHandle CreateHandleOfType(GCObject* obj, HandleType type);

    ref ObjectHandle CreateHandleWithExtraInfo(GCObject* obj, HandleType type, void* pExtraInfo);

    ref ObjectHandle CreateDependentHandle(GCObject* primary, GCObject* secondary);

    void Destructor();
}