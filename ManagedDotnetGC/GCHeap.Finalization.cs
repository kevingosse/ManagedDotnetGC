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

            if (obj != null && obj->Header->HasFinalizerRun)
            {
                // Clear the bit while skipping (stock finalizerthread.cpp:253-259): the object
                // is out of the queue now, so a later ReRegisterForFinalize must see a clear
                // bit and enqueue for real instead of early-returning (missing-features 5.1).
                // Header writes are GC heap writes: via the alias (SPEC-M6 §3).
                obj->AliasHeader->HasFinalizerRun = false;
                obj = null;
            }
        }

        return obj;
    }

    public void SetFinalizationRun(GCObject* obj)
    {
        Write($"Setting finalization run for object at {(nint)obj:X}");
        obj->AliasHeader->HasFinalizerRun = true;
    }

    public bool RegisterForFinalization(int gen, GCObject* obj)
    {
        Write($"Registering object at {(nint)obj:X} for finalization");
        if (obj->Header->HasFinalizerRun)
        {
            obj->AliasHeader->HasFinalizerRun = false;
            return true;
        }

        lock (_finalizationLock)
        {
            if (_finalizationQueueCount >= _finalizationQueue.Length)
            {
                try
                {
                    var newQueue = new GCObject*[_finalizationQueue.Length * 2];
                    Array.Copy(_finalizationQueue, newQueue, _finalizationQueue.Length);
                    _finalizationQueue = newQueue;
                }
                catch (OutOfMemoryException)
                {
                    // The EE turns false into a managed OutOfMemoryException; an exception
                    // escaping this UnmanagedCallersOnly frame would be a fail-fast (§9)
                    return false;
                }
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
                if (obj->Header->HasFinalizerRun)
                {
                    // Suppressed + dead: dropped outright with the bit cleared, never
                    // resurrected (stock finalization.cpp:367-377; missing-features 5.1)
                    obj->AliasHeader->HasFinalizerRun = false;
                }
                else if (!_gcToClr.EagerFinalized(obj))
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
