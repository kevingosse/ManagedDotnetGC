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
        _store = (nint*)Marshal.AllocHGlobal(sizeof(nint) * 65535);

        for (int i = 0; i < 65535; i++)
        {
            _store[i] = 0;
        }

        Write($"GCHandleStore {(IntPtr)_store:x2}");

        _nativeObject = NativeObjects.IGCHandleStore.Wrap(this);
    }

    public IntPtr IGCHandleStoreObject => _nativeObject;

    public void DumpHandles(DacManager? dacManager)
    {
        Write("GCHandleStore DumpHandles");

        for (int i = 0; i < _handleCount; i++)
        {
            var target = *(_store + i);

            if (dacManager == null)
            {
                Write($"Handle {i} - {target:x2}");
            }
            else
            {
                //var mtPtr = *(nint*)target;
                if (target != 0)
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

        //if (obj != null)
        //{
        //    Console.WriteLine($"GCHandleStore CreateHandleOfType - {(nint)obj:x2} -> {(*obj).MethodTable:x2}");
        //}
        //else
        //{
        //    Console.WriteLine($"GCHandleStore CreateHandleOfType - {(nint)obj:x2}");
        //}
        
        var handle = _store + _handleCount;

        *handle = (nint)obj;

        _handleCount++;

        Write($"Returning {(nint)handle:x2}");

        return new OBJECTHANDLE((nint)handle);
    }

    public unsafe OBJECTHANDLE CreateHandleOfType2(GCObject* obj, HandleType type, int heapToAffinitizeTo)
    {
        Write($"GCHandleStore CreateHandleOfType2 - {(nint)obj:x2}");

        var handle = _store + _handleCount;

         *handle = (nint)obj;
        //*handle = (nint)(&obj);
        var result = new OBJECTHANDLE((nint)handle);
        _handleCount++;

        Write($"Returning {result.Value:x2}");

        return result;
    }

    public unsafe OBJECTHANDLE CreateHandleWithExtraInfo(GCObject* obj, HandleType type, void* pExtraInfo)
    {
        Write("GCHandleStore CreateHandleWithExtraInfo");
        return new OBJECTHANDLE((nint)_store + (_handleCount++));
    }

    public unsafe OBJECTHANDLE CreateDependentHandle(GCObject* primary, GCObject* secondary)
    {
        Write("GCHandleStore CreateDependentHandle");
        return new OBJECTHANDLE((nint)_store + (_handleCount++));
    }

    public void Destructor()
    {
        Write("GCHandleStore Destructor");
    }
}
