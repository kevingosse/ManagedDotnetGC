using static ManagedDotnetGC.Log;

namespace ManagedDotnetGC;

partial class GCHeap
{
    public nint GetExtraWorkForFinalization()
    {
        Write("GetExtraWorkForFinalization");
        return 0;
    }

    public nint GetNumberOfFinalizable()
    {
        Write("GetNumberOfFinalizable");
        return 0;
    }

    public unsafe GCObject* GetNextFinalizable()
    {
        Write("GetNextFinalizable");
        return null;
    }

    public unsafe void SetFinalizationRun(GCObject* obj)
    {
        Write($"SetFinalizationRun - {(nint)obj:x2}");
    }

    public unsafe bool RegisterForFinalization(int gen, GCObject* obj)
    {
        Write("RegisterForFinalization");
        return false;
    }
}
