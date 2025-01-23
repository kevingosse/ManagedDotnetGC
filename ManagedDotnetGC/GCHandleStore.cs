using ManagedDotnetGC.Dac;
using ManagedDotnetGC.Interfaces;
using System.Runtime.InteropServices;

using static ManagedDotnetGC.Log;

namespace ManagedDotnetGC;

public unsafe class GCHandleStore : IGCHandleStore
{
    private readonly NativeObjects.IGCHandleStore _nativeObject;
    private readonly nint* _store;
    private int _handleCount;

    public GCHandleStore()
    {
        _nativeObject = NativeObjects.IGCHandleStore.Wrap(this);
        _store = (nint*)NativeMemory.AllocZeroed((nuint)sizeof(nint) * 65535);
        Write($"GCHandleStore {(IntPtr)_store:x2}");
    }

    public IntPtr IGCHandleStoreObject => _nativeObject;

    public void DumpHandles(DacManager? dacManager)
    {
        Write("GCHandleStore DumpHandles");

        for (int i = 0; i < _handleCount; i++)
        {
            var target = _store[i];

            if (dacManager == null)
            {
                Write($"Handle {i} - {target:x2}");
            }
            else
            {
                Write($"Handle {i} - {target:x2} - {dacManager.GetObjectName(new(target))}");
            }
        }
    }

    public void Uproot()
    {
        Write("GCHandleStore Uproot");
    }

    public bool ContainsHandle(OBJECTHANDLE handle)
    {
        Console.WriteLine("GCHandleStore ContainsHandle");
        return false;
    }

    public unsafe OBJECTHANDLE CreateHandleOfType(GCObject* obj, HandleType type)
    {
        Write($"CreateHandleOfType {type} for {(IntPtr)obj:x2}");

        var handle = GetNextAvailableHandle();
        handle.SetObject((nint)obj);

        Write($"Returning {handle}");
        return handle;
    }

    public unsafe OBJECTHANDLE CreateHandleOfType2(GCObject* obj, HandleType type, int heapToAffinitizeTo)
    {
        Write($"GCHandleStore CreateHandleOfType2 - {(nint)obj:x2}");

        var handle = GetNextAvailableHandle();
        handle.SetObject((nint)obj);

        Write($"Returning {handle}");
        return handle;
    }

    public unsafe OBJECTHANDLE CreateHandleWithExtraInfo(GCObject* obj, HandleType type, void* pExtraInfo)
    {
        Write("GCHandleStore CreateHandleWithExtraInfo");
        return GetNextAvailableHandle();
    }

    public unsafe OBJECTHANDLE CreateDependentHandle(GCObject* primary, GCObject* secondary)
    {
        Write("GCHandleStore CreateDependentHandle");
        return GetNextAvailableHandle();
    }

    public void Destructor()
    {
        Write("GCHandleStore Destructor");
    }

    private OBJECTHANDLE GetNextAvailableHandle()
    {
        var handle = (nint)(_store + _handleCount);
        _handleCount++;
        return new(handle);
    }
}
