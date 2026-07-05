namespace ManagedDotnetGC;

unsafe partial class GCHeap
{
    private void SweepPhase()
    {
        _lastLiveBytes = _regionAllocator.Sweep(_freeObjectMethodTable);
    }

    private void ClearHandles(ReadOnlySpan<HandleType> handleTypes)
    {
        foreach (var handle in _gcHandleManager.Store.EnumerateHandlesOfType(handleTypes))
        {
            var obj = handle->Object;
            if (obj != null && _nativeAllocator.IsInRange((nint)obj) && !obj->IsMarked())
            {
                handle->Clear();
            }
        }
    }
}
