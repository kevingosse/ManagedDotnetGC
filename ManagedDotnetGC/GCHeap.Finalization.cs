using static ManagedDotnetGC.Log;

namespace ManagedDotnetGC;

unsafe partial class GCHeap
{
    private readonly Lock _finalizationLock = new();
    private GCObject*[] _finalizationQueue = new GCObject*[16];
    private int _finalizationQueueCount;

    private readonly Queue<nint> _freachableQueue = new();
    private readonly Queue<nint> _criticalFreachableQueue = new();

    public nint GetExtraWorkForFinalization() => 0;

    public nint GetNumberOfFinalizable() => _freachableQueue.Count + _criticalFreachableQueue.Count;

    public GCObject* GetNextFinalizable()
    {
        GCObject* obj = null;

        while (obj == null && (_criticalFreachableQueue.Count > 0 || _freachableQueue.Count > 0))
        {           
            if (_freachableQueue.Count > 0)
            {
                obj = (GCObject*)_freachableQueue.Dequeue();
            }
            else if (_criticalFreachableQueue.Count > 0)
            {
                obj = (GCObject*)_criticalFreachableQueue.Dequeue();
            }

            if (obj != null && (obj->Header->HasFinalizerRun || obj->MethodTable == _freeObjectMethodTable))
            {
                obj = null;
            }            
        }

        return obj;
    }

    public void SetFinalizationRun(GCObject* obj)
    {
        Write($"Setting finalization run for object at {(nint)obj:X}");
        obj->Header->HasFinalizerRun = true;
    }

    public bool RegisterForFinalization(int gen, GCObject* obj)
    {
        Write($"Registering object at {(nint)obj:X} for finalization");
        if (obj->Header->HasFinalizerRun)
        {
            obj->Header->HasFinalizerRun = false;
            return true;
        }

        lock (_finalizationLock)
        {
            if (_finalizationQueueCount >= _finalizationQueue.Length)
            {
                var newQueue = new GCObject*[_finalizationQueue.Length * 2];
                Array.Copy(_finalizationQueue, newQueue, _finalizationQueue.Length);
                _finalizationQueue = newQueue;
            }

            _finalizationQueue[_finalizationQueueCount++] = obj;
        }

        return true;
    }

    private void PrepareForFinalization()
    {
        int i = 0;

        while (i < _finalizationQueueCount)
        {
            var obj = _finalizationQueue[i];

            if (!obj->IsMarked())
            {
                if (!_gcToClr.EagerFinalized(obj))
                {
                    if (obj->MethodTable->HasCriticalFinalizer)
                    {
                        _criticalFreachableQueue.Enqueue((nint)obj);
                    }
                    else
                    {
                        _freachableQueue.Enqueue((nint)obj);
                    }
                }

                _finalizationQueueCount--;
                _finalizationQueue[i] = _finalizationQueue[_finalizationQueueCount];
                _finalizationQueue[_finalizationQueueCount] = null;
            }
            else
            {
                i++;
            }
        }
    }
}
