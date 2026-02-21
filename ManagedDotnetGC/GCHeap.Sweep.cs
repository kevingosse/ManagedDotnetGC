using static ManagedDotnetGC.Log;

namespace ManagedDotnetGC;

unsafe partial class GCHeap
{
    private void SweepPhase()
    {
        Sweep();      
    }

    private void Sweep()
    {
        foreach (var ptr in WalkHeapObjects())
        {
            var obj = (GCObject*)ptr;

            bool marked = obj->IsMarked();
            obj->Unmark();

            bool isFreeObject = obj->MethodTable == _freeObjectMethodTable;

            if (!marked && !isFreeObject)
            {
                var startPtr = ptr - IntPtr.Size; // Include the header
                var endPtr = Align(startPtr + (nint)obj->ComputeSize());

                // Clear the memory
                new Span<byte>((void*)startPtr, (int)(endPtr - startPtr)).Clear();

                // Allocate a free object to keep the heap walkable
                AllocateFreeObject(ptr, (uint)(endPtr - startPtr - SizeOfObject));
            }
        }
    }

    private void ClearHandles(ReadOnlySpan<HandleType> handleTypes)
    {
        foreach (var handle in _gcHandleManager.Store.EnumerateHandlesOfType(handleTypes))
        {
            var obj = handle->Object;

            if (obj != null && !obj->IsMarked())
            {
                handle->Clear();
            }
        }
    }
}
