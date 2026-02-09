using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using static ManagedDotnetGC.Log;

namespace ManagedDotnetGC;

public class DllMain
{
#if WINDOWS
    [UnmanagedCallersOnly(EntryPoint = "_GC_Initialize")]
#else
    [UnmanagedCallersOnly(EntryPoint = "GC_Initialize")]
#endif
    public static unsafe HResult GC_Initialize(IntPtr clrToGC, IntPtr* gcHeap, IntPtr* gcHandleManager, GcDacVars* gcDacVars)
    {
        Write("GC_Initialize");
        
        if (Environment.GetEnvironmentVariable("GC_DEBUG") == "1")
        {
            Write($"Waiting for debugger to attach to process {Environment.ProcessId}...");
            Console.ReadLine();
        }

        var clrToGc = NativeObjects.IGCToCLR.Wrap(clrToGC);

        fixed (byte* privateKey = "gcServer"u8, publicKey = "System.GC.Server"u8)
        {
            clrToGc.GetBooleanConfigValue(privateKey, publicKey, out var gcServerEnabled);

            if (gcServerEnabled)
            {
                Write("This GC isn't compatible with server GC. Set DOTNET_gcServer=0 to disable it.");
                return HResult.E_FAIL;
            }
        }

        var gc = new GCHeap(clrToGc);

        *gcHeap = gc.IGCHeapObject;
        *gcHandleManager = gc.IGCHandleManagerObject;

        return HResult.S_OK;
    }

#if WINDOWS
    [UnmanagedCallersOnly(EntryPoint = "_GC_VersionInfo", CallConvs = new[] { typeof(CallConvCdecl) })]
#else
    [UnmanagedCallersOnly(EntryPoint = "GC_VersionInfo", CallConvs = new[] { typeof(CallConvCdecl) })]
#endif
    public static unsafe void GC_VersionInfo(VersionInfo* versionInfo)
    {
        Write($"GC_VersionInfo {versionInfo->MajorVersion}.{versionInfo->MinorVersion}.{versionInfo->BuildVersion}");

        versionInfo->MajorVersion = 5;
        versionInfo->MinorVersion = 3;
    }
}