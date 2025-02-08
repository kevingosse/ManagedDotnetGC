using ManagedDotnetGC.Interfaces;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static ManagedDotnetGC.Log;

namespace ManagedDotnetGC;

public unsafe class GCHandleStore : IGCHandleStore
{
    private const int MaxHandles = 10_000;

    private readonly NativeObjects.IGCHandleStore _nativeObject;
    private readonly ObjectHandle* _store;
    private int _handleCount;

    public GCHandleStore()
    {
        _nativeObject = NativeObjects.IGCHandleStore.Wrap(this);
        _store = (ObjectHandle*)NativeMemory.AllocZeroed(MaxHandles, (nuint)sizeof(ObjectHandle));
    }

    public IntPtr IGCHandleStoreObject => _nativeObject;

    public void DumpHandles()
    {
        Write("GCHandleStore DumpHandles");

        bool isPreeptiveGCDisabled = GCHeap.GcToClr.IsPreemptiveGCDisabled();

        if (isPreeptiveGCDisabled)
        {
            GCHeap.GcToClr.EnablePreemptiveGC();
        }

        var buffer = new char[1000];

        fixed (char* p = buffer)
        {
            for (int i = 0; i < _handleCount; i++)
            {
                var handle = _store[i];
                var output = $"Handle {i} - {_store[i]}";

                if (handle.Object != 0)
                {
                    if (DllMain.GetTypeCallback != null)
                    {
                        int size = 0;
                        DllMain.GetTypeCallback(handle.Object, p, buffer.Length, &size);
                        output += $" - Object type: {new string(buffer[..size])}";
                    }
                }

                Write(output);
            }
        }

        if (isPreeptiveGCDisabled)
        {
            GCHeap.GcToClr.DisablePreemptiveGC();
        }
    }

    public static object GetObject()
    {
        return new string('x', 5);
    }


    public static nint GetAddress<T>(T obj)
    {
        // Get the address of the reference, cast it to a pointer,
        // then dereference it to get the address of the object
        return (nint)(*(T**)&obj);
    }

    public void Uproot()
    {
        Write("GCHandleStore Uproot");
    }

    public bool ContainsHandle(ref ObjectHandle handle)
    {
        var ptr = Unsafe.AsPointer(ref handle);
        return ptr >= _store && ptr < _store + _handleCount;
    }

    public unsafe ref ObjectHandle CreateHandleOfType(GCObject* obj, HandleType type)
    {
        return ref CreateHandleWithExtraInfo(obj, type, null);
    }

    public unsafe ref ObjectHandle CreateHandleOfType2(GCObject* obj, HandleType type, int heapToAffinitizeTo)
    {
        return ref CreateHandleWithExtraInfo(obj, type, null);
    }

    public unsafe ref ObjectHandle CreateHandleWithExtraInfo(GCObject* obj, HandleType type, void* pExtraInfo)
    {
        var index = Interlocked.Increment(ref _handleCount) - 1;

        if (index >= MaxHandles)
        {
            Environment.FailFast("Too many handles");
        }

        ref var handle = ref _store[index];

        handle.Object = (nint)obj;
        handle.Type = type;
        handle.ExtraInfo = (nint)pExtraInfo;

        return ref handle;
    }

    public unsafe ref ObjectHandle CreateDependentHandle(GCObject* primary, GCObject* secondary)
    {
        return ref CreateHandleWithExtraInfo(primary, HandleType.HNDTYPE_DEPENDENT, secondary);
    }

    public void Destructor()
    {
        Write("GCHandleStore Destructor");
    }
}
