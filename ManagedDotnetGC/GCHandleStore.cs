using ManagedDotnetGC.Dac;
using ManagedDotnetGC.Interfaces;
using static ManagedDotnetGC.Log;

namespace ManagedDotnetGC;

public unsafe class GCHandleStore : IGCHandleStore
{
    private const int HandleTypeCount = (int)HandleType.Max + 1;

    private readonly NativeObjects.IGCHandleStore _nativeObject;
    private readonly HandleSegmentList[] _lists;

    public GCHandleStore()
    {
        _nativeObject = NativeObjects.IGCHandleStore.Wrap(this);

        _lists = new HandleSegmentList[HandleTypeCount];

        for (int i = 0; i < HandleTypeCount; i++)
        {
            _lists[i] = new HandleSegmentList();
        }
    }

    public IntPtr IGCHandleStoreObject => _nativeObject;

    /// <summary>
    /// Get the segment list for a specific handle type, for efficient per-type iteration.
    /// </summary>
    private HandleSegmentList GetList(HandleType type) => _lists[(int)type];

    public HandlesEnumerable EnumerateHandlesOfType(ReadOnlySpan<HandleType> handleTypes) => new(this, handleTypes);

    public void DumpHandles(DacManager? dacManager)
    {
        Write("GCHandleStore DumpHandles");

        int index = 0;
        foreach (var handle in EnumerateHandlesOfType(ObjectHandle.AllTypes))
        {
            var output = $"Handle {index++} - {handle->ToString()}";

            if (dacManager != null && handle->Object != null)
            {
                output += $" - {dacManager.GetObjectName(new((nint)handle->Object))}";
            }

            Write(output);
        }
    }

    public void Uproot()
    {
        Write("GCHandleStore Uproot");
        throw new NotImplementedException();
    }

    public bool ContainsHandle(ObjectHandle* handle)
    {
        return _lists[(int)handle->Type].FindSegment(handle) != null;
    }

    public ObjectHandle* CreateHandleOfType(GCObject* obj, HandleType type)
    {
        return CreateHandleWithExtraInfo(obj, type, null);
    }

    public ObjectHandle* CreateHandleOfType2(GCObject* obj, HandleType type, int heapToAffinitizeTo)
    {
        return CreateHandleWithExtraInfo(obj, type, null);
    }

    public ObjectHandle* CreateHandleWithExtraInfo(GCObject* obj, HandleType type, void* pExtraInfo)
    {
        var handle = _lists[(int)type].Allocate();

        handle->Type = type;
        handle->Object = obj;
        handle->ExtraInfo = (nint)pExtraInfo;

        return handle;
    }

    public ObjectHandle* CreateDependentHandle(GCObject* primary, GCObject* secondary)
    {
        return CreateHandleWithExtraInfo(primary, HandleType.HNDTYPE_DEPENDENT, secondary);
    }

    public void DestroyHandle(ObjectHandle* handle)
    {
        _lists[(int)handle->Type].Free(handle);
    }

    public void Destructor()
    {
        Write("GCHandleStore Destructor");
        throw new NotImplementedException();
    }

    public readonly ref struct HandlesEnumerable
    {
        private readonly ReadOnlySpan<HandleType> _handleTypes;
        private readonly GCHandleStore _store;

        internal HandlesEnumerable(GCHandleStore store, ReadOnlySpan<HandleType> handleTypes)
        {
            _handleTypes = handleTypes;
            _store = store;
        }

        public HandlesEnumerator GetEnumerator() => new(_store, _handleTypes);
    }

    public ref struct HandlesEnumerator
    {
        private readonly ReadOnlySpan<HandleType> _handleTypes;
        private readonly GCHandleStore _store;

        private int _handleTypeIndex;
        private HandleSegmentList.Enumerator _current;
        private bool _hasEnumerator;

        internal HandlesEnumerator(GCHandleStore store, ReadOnlySpan<HandleType> handleTypes)
        {
            _handleTypes = handleTypes;
            _store = store;
            _handleTypeIndex = 0;
            _hasEnumerator = false;
        }

        public ObjectHandle* Current => _current.Current;

        public bool MoveNext()
        {
            while (_handleTypeIndex < _handleTypes.Length)
            {
                if (!_hasEnumerator)
                {
                    var handleType = _handleTypes[_handleTypeIndex];
                    _current = _store.GetList(handleType).GetEnumerator();
                    _hasEnumerator = true;
                }

                if (_current.MoveNext())
                {
                    return true;
                }

                _handleTypeIndex++;
                _hasEnumerator = false;
            }

            return false;
        }
    }
}
