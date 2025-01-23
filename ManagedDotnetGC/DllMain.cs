using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using static ManagedDotnetGC.Log;

namespace ManagedDotnetGC;

public class DllMain
{
    [UnmanagedCallersOnly(EntryPoint = "DllMain")]
    public static bool Main(nint hModule, int fdwReason, nint reserved)
    {
        if (fdwReason == 1)
        {
            Write("DllMain");
        }

        return true;
    }

    [UnmanagedCallersOnly(EntryPoint = "Custom_GC_Initialize")]
    public static unsafe HResult GC_Initialize(IntPtr clrToGC, IntPtr* gcHeap, IntPtr* gcHandleManager, GcDacVars* gcDacVars)
    {
        Write("GC_Initialize");

        var gc = new GCHeap(NativeObjects.IGCToCLR.Wrap(clrToGC));

        *gcHeap = gc.IGCHeapObject;
        *gcHandleManager = gc.IGCHandleManagerObject;

        return HResult.S_OK;
    }

    [UnmanagedCallersOnly(EntryPoint = "Custom_GC_VersionInfo", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static unsafe HResult GC_VersionInfo(VersionInfo* versionInfo)
    {
        Write($"GC_VersionInfo {versionInfo->MajorVersion}.{versionInfo->MinorVersion}.{versionInfo->BuildVersion}");

        versionInfo->MajorVersion = 5;
        versionInfo->MinorVersion = 3;

        return HResult.S_OK;
    }
}