using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ManagedDotnetGC;

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

public enum enable_no_gc_region_callback_status
{
    succeed,
    not_started,
    insufficient_budget,
    already_registered,
};

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
[StructLayout(LayoutKind.Sequential)]
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

    // mapping table from region index to generation
    public byte* region_to_generation_table;

    // shift count - how many bits to shift right to obtain region index from address
    public byte region_shr;

    // whether to use the more precise but slower write barrier
    public bool region_use_bitwise_write_barrier;
}

// Different operations that can be done by GCToEEInterface::StompWriteBarrier
public enum WriteBarrierOp
{
    StompResize,
    StompEphemeral,
    Initialize,
    SwitchToWriteWatch,
    SwitchToNonWriteWatch
}
