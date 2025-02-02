﻿using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using static ManagedDotnetGC.Log;

namespace ManagedDotnetGC;

public class DllMain
{
    [UnmanagedCallersOnly(EntryPoint = "GC_Initialize")]
    public static unsafe HResult GC_Initialize(IntPtr clrToGC, IntPtr* gcHeap, IntPtr* gcHandleManager, GcDacVars* gcDacVars)
    {
        Write("GC_Initialize");

        var gc = new GCHeap(NativeObjects.IGCToCLR.Wrap(clrToGC));

        *gcHeap = gc.IGCHeapObject;
        *gcHandleManager = gc.IGCHandleManagerObject;

        return HResult.S_OK;
    }

    [UnmanagedCallersOnly(EntryPoint = "GC_VersionInfo", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static unsafe void GC_VersionInfo(VersionInfo* versionInfo)
    {
        Write($"GC_VersionInfo {versionInfo->MajorVersion}.{versionInfo->MinorVersion}.{versionInfo->BuildVersion}");

        versionInfo->MajorVersion = 5;
        versionInfo->MinorVersion = 3;
    }
}