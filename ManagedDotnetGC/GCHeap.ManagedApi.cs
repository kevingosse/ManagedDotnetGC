using ManagedDotnetGC.Api;
using static ManagedDotnetGC.Log;

namespace ManagedDotnetGC;

unsafe partial class GCHeap : IGc
{
    private NativeObjects.IGc _managedApiObject;

    // EE bracket notification counters, surfaced through IGc for GcCallbackBracketTest
    // (docs/missing-features.md item 2.1)
    private int _gcStartWorkCalls;
    private int _beforeGcScanRootsCalls;
    private int _afterGcScanRootsCalls;
    private int _gcDoneCalls;
    private int _bracketOrderViolations;
    private int _bracketState; // 0 = idle, 1 = started, 2 = before root scan, 3 = after root scan

    // The parameters passed to the EE on the most recent write barrier update, surfaced through
    // IGc for WriteBarrierParamsTest (docs/missing-features.md item 7.1)
    private WriteBarrierParameters _lastWriteBarrierParameters;

    private void InitializeManagedApi()
    {
        _managedApiObject = NativeObjects.IGc.Wrap(this);
    }

    public uint GetSyncBlockCacheCount()
    {
        _gcToClr.SuspendEE(SUSPEND_REASON.SUSPEND_FOR_GC_PREP);

        var count = _gcToClr.GetActiveSyncBlockCount();

        _gcToClr.RestartEE(finishedGC: false);

        return count;
    }

    public nint GetContainingObject(nint address)
    {
        try
        {
            // The bump-region walk is only safe on a walkable heap: outside a collection,
            // live alloc-context remainders are unplugged zeroes. Suspend and plug them
            // first, exactly like a real collection does.
            _gcToClr.SuspendEE(SUSPEND_REASON.SUSPEND_FOR_GC_PREP);

            try
            {
                FixAllocContexts();
                return (nint)GetContainingObject(address, fCollectedGenOnly: false);
            }
            finally
            {
                _gcToClr.RestartEE(finishedGC: false);
            }
        }
        catch (NotImplementedException)
        {
            return GcProbe.NotImplemented;
        }
        catch (Exception ex)
        {
            Write($"GetContainingObject probe threw: {ex.Message}");
            return GcProbe.Error;
        }
    }

    public int IsHeapPointer(nint address)
    {
        try
        {
            return IsHeapPointer(address, small_heap_only: false) ? GcProbe.True : GcProbe.False;
        }
        catch (NotImplementedException)
        {
            return GcProbe.NotImplemented;
        }
        catch (Exception ex)
        {
            Write($"IsHeapPointer probe threw: {ex.Message}");
            return GcProbe.Error;
        }
    }

    public int IsPromoted(nint address)
    {
        try
        {
            return IsPromoted((GCObject*)address) ? GcProbe.True : GcProbe.False;
        }
        catch (NotImplementedException)
        {
            return GcProbe.NotImplemented;
        }
        catch (Exception ex)
        {
            Write($"IsPromoted probe threw: {ex.Message}");
            return GcProbe.Error;
        }
    }

    public int GetGcCallbackCount(GcCallbackKind kind)
    {
        return kind switch
        {
            GcCallbackKind.GcStartWork => Volatile.Read(ref _gcStartWorkCalls),
            GcCallbackKind.BeforeGcScanRoots => Volatile.Read(ref _beforeGcScanRootsCalls),
            GcCallbackKind.AfterGcScanRoots => Volatile.Read(ref _afterGcScanRootsCalls),
            GcCallbackKind.GcDone => Volatile.Read(ref _gcDoneCalls),
            GcCallbackKind.OrderViolation => Volatile.Read(ref _bracketOrderViolations),
            _ => GcProbe.Error
        };
    }

    public ulong GetWriteBarrierParameter(WriteBarrierParameterKind kind)
    {
        var parameters = _lastWriteBarrierParameters;

        return kind switch
        {
            WriteBarrierParameterKind.CardTable => (ulong)parameters.card_table,
            WriteBarrierParameterKind.CardBundleTable => (ulong)parameters.card_bundle_table,
            WriteBarrierParameterKind.LowestAddress => (ulong)parameters.lowest_address,
            WriteBarrierParameterKind.HighestAddress => (ulong)parameters.highest_address,
            WriteBarrierParameterKind.EphemeralLow => (ulong)parameters.ephemeral_low,
            WriteBarrierParameterKind.EphemeralHigh => (ulong)parameters.ephemeral_high,
            _ => ulong.MaxValue
        };
    }

    // Records the parameters for GetWriteBarrierParameter. All write barrier updates sent to the
    // EE must go through this wrapper rather than calling _gcToClr.StompWriteBarrier directly.
    private void StompWriteBarrier(WriteBarrierParameters parameters)
    {
        _lastWriteBarrierParameters = parameters;
        _gcToClr.StompWriteBarrier(&parameters);
    }

    // The sanctioned call sites for the EE bracket notifications (docs/missing-features.md
    // item 2.1). When wiring them into the collection cycle, call these wrappers rather than
    // _gcToClr directly, so that GcCallbackBracketTest can observe the calls and their ordering.
    private void NotifyGcStartWork(int condemned, int maxGen)
    {
        RecordBracketCall(expectedState: 0, newState: 1, ref _gcStartWorkCalls);
        _gcToClr.GcStartWork(condemned, maxGen);
    }

    private void NotifyBeforeGcScanRoots(int condemned, bool isBgc, bool isConcurrent)
    {
        RecordBracketCall(expectedState: 1, newState: 2, ref _beforeGcScanRootsCalls);
        _gcToClr.BeforeGcScanRoots(condemned, isBgc, isConcurrent);
    }

    private void NotifyAfterGcScanRoots(int condemned, int maxGen, void* sc)
    {
        RecordBracketCall(expectedState: 2, newState: 3, ref _afterGcScanRootsCalls);
        _gcToClr.AfterGcScanRoots(condemned, maxGen, sc);
    }

    private void NotifyGcDone(int condemned)
    {
        RecordBracketCall(expectedState: 3, newState: 0, ref _gcDoneCalls);
        _gcToClr.GcDone(condemned);
    }

    private void RecordBracketCall(int expectedState, int newState, ref int counter)
    {
        if (_bracketState != expectedState)
        {
            Interlocked.Increment(ref _bracketOrderViolations);
        }

        _bracketState = newState;
        Interlocked.Increment(ref counter);
    }
}
