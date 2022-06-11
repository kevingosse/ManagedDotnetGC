using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ManagedDotnetGC
{
    public class DllMain
    {
        [UnmanagedCallersOnly(EntryPoint = "DllMain")]
        public static bool Main(nint hModule, int fdwReason, nint reserved)
        {
            if (fdwReason == 1)
            {
                Console.WriteLine("[GC] DllMain");
            }

            return true;
        }

        [UnmanagedCallersOnly(EntryPoint = "Custom_GC_Initialize")]
        public static unsafe HResult GC_Initialize(IntPtr clrToGC, IntPtr* gcHeap, IntPtr* gcHandleManager, GcDacVars* gcDacVars)
        {
            Console.WriteLine("[GC] GC_Initialize");

            var gc = new GCHeap(NativeStubs.IGCToCLRStub.Wrap(clrToGC));

            *gcHeap = gc.IGCHeapObject;
            *gcHandleManager = gc.IGCHandleManagerObject;

            return HResult.S_OK;
        }

        [UnmanagedCallersOnly(EntryPoint = "Custom_GC_VersionInfo", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static unsafe HResult GC_VersionInfo(VersionInfo* versionInfo)
        {
            Console.WriteLine("[GC] GC_VersionInfo");

            (*versionInfo).MajorVersion = 5;
            (*versionInfo).MinorVersion = 1;

            return HResult.S_OK;
        }
    }
}