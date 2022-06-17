namespace ManagedDotnetGC;

using ULONG = System.UInt64;
using WCHAR = System.Char;
using DWORD = System.Int32;

[GenerateNativeStub]
public unsafe interface ISOSDacInterface : IUnknown
{
    HResult GetThreadStoreData(out DacpThreadStoreData data);

    HResult GetAppDomainStoreData(out DacpAppDomainStoreData data);

    HResult GetAppDomainList(
        uint count,
        CLRDATA_ADDRESS* values,
        out uint pNeeded);

    HResult GetAppDomainData(
        CLRDATA_ADDRESS addr,
        out DacpAppDomainData data);

    HResult GetAppDomainName(
        CLRDATA_ADDRESS addr,
        uint count,
        char* name,
        out uint pNeeded);

    HResult GetDomainFromContext(
        CLRDATA_ADDRESS context,
        out CLRDATA_ADDRESS domain);

    HResult GetAssemblyList(
        CLRDATA_ADDRESS appDomain,
        int count,
        CLRDATA_ADDRESS* values,
        out int pNeeded);

    HResult GetAssemblyData(
        CLRDATA_ADDRESS baseDomainPtr,
        CLRDATA_ADDRESS assembly,
        out DacpAssemblyData data);

    HResult GetAssemblyName(
        CLRDATA_ADDRESS assembly,
        uint count,
        char* name,
        out uint pNeeded);

    HResult GetModule(
        CLRDATA_ADDRESS addr,
        out IntPtr mod);

    HResult GetModuleData(
        CLRDATA_ADDRESS moduleAddr,
        out DacpModuleData data);

    HResult TraverseModuleMap(
        ModuleMapType mmt,
        CLRDATA_ADDRESS moduleAddr,
        delegate* unmanaged[Stdcall]<uint, CLRDATA_ADDRESS, IntPtr, void> pCallback,
        IntPtr token);

    HResult GetAssemblyModuleList(
        CLRDATA_ADDRESS assembly,
        uint count,
        CLRDATA_ADDRESS* modules,
        out uint pNeeded);

    HResult GetILForModule(
        CLRDATA_ADDRESS moduleAddr,
        int rva,
        out CLRDATA_ADDRESS il);

    HResult GetThreadData(
        CLRDATA_ADDRESS thread,
        out DacpThreadData data);

    HResult GetThreadFromThinlockID(
        uint thinLockId,
        CLRDATA_ADDRESS* pThread);

    HResult GetStackLimits(
        CLRDATA_ADDRESS threadPtr,
        CLRDATA_ADDRESS* lower,
        CLRDATA_ADDRESS* upper,
        CLRDATA_ADDRESS* fp);

    HResult GetMethodDescData(
        CLRDATA_ADDRESS methodDesc,
        CLRDATA_ADDRESS ip,
        DacpMethodDescData* data,
        ULONG cRevertedRejitVersions,
        DacpReJitData* rgRevertedRejitData,
        ULONG* pcNeededRevertedRejitData);

    HResult GetMethodDescPtrFromIP(
        CLRDATA_ADDRESS ip,
        CLRDATA_ADDRESS* ppMD);

    HResult GetMethodDescName(
        CLRDATA_ADDRESS methodDesc,
        uint count,
        WCHAR* name,
        uint* pNeeded);

    HResult GetMethodDescPtrFromFrame(
        CLRDATA_ADDRESS frameAddr,
        CLRDATA_ADDRESS* ppMD);

    HResult GetMethodDescFromToken(
        CLRDATA_ADDRESS moduleAddr,
        MdToken token,
        CLRDATA_ADDRESS* methodDesc);

    HResult GetMethodDescTransparencyData(
        CLRDATA_ADDRESS methodDesc,
        out DacpMethodDescTransparencyData data);

    HResult GetCodeHeaderData(
        CLRDATA_ADDRESS ip,
        out DacpCodeHeaderData data);

    HResult GetJitManagerList(
        uint count,
        DacpJitManagerInfo* managers,
        out uint pNeeded);

    HResult GetJitHelperFunctionName(
        CLRDATA_ADDRESS ip,
        uint count,
        char* name,
        out uint pNeeded);

    HResult GetJumpThunkTarget(
        T_CONTEXT* ctx,
        out CLRDATA_ADDRESS targetIP,
        out CLRDATA_ADDRESS targetMD);

    HResult GetThreadpoolData(
        out DacpThreadpoolData data);

    HResult GetWorkRequestData(
        CLRDATA_ADDRESS addrWorkRequest,
        out DacpWorkRequestData data);

    HResult GetHillClimbingLogEntry(
        CLRDATA_ADDRESS addr,
        out DacpHillClimbingLogEntry data);

    HResult GetObjectData(
        CLRDATA_ADDRESS objAddr,
        out DacpObjectData data);

    HResult GetObjectStringData(
        CLRDATA_ADDRESS obj,
        uint count,
        WCHAR* stringData,
        out uint pNeeded);

    HResult GetObjectClassName(
        CLRDATA_ADDRESS obj,
        uint count,
        WCHAR* className,
        out uint pNeeded);

    HResult GetMethodTableName(
        CLRDATA_ADDRESS mt,
        uint count,
        WCHAR* mtName,
        out uint pNeeded);

    HResult GetMethodTableData(
        CLRDATA_ADDRESS mt,
        out DacpMethodTableData data);

    HResult GetMethodTableSlot(
        CLRDATA_ADDRESS mt,
        uint slot,
        out CLRDATA_ADDRESS value);

    HResult GetMethodTableFieldData(
        CLRDATA_ADDRESS mt,
        out DacpMethodTableFieldData data);

    HResult GetMethodTableTransparencyData(
        CLRDATA_ADDRESS mt,
        out DacpMethodTableTransparencyData data);

    HResult GetMethodTableForEEClass(
        CLRDATA_ADDRESS eeClass,
        out CLRDATA_ADDRESS value);

    HResult GetFieldDescData(
        CLRDATA_ADDRESS fieldDesc,
        out DacpFieldDescData data);

    HResult GetFrameName(
        CLRDATA_ADDRESS vtable,
        uint count,
        WCHAR* frameName,
        out uint pNeeded);

    HResult GetPEFileBase(
        CLRDATA_ADDRESS addr,
        out CLRDATA_ADDRESS baseAddress);

    HResult GetPEFileName(
        CLRDATA_ADDRESS addr,
        uint count,
        WCHAR* fileName,
        out uint pNeeded);

    HResult GetGCHeapData(
        out DacpGcHeapData data);

    HResult GetGCHeapList(
        uint count,
        CLRDATA_ADDRESS* heaps,
        out uint pNeeded);

    HResult GetGCHeapDetails(
        CLRDATA_ADDRESS heap,
        out DacpGcHeapDetails details);

    HResult GetGCHeapStaticData(
        out DacpGcHeapDetails data);

    HResult GetHeapSegmentData(
        CLRDATA_ADDRESS seg,
        out DacpHeapSegmentData data);

    HResult GetOOMData(
        CLRDATA_ADDRESS oomAddr,
        out DacpOomData data);

    HResult GetOOMStaticData(
        out DacpOomData data);

    HResult GetHeapAnalyzeData(
        CLRDATA_ADDRESS addr,
        out DacpGcHeapAnalyzeData data);

    HResult GetHeapAnalyzeStaticData(
        out DacpGcHeapAnalyzeData data);

    HResult GetDomainLocalModuleData(
        CLRDATA_ADDRESS addr,
        out DacpDomainLocalModuleData data);

    HResult GetDomainLocalModuleDataFromAppDomain(
        CLRDATA_ADDRESS appDomainAddr,
        int moduleID,
        out DacpDomainLocalModuleData data);

    HResult GetDomainLocalModuleDataFromModule(
        CLRDATA_ADDRESS moduleAddr,
        out DacpDomainLocalModuleData data);

    HResult GetThreadLocalModuleData(
        CLRDATA_ADDRESS thread,
        uint index,
        out DacpThreadLocalModuleData data);

    HResult GetSyncBlockData(
        uint number,
        out DacpSyncBlockData data);

    HResult GetSyncBlockCleanupData(
        CLRDATA_ADDRESS addr,
        out DacpSyncBlockCleanupData data);

    HResult GetHandleEnum(
        out IntPtr ppHandleEnum);

    HResult GetHandleEnumForTypes(
        uint* types,
        uint count,
        out IntPtr ppHandleEnum);

    HResult GetHandleEnumForGC(
        uint gen,
        out IntPtr ppHandleEnum);

    HResult TraverseEHInfo(
        CLRDATA_ADDRESS ip,
        void* pCallback,
        IntPtr token);

    HResult GetNestedExceptionData(
        CLRDATA_ADDRESS exception,
        out CLRDATA_ADDRESS exceptionObject,
        out CLRDATA_ADDRESS nextNestedException);

    HResult GetStressLogAddress(
        out CLRDATA_ADDRESS stressLog);

    HResult TraverseLoaderHeap(
        CLRDATA_ADDRESS loaderHeapAddr,
        delegate* unmanaged[Stdcall]<CLRDATA_ADDRESS, nint, bool> pCallback);

    HResult GetCodeHeapList(
        CLRDATA_ADDRESS jitManager,
        uint count,
        void* codeHeaps,
        out uint pNeeded);

    HResult TraverseVirtCallStubHeap(
        CLRDATA_ADDRESS pAppDomain,
        VCSHeapType heaptype,
        delegate* unmanaged[Stdcall]<CLRDATA_ADDRESS, nint, bool> pCallback);

    HResult GetUsefulGlobals(
        out DacpUsefulGlobalsData data);

    HResult GetClrWatsonBuckets(
        CLRDATA_ADDRESS thread,
        void* pGenericModeBlock);

    HResult GetTLSIndex(
        out ULONG pIndex);

    HResult GetDacModuleHandle(
        out HMODULE phModule);

    HResult GetRCWData(
        CLRDATA_ADDRESS addr,
        out DacpRCWData data);

    HResult GetRCWInterfaces(
        CLRDATA_ADDRESS rcw,
        uint count,
        out DacpCOMInterfacePointerData interfaces,
        out uint pNeeded);

    HResult GetCCWData(
        CLRDATA_ADDRESS ccw,
        out DacpCCWData data);

    HResult GetCCWInterfaces(
        CLRDATA_ADDRESS ccw,
        uint count,
        DacpCOMInterfacePointerData* interfaces,
        out uint pNeeded);

    HResult TraverseRCWCleanupList(
        CLRDATA_ADDRESS cleanupListPtr,
        void* pCallback,
        IntPtr token);

    HResult GetStackReferences(
        /* [in] */ DWORD osThreadID,
        /* [out] */ out IntPtr ppEnum);

    HResult GetRegisterName(
        /* [in] */ int regName,
        /* [in] */ uint count,
        /* [out] */ WCHAR* buffer,
        /* [out] */ out uint pNeeded);

    HResult GetThreadAllocData(
        CLRDATA_ADDRESS thread,
        out DacpAllocData data);

    HResult GetHeapAllocData(
        uint count,
        DacpGenerationAllocData* data,
        out uint pNeeded);

    HResult GetFailedAssemblyList(
        CLRDATA_ADDRESS appDomain,
        int count,
        CLRDATA_ADDRESS* values,
        out uint pNeeded);

    HResult GetPrivateBinPaths(
        CLRDATA_ADDRESS appDomain,
        int count,
        WCHAR* paths,
        out uint pNeeded);

    HResult GetAssemblyLocation(
        CLRDATA_ADDRESS assembly,
        int count,
        WCHAR* location,
        out uint pNeeded);

    HResult GetAppDomainConfigFile(
        CLRDATA_ADDRESS appDomain,
        int count,
        WCHAR* configFile,
        out uint pNeeded);

    HResult GetApplicationBase(
        CLRDATA_ADDRESS appDomain,
        int count,
        WCHAR* applicationBase,
        out uint pNeeded);

    HResult GetFailedAssemblyData(
        CLRDATA_ADDRESS assembly,
        out uint pContext,
        out HResult pResult);

    HResult GetFailedAssemblyLocation(
        CLRDATA_ADDRESS assembly,
        uint count,
        WCHAR* location,
        out uint pNeeded);

    HResult GetFailedAssemblyDisplayName(
        CLRDATA_ADDRESS assembly,
        uint count,
        WCHAR* name,
        out uint pNeeded);
}