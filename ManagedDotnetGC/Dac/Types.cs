namespace ManagedDotnetGC.Dac;

using System.Diagnostics;

using ULONG64 = UInt64;
using ULONG = UInt32;
using LONG = Int32;
using UINT = UInt32;
using BOOL = Int32;
using DWORD = UInt32;
using WORD = Int16;

public readonly struct GcDacVars
{
    public readonly byte Major_version_number;
    public readonly byte Minor_version_number;
    public readonly nint Generation_size;
    public readonly nint Total_generation_count;
}

public unsafe struct VersionInfo
{
    public int MajorVersion;
    public int MinorVersion;
    public int BuildVersion;
    public char* Name;
}

// SUSPEND_REASON is the reason why the GC wishes to suspend the EE,
// used as an argument to IGCToCLR::SuspendEE.
public enum SUSPEND_REASON
{
    SUSPEND_FOR_GC = 1,
    SUSPEND_FOR_GC_PREP = 6
}

public readonly struct GCObject
{
    public readonly IntPtr MethodTable;
    public readonly int Length;
}

public unsafe struct gc_alloc_context
{
    public nint alloc_ptr;
    public nint alloc_limit;
    public long alloc_bytes; //Number of bytes allocated on SOH by this context

    public long alloc_bytes_uoh; //Number of bytes allocated not on SOH by this context

    // These two fields are deliberately not exposed past the EE-GC interface.
    public void* gc_reserved_1;
    public void* gc_reserved_2;
    public int alloc_count;
}

public enum walk_surv_type
{
    walk_for_gc = 1,
    walk_for_bgc = 2,
    walk_for_uoh = 3
}

public unsafe struct segment_info
{
    public void* pvMem; // base of the allocation, not the first object (must add ibFirstObject)
    public nint ibFirstObject;   // offset to the base of the first object in the segment
    public nint ibAllocated; // limit of allocated memory in the segment (>= firstobject)
    public nint ibCommit; // limit of committed memory in the segment (>= allocated)
    public nint ibReserved; // limit of reserved memory in the segment (>= commit)
}

// Event keywords corresponding to events that can be fired by the GC. These
// numbers come from the ETW manifest itself - please make changes to this enum
// if you add, remove, or change keyword sets that are used by the GC!
[Flags]
public enum GCEventKeyword
{
    GCEventKeyword_None = 0x0,
    GCEventKeyword_GC = 0x1,
    // Duplicate on purpose, GCPrivate is the same keyword as GC,
    // with a different provider
    GCEventKeyword_GCPrivate = 0x1,
    GCEventKeyword_GCHandle = 0x2,
    GCEventKeyword_GCHandlePrivate = 0x4000,
    GCEventKeyword_GCHeapDump = 0x100000,
    GCEventKeyword_GCSampledObjectAllocationHigh = 0x200000,
    GCEventKeyword_GCHeapSurvivalAndMovement = 0x400000,
    GCEventKeyword_GCHeapCollect = 0x800000,
    GCEventKeyword_GCHeapAndTypeNames = 0x1000000,
    GCEventKeyword_GCSampledObjectAllocationLow = 0x2000000,
    GCEventKeyword_All = GCEventKeyword_GC
                         | GCEventKeyword_GCPrivate
                         | GCEventKeyword_GCHandle
                         | GCEventKeyword_GCHandlePrivate
                         | GCEventKeyword_GCHeapDump
                         | GCEventKeyword_GCSampledObjectAllocationHigh
                         | GCEventKeyword_GCHeapSurvivalAndMovement
                         | GCEventKeyword_GCHeapCollect
                         | GCEventKeyword_GCHeapAndTypeNames
                         | GCEventKeyword_GCSampledObjectAllocationLow
}

// Event levels corresponding to events that can be fired by the GC.
public enum GCEventLevel
{
    GCEventLevel_None = 0,
    GCEventLevel_Fatal = 1,
    GCEventLevel_Error = 2,
    GCEventLevel_Warning = 3,
    GCEventLevel_Information = 4,
    GCEventLevel_Verbose = 5,
    GCEventLevel_Max = 6,
    GCEventLevel_LogAlways = 255
}

public unsafe struct OBJECTHANDLE
{
    public OBJECTHANDLE(nint value)
    {
        Value = value;
    }

    public nint Value;
}

public enum HandleType
{
    /*
     * WEAK HANDLES
     *
     * Weak handles are handles that track an object as long as it is alive,
     * but do not keep the object alive if there are no strong references to it.
     *
     */

    /*
     * SHORT-LIVED WEAK HANDLES
     *
     * Short-lived weak handles are weak handles that track an object until the
     * first time it is detected to be unreachable.  At this point, the handle is
     * severed, even if the object will be visible from a pending finalization
     * graph.  This further implies that short weak handles do not track
     * across object resurrections.
     *
     */
    HNDTYPE_WEAK_SHORT = 0,

    /*
     * LONG-LIVED WEAK HANDLES
     *
     * Long-lived weak handles are weak handles that track an object until the
     * object is actually reclaimed.  Unlike short weak handles, long weak handles
     * continue to track their referents through finalization and across any
     * resurrections that may occur.
     *
     */
    HNDTYPE_WEAK_LONG = 1,
    HNDTYPE_WEAK_DEFAULT = 1,

    /*
     * STRONG HANDLES
     *
     * Strong handles are handles which function like a normal object reference.
     * The existence of a strong handle for an object will cause the object to
     * be promoted (remain alive) through a garbage collection cycle.
     *
     */
    HNDTYPE_STRONG = 2,
    HNDTYPE_DEFAULT = 2,

    /*
     * PINNED HANDLES
     *
     * Pinned handles are strong handles which have the added property that they
     * prevent an object from moving during a garbage collection cycle.  This is
     * useful when passing a pointer to object innards out of the runtime while GC
     * may be enabled.
     *
     * NOTE:  PINNING AN OBJECT IS EXPENSIVE AS IT PREVENTS THE GC FROM ACHIEVING
     *        OPTIMAL PACKING OF OBJECTS DURING EPHEMERAL COLLECTIONS.  THIS TYPE
     *        OF HANDLE SHOULD BE USED SPARINGLY!
     */
    HNDTYPE_PINNED = 3,

    /*
     * VARIABLE HANDLES
     *
     * Variable handles are handles whose type can be changed dynamically.  They
     * are larger than other types of handles, and are scanned a little more often,
     * but are useful when the handle owner needs an efficient way to change the
     * strength of a handle on the fly.
     *
     */
    HNDTYPE_VARIABLE = 4,

    /*
     * REFCOUNTED HANDLES
     *
     * Refcounted handles are handles that behave as strong handles while the
     * refcount on them is greater than 0 and behave as weak handles otherwise.
     *
     * N.B. These are currently NOT general purpose.
     *      The implementation is tied to COM Interop.
     *
     */
    HNDTYPE_REFCOUNTED = 5,

    /*
     * DEPENDENT HANDLES
     *
     * Dependent handles are two handles that need to have the same lifetime.  One handle refers to a secondary object
     * that needs to have the same lifetime as the primary object. The secondary object should not cause the primary
     * object to be referenced, but as long as the primary object is alive, so must be the secondary
     *
     * They are currently used for EnC for adding new field members to existing instantiations under EnC modes where
     * the primary object is the original instantiation and the secondary represents the added field.
     *
     * They are also used to implement the managed ConditionalWeakTable class. If you want to use
     * these from managed code, they are exposed to BCL through the managed DependentHandle class.
     *
     *
     */
    HNDTYPE_DEPENDENT = 6,

    /*
     * PINNED HANDLES for asynchronous operation
     *
     * Pinned handles are strong handles which have the added property that they
     * prevent an object from moving during a garbage collection cycle.  This is
     * useful when passing a pointer to object innards out of the runtime while GC
     * may be enabled.
     *
     * NOTE:  PINNING AN OBJECT IS EXPENSIVE AS IT PREVENTS THE GC FROM ACHIEVING
     *        OPTIMAL PACKING OF OBJECTS DURING EPHEMERAL COLLECTIONS.  THIS TYPE
     *        OF HANDLE SHOULD BE USED SPARINGLY!
     */
    HNDTYPE_ASYNCPINNED = 7,

    /*
     * SIZEDREF HANDLES
     *
     * SizedRef handles are strong handles. Each handle has a piece of user data associated
     * with it that stores the size of the object this handle refers to. These handles
     * are scanned as strong roots during each GC but only during full GCs would the size
     * be calculated.
     *
     */
    HNDTYPE_SIZEDREF = 8,

    /*
     * NATIVE WEAK HANDLES
     *
     * Native weak reference handles hold two different types of weak handles to any
     * RCW with an underlying COM object that implements IWeakReferenceSource.  The
     * object reference itself is a short weak handle to the RCW.  In addition an
     * IWeakReference* to the underlying COM object is stored, allowing the handle
     * to create a new RCW if the existing RCW is collected.  This ensures that any
     * code holding onto a native weak reference can always access an RCW to the
     * underlying COM object as long as it has not been released by all of its strong
     * references.
     */
    HNDTYPE_WEAK_NATIVE_COM = 9
}

// Arguments to GCToEEInterface::StompWriteBarrier
public unsafe struct WriteBarrierParameters
{
    // The operation that StompWriteBarrier will perform.
    public WriteBarrierOp operation;

    // Whether or not the runtime is currently suspended. If it is not,
    // the EE will need to suspend it before bashing the write barrier.
    // Used for all operations.
    public bool is_runtime_suspended;

    // Whether or not the GC has moved the ephemeral generation to no longer
    // be at the top of the heap. When the ephemeral generation is at the top
    // of the heap, and the write barrier observes that a pointer is greater than
    // g_ephemeral_low, it does not need to check that the pointer is less than
    // g_ephemeral_high because there is nothing in the GC heap above the ephemeral
    // generation. When this is not the case, however, the GC must inform the EE
    // so that the EE can switch to a write barrier that checks that a pointer
    // is both greater than g_ephemeral_low and less than g_ephemeral_high.
    // Used for WriteBarrierOp::StompResize.
    public bool requires_upper_bounds_check;

    // The new card table location. May or may not be the same as the previous
    // card table. Used for WriteBarrierOp::Initialize and WriteBarrierOp::StompResize.
    public uint* card_table;

    // The new card bundle table location. May or may not be the same as the previous
    // card bundle table. Used for WriteBarrierOp::Initialize and WriteBarrierOp::StompResize.
    public uint* card_bundle_table;

    // The heap's new low boundary. May or may not be the same as the previous
    // value. Used for WriteBarrierOp::Initialize and WriteBarrierOp::StompResize.
    public byte* lowest_address;

    // The heap's new high boundary. May or may not be the same as the previous
    // value. Used for WriteBarrierOp::Initialize and WriteBarrierOp::StompResize.
    public byte* highest_address;

    // The new start of the ephemeral generation.
    // Used for WriteBarrierOp::StompEphemeral.
    public byte* ephemeral_low;

    // The new end of the ephemeral generation.
    // Used for WriteBarrierOp::StompEphemeral.
    public byte* ephemeral_high;

    // The new write watch table, if we are using our own write watch
    // implementation. Used for WriteBarrierOp::SwitchToWriteWatch only.
    public byte* write_watch_table;
};

// Different operations that can be done by GCToEEInterface::StompWriteBarrier
public enum WriteBarrierOp
{
    StompResize,
    StompEphemeral,
    Initialize,
    SwitchToWriteWatch,
    SwitchToNonWriteWatch
};

/// <summary>
/// A representation of CLR's CLRDATA_ADDRESS, which is a signed 64bit integer.
/// Unfortunately this can cause issues when inspecting 32bit processes, since
/// if the highest bit is set the value will be sign-extended.  This struct is
/// meant to
/// </summary>
[DebuggerDisplay("{AsUInt64()}")]
public readonly struct CLRDATA_ADDRESS
{
    /// <summary>
    /// Gets raw value of this address.  May be sign-extended if inspecting a 32bit process.
    /// </summary>
    public long Value { get; }

    /// <summary>Creates an instance of CLRDATA_ADDRESS.</summary>
    /// <param name="value"></param>
    public CLRDATA_ADDRESS(long value) => this.Value = value;

    /// <summary>
    /// Returns the value of this address and un-sign extends the value if appropriate.
    /// </summary>
    /// <param name="cda">The address to convert.</param>
    public static implicit operator ulong(CLRDATA_ADDRESS cda) => cda.AsUInt64();

    public static implicit operator CLRDATA_ADDRESS(ulong value) => new(unchecked((nint)value));

    public override string ToString() => AsUInt64().ToString("x2");

    /// <summary>
    /// Returns the value of this address and un-sign extends the value if appropriate.
    /// </summary>
    /// <returns>The value of this address and un-sign extends the value if appropriate.</returns>
    private ulong AsUInt64() => unchecked((nuint)Value);
}

public enum CorDebugPlatform : uint
{
    CORDB_PLATFORM_WINDOWS_X86 = 0,
    CORDB_PLATFORM_WINDOWS_AMD64 = (CORDB_PLATFORM_WINDOWS_X86 + 1),
    CORDB_PLATFORM_WINDOWS_IA64 = (CORDB_PLATFORM_WINDOWS_AMD64 + 1),
    CORDB_PLATFORM_MAC_PPC = (CORDB_PLATFORM_WINDOWS_IA64 + 1),
    CORDB_PLATFORM_MAC_X86 = (CORDB_PLATFORM_MAC_PPC + 1),
    CORDB_PLATFORM_WINDOWS_ARM = (CORDB_PLATFORM_MAC_X86 + 1),
    CORDB_PLATFORM_MAC_AMD64 = (CORDB_PLATFORM_WINDOWS_ARM + 1),
    CORDB_PLATFORM_WINDOWS_ARM64 = (CORDB_PLATFORM_MAC_AMD64 + 1),
    CORDB_PLATFORM_POSIX_AMD64 = (CORDB_PLATFORM_WINDOWS_ARM64 + 1),
    CORDB_PLATFORM_POSIX_X86 = (CORDB_PLATFORM_POSIX_AMD64 + 1),
    CORDB_PLATFORM_POSIX_ARM = (CORDB_PLATFORM_POSIX_X86 + 1),
    CORDB_PLATFORM_POSIX_ARM64 = (CORDB_PLATFORM_POSIX_ARM + 1)
}

public struct DacpThreadStoreData
{
    public int threadCount;
    public int unstartedThreadCount;
    public int backgroundThreadCount;
    public int pendingThreadCount;
    public int deadThreadCount;
    public CLRDATA_ADDRESS firstThread;
    public CLRDATA_ADDRESS finalizerThread;
    public CLRDATA_ADDRESS gcThread;
    public int fHostConfig;          // Uses hosting flags defined above
}

public struct DacpAppDomainStoreData
{
    public CLRDATA_ADDRESS sharedDomain;
    public CLRDATA_ADDRESS systemDomain;
    public int DomainCount;
}

public struct DacpCOMInterfacePointerData
{
    public CLRDATA_ADDRESS methodTable;
    public CLRDATA_ADDRESS interfacePtr;
    public CLRDATA_ADDRESS comContext;
}

public enum DacpAppDomainDataStage
{
    STAGE_CREATING,
    STAGE_READYFORMANAGEDCODE,
    STAGE_ACTIVE,
    STAGE_OPEN,
    STAGE_UNLOAD_REQUESTED,
    STAGE_EXITING,
    STAGE_EXITED,
    STAGE_FINALIZING,
    STAGE_FINALIZED,
    STAGE_HANDLETABLE_NOACCESS,
    STAGE_CLEARED,
    STAGE_COLLECTED,
    STAGE_CLOSED
}

// Information about a BaseDomain (AppDomain, SharedDomain or SystemDomain).
// For types other than AppDomain, some fields (like dwID, DomainLocalBlock, etc.) will be 0/null.
public struct DacpAppDomainData
{
    // The pointer to the BaseDomain (not necessarily an AppDomain).
    // It's useful to keep this around in the structure
    public CLRDATA_ADDRESS AppDomainPtr;
    public CLRDATA_ADDRESS AppSecDesc;
    public CLRDATA_ADDRESS pLowFrequencyHeap;
    public CLRDATA_ADDRESS pHighFrequencyHeap;
    public CLRDATA_ADDRESS pStubHeap;
    public CLRDATA_ADDRESS DomainLocalBlock;
    public CLRDATA_ADDRESS pDomainLocalModules;
    // The creation sequence number of this app domain (starting from 1)
    public int dwId;
    public int AssemblyCount;
    public int FailedAssemblyCount;
    public DacpAppDomainDataStage appDomainStage;
}

public struct DacpAssemblyData
{
    public CLRDATA_ADDRESS AssemblyPtr; //useful to have
    public CLRDATA_ADDRESS ClassLoader;
    public CLRDATA_ADDRESS ParentDomain;
    public CLRDATA_ADDRESS BaseDomainPtr;
    public CLRDATA_ADDRESS AssemblySecDesc;
    public bool isDynamic;
    public uint ModuleCount;
    public uint LoadContext;
    public bool isDomainNeutral; // Always false, preserved for backward compatibility
    public int dwLocationFlags;
}

public struct DacpThreadData
{
    public int corThreadId;
    public int osThreadId;
    public int state;
    public ulong preemptiveGCDisabled;
    public CLRDATA_ADDRESS allocContextPtr;
    public CLRDATA_ADDRESS allocContextLimit;
    public CLRDATA_ADDRESS context;
    public CLRDATA_ADDRESS domain;
    public CLRDATA_ADDRESS pFrame;
    public int lockCount;
    public CLRDATA_ADDRESS firstNestedException; // Pass this pointer to DacpNestedExceptionInfo
    public CLRDATA_ADDRESS teb;
    public CLRDATA_ADDRESS fiberData;
    public CLRDATA_ADDRESS lastThrownObjectHandle;
    public CLRDATA_ADDRESS nextThread;
}

public struct DacpModuleData
{
    public CLRDATA_ADDRESS Address;
    public CLRDATA_ADDRESS File; // A PEFile addr
    public CLRDATA_ADDRESS ilBase;
    public CLRDATA_ADDRESS metadataStart;
    public ULONG64 metadataSize;
    public CLRDATA_ADDRESS Assembly; // Assembly pointer
    public BOOL bIsReflection;
    public BOOL bIsPEFile;
    public ULONG64 dwBaseClassIndex;
    public ULONG64 dwModuleID;

    public DWORD dwTransientFlags;

    public CLRDATA_ADDRESS TypeDefToMethodTableMap;
    public CLRDATA_ADDRESS TypeRefToMethodTableMap;
    public CLRDATA_ADDRESS MethodDefToDescMap;
    public CLRDATA_ADDRESS FieldDefToDescMap;
    public CLRDATA_ADDRESS MemberRefToDescMap;
    public CLRDATA_ADDRESS FileReferencesMap;
    public CLRDATA_ADDRESS ManifestModuleReferencesMap;

    CLRDATA_ADDRESS pLookupTableHeap;
    CLRDATA_ADDRESS pThunkHeap;

    public ULONG64 dwModuleIndex;
}

public enum ModuleMapType { TYPEDEFTOMETHODTABLE, TYPEREFTOMETHODTABLE }


public struct DacpMethodDescData
{
    public BOOL bHasNativeCode;
    public BOOL bIsDynamic;
    public WORD wSlotNumber;
    public CLRDATA_ADDRESS NativeCodeAddr;
    // Useful for breaking when a method is jitted.
    public CLRDATA_ADDRESS AddressOfNativeCodeSlot;

    public CLRDATA_ADDRESS MethodDescPtr;
    public CLRDATA_ADDRESS MethodTablePtr;
    public CLRDATA_ADDRESS ModulePtr;

    public MdToken MDToken;
    public CLRDATA_ADDRESS GCInfo;
    public CLRDATA_ADDRESS GCStressCodeCopy;

    // This is only valid if bIsDynamic is true
    public CLRDATA_ADDRESS managedDynamicMethodObject;

    public CLRDATA_ADDRESS requestedIP;

    // Gives info for the single currently active version of a method
    public DacpReJitData rejitDataCurrent;

    // Gives info corresponding to requestedIP (for !ip2md)
    public DacpReJitData rejitDataRequested;

    // Total number of rejit versions that have been jitted
    public ULONG cJittedRejitVersions;
}

public struct MdToken
{
    public int Value;
}

public struct DacpReJitData
{
    public enum Flags
    {
        kUnknown,
        kRequested,
        kActive,
        kReverted,
    };

    public CLRDATA_ADDRESS rejitID;
    public Flags flags;
    public CLRDATA_ADDRESS NativeCodeAddr;
}

public struct DacpMethodDescTransparencyData
{
    public BOOL bHasCriticalTransparentInfo;
    public BOOL bIsCritical;
    public BOOL bIsTreatAsSafe;
}

public enum JITTypes { TYPE_UNKNOWN = 0, TYPE_JIT, TYPE_PJIT };

public struct DacpCodeHeaderData
{
    public CLRDATA_ADDRESS GCInfo;
    public JITTypes JITType;
    public CLRDATA_ADDRESS MethodDescPtr;
    public CLRDATA_ADDRESS MethodStart;
    public DWORD MethodSize;
    public CLRDATA_ADDRESS ColdRegionStart;
    public DWORD ColdRegionSize;
    public DWORD HotRegionSize;
}

public struct DacpJitManagerInfo
{
    public CLRDATA_ADDRESS managerAddr;
    public DWORD codeType; // for union below
    public CLRDATA_ADDRESS ptrHeapList;    // A HeapList * if IsMiIL(codeType)
}

public struct T_CONTEXT
{
    public int Value;
}

public struct DacpThreadpoolData
{
    public LONG cpuUtilization;
    public int NumIdleWorkerThreads;
    public int NumWorkingWorkerThreads;
    public int NumRetiredWorkerThreads;
    public LONG MinLimitTotalWorkerThreads;
    public LONG MaxLimitTotalWorkerThreads;

    public CLRDATA_ADDRESS FirstUnmanagedWorkRequest;

    public CLRDATA_ADDRESS HillClimbingLog;
    public int HillClimbingLogFirstIndex;
    public int HillClimbingLogSize;

    public DWORD NumTimers;

    public LONG NumCPThreads;
    public LONG NumFreeCPThreads;
    public LONG MaxFreeCPThreads;
    public LONG NumRetiredCPThreads;
    public LONG MaxLimitTotalCPThreads;
    public LONG CurrentLimitTotalCPThreads;
    public LONG MinLimitTotalCPThreads;

    public CLRDATA_ADDRESS AsyncTimerCallbackCompletionFPtr;
}

public struct DacpGenerationData
{
    public CLRDATA_ADDRESS start_segment;
    public CLRDATA_ADDRESS allocation_start;

    // These are examined only for generation 0, otherwise NULL
    public CLRDATA_ADDRESS allocContextPtr;
    public CLRDATA_ADDRESS allocContextLimit;
}

public struct DacpWorkRequestData
{
    public CLRDATA_ADDRESS Function;
    public CLRDATA_ADDRESS Context;
    public CLRDATA_ADDRESS NextWorkRequest;
}

public struct DacpHillClimbingLogEntry
{
    public DWORD TickCount;
    public int Transition;
    public int NewControlSetting;
    public int LastHistoryCount;
    public double LastHistoryMean;
}

public enum DacpObjectType { OBJ_STRING = 0, OBJ_FREE, OBJ_OBJECT, OBJ_ARRAY, OBJ_OTHER };

public readonly struct CorElementType
{
    public readonly uint Value;
}

public struct DacpObjectData
{
    public CLRDATA_ADDRESS MethodTable;
    public DacpObjectType ObjectType;
    public ULONG64 Size;
    public CLRDATA_ADDRESS ElementTypeHandle;
    public CorElementType ElementType;
    public DWORD dwRank;
    public ULONG64 dwNumComponents;
    public ULONG64 dwComponentSize;
    public CLRDATA_ADDRESS ArrayDataPtr;
    public CLRDATA_ADDRESS ArrayBoundsPtr;
    public CLRDATA_ADDRESS ArrayLowerBoundsPtr;

    public CLRDATA_ADDRESS RCW;
    public CLRDATA_ADDRESS CCW;
}

public struct DacpMethodTableData
{
    public BOOL bIsFree; // everything else is NULL if this is true.
    public CLRDATA_ADDRESS Module;
    public CLRDATA_ADDRESS Class;
    public CLRDATA_ADDRESS ParentMethodTable;
    public WORD wNumInterfaces;
    public WORD wNumMethods;
    public WORD wNumVtableSlots;
    public WORD wNumVirtuals;
    public DWORD BaseSize;
    public DWORD ComponentSize;
    public MdTypeDef cl; // Metadata token
    public DWORD dwAttrClass; // cached metadata
    public BOOL bIsShared;  // Always false, preserved for backward compatibility
    public BOOL bIsDynamic;
    public BOOL bContainsPointers;
}

public readonly struct MdTypeDef
{
    public readonly int Value;
}

public struct DacpMethodTableFieldData
{
    public WORD wNumInstanceFields;
    public WORD wNumStaticFields;
    public WORD wNumThreadStaticFields;

    public CLRDATA_ADDRESS FirstField; // If non-null, you can retrieve more

    public WORD wContextStaticOffset;
    public WORD wContextStaticsSize;
}

public struct DacpMethodTableTransparencyData
{
    public BOOL bHasCriticalTransparentInfo;
    public BOOL bIsCritical;
    public BOOL bIsTreatAsSafe;
}

public struct DacpFieldDescData
{
    public CorElementType Type;
    public CorElementType sigType;     // ELEMENT_TYPE_XXX from signature. We need this to disply pretty name for String in minidump's case
    public CLRDATA_ADDRESS MTOfType; // NULL if Type is not loaded

    public CLRDATA_ADDRESS ModuleOfType;
    public MdTypeDef TokenOfType;

    public MdFieldDef mb;
    public CLRDATA_ADDRESS MTOfEnclosingClass;
    public DWORD dwOffset;
    public BOOL bIsThreadLocal;
    public BOOL bIsContextLocal;
    public BOOL bIsStatic;
    public CLRDATA_ADDRESS NextField;
}

public readonly struct MdFieldDef
{
    public readonly int Value;
}

public struct DacpGcHeapData
{
    public BOOL bServerMode;
    public BOOL bGcStructuresValid;
    public UINT HeapCount;
    public UINT g_max_generation;
}

public struct DacpGcHeapDetails
{
    public const int DAC_NUMBERGENERATIONS = 4;
    public CLRDATA_ADDRESS heapAddr; // Only filled in in server mode, otherwise NULL
    public CLRDATA_ADDRESS alloc_allocated;

    public CLRDATA_ADDRESS mark_array;
    public CLRDATA_ADDRESS current_c_gc_state;
    public CLRDATA_ADDRESS next_sweep_obj;
    public CLRDATA_ADDRESS saved_sweep_ephemeral_seg;
    public CLRDATA_ADDRESS saved_sweep_ephemeral_start;
    public CLRDATA_ADDRESS background_saved_lowest_address;
    public CLRDATA_ADDRESS background_saved_highest_address;

    public DacpGenerationData generation_table1;
    public DacpGenerationData generation_table2;
    public DacpGenerationData generation_table3;
    public DacpGenerationData generation_table4;
    public CLRDATA_ADDRESS ephemeral_heap_segment;
    public CLRDATA_ADDRESS finalization_fill_pointers1;
    public CLRDATA_ADDRESS finalization_fill_pointers2;
    public CLRDATA_ADDRESS finalization_fill_pointers3;
    public CLRDATA_ADDRESS finalization_fill_pointers4;
    public CLRDATA_ADDRESS finalization_fill_pointers5;
    public CLRDATA_ADDRESS finalization_fill_pointers6;
    public CLRDATA_ADDRESS finalization_fill_pointers7;
    public CLRDATA_ADDRESS lowest_address;
    public CLRDATA_ADDRESS highest_address;
    public CLRDATA_ADDRESS card_table;
}

public struct DacpHeapSegmentData
{
    public CLRDATA_ADDRESS segmentAddr;
    public CLRDATA_ADDRESS allocated;
    public CLRDATA_ADDRESS committed;
    public CLRDATA_ADDRESS reserved;
    public CLRDATA_ADDRESS used;
    public CLRDATA_ADDRESS mem;
    // pass this to request if non-null to get the next segments.
    public CLRDATA_ADDRESS next;
    public CLRDATA_ADDRESS gc_heap; // only filled in in server mode, otherwise NULL
    // computed field: if this is the ephemeral segment highMark includes the ephemeral generation
    public CLRDATA_ADDRESS highAllocMark;

    public nint flags;
    public CLRDATA_ADDRESS background_allocated;
}

public struct DacpOomData
{
    public int reason;
    public ULONG64 alloc_size;
    public ULONG64 available_pagefile_mb;
    public ULONG64 gc_index;
    public int fgm;
    public ULONG64 size;
    public BOOL loh_p;
}

public struct DacpGcHeapAnalyzeData
{
    public CLRDATA_ADDRESS heapAddr; // Only filled in in server mode, otherwise NULL

    public CLRDATA_ADDRESS internal_root_array;
    public ULONG64 internal_root_array_index;
    public BOOL heap_analyze_success;
}

public struct DacpSyncBlockData
{
    public CLRDATA_ADDRESS Object;
    public BOOL bFree; // if set, no other fields are useful

    // fields below provide data from this, so it's just for display
    public CLRDATA_ADDRESS SyncBlockPointer;
    public DWORD COMFlags;
    public UINT MonitorHeld;
    public UINT Recursion;
    public CLRDATA_ADDRESS HoldingThread;
    public UINT AdditionalThreadCount;
    public CLRDATA_ADDRESS appDomainPtr;

    // SyncBlockCount will always be filled in with the number of SyncBlocks.
    // SyncBlocks may be requested from [1,SyncBlockCount]
    public UINT SyncBlockCount;
}

public struct DacpDomainLocalModuleData
{
    // These two parameters are used as input params when calling the
    // no-argument form of Request below.
    public CLRDATA_ADDRESS appDomainAddr;
    public ULONG64 ModuleID;

    public CLRDATA_ADDRESS pClassData;
    public CLRDATA_ADDRESS pDynamicClassTable;
    public CLRDATA_ADDRESS pGCStaticDataStart;
    public CLRDATA_ADDRESS pNonGCStaticDataStart;
}

public struct DacpThreadLocalModuleData
{
    // These two parameters are used as input params when calling the
    // no-argument form of Request below.
    public CLRDATA_ADDRESS threadAddr;
    public ULONG64 ModuleIndex;

    public CLRDATA_ADDRESS pClassData;
    public CLRDATA_ADDRESS pDynamicClassTable;
    public CLRDATA_ADDRESS pGCStaticDataStart;
    public CLRDATA_ADDRESS pNonGCStaticDataStart;
}

public struct DacpSyncBlockCleanupData
{
    public CLRDATA_ADDRESS SyncBlockPointer;

    public CLRDATA_ADDRESS nextSyncBlock;
    public CLRDATA_ADDRESS blockRCW;
    public CLRDATA_ADDRESS blockClassFactory;
    public CLRDATA_ADDRESS blockCCW;
}

public enum VCSHeapType { IndcellHeap, LookupHeap, ResolveHeap, DispatchHeap, CacheEntryHeap }

public struct DacpUsefulGlobalsData
{
    public CLRDATA_ADDRESS ArrayMethodTable;
    public CLRDATA_ADDRESS StringMethodTable;
    public CLRDATA_ADDRESS ObjectMethodTable;
    public CLRDATA_ADDRESS ExceptionMethodTable;
    public CLRDATA_ADDRESS FreeMethodTable;
}

public readonly struct HMODULE
{
    public readonly nint Value;
}

public struct DacpRCWData
{
    public CLRDATA_ADDRESS identityPointer;
    public CLRDATA_ADDRESS unknownPointer;
    public CLRDATA_ADDRESS managedObject;
    public CLRDATA_ADDRESS jupiterObject;
    public CLRDATA_ADDRESS vtablePtr;
    public CLRDATA_ADDRESS creatorThread;
    public CLRDATA_ADDRESS ctxCookie;

    public LONG refCount;
    public LONG interfaceCount;

    public BOOL isJupiterObject;
    public BOOL supportsIInspectable;
    public BOOL isAggregated;
    public BOOL isContained;
    public BOOL isFreeThreaded;
    public BOOL isDisconnected;
}

public struct DacpCCWData
{
    public CLRDATA_ADDRESS outerIUnknown;
    public CLRDATA_ADDRESS managedObject;
    public CLRDATA_ADDRESS handle;
    public CLRDATA_ADDRESS ccwAddress;

    public LONG refCount;
    public LONG interfaceCount;
    public BOOL isNeutered;

    public LONG jupiterRefCount;
    public BOOL isPegged;
    public BOOL isGlobalPegged;
    public BOOL hasStrongRef;
    public BOOL isExtendsCOMObject;
    public BOOL isAggregated;
}

public struct DacpAllocData
{
    public CLRDATA_ADDRESS allocBytes;
    public CLRDATA_ADDRESS allocBytesLoh;
};

public struct DacpGenerationAllocData
{
    public DacpAllocData allocData1;
    public DacpAllocData allocData2;
    public DacpAllocData allocData3;
    public DacpAllocData allocData4;
}