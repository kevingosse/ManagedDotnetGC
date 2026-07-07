using System.Runtime.InteropServices;
using static ManagedDotnetGC.Log;

namespace ManagedDotnetGC;

unsafe partial class GCHeap
{
    private void MarkPhase(bool young)
    {
        // TODO: Check what need to be set on ScanContext
        ScanContext scanContext = default;
        scanContext.promotion = true;
        scanContext._unused1 = GCHandle.ToIntPtr(_handle);

        var condemned = young ? 0 : 2;

        NotifyBeforeGcScanRoots(condemned, isBgc: false, isConcurrent: false);

        // Full marks trace the whole graph from a handful of fat entry points, so instead
        // of draining inline per root (fine for young marks, whose tracing is mostly done
        // by the parallel card scan) the strong roots accumulate on the GC thread's stack
        // and the worker pool traces them together, sharing excess through the share queue.
        var buffered = !young && _workerPool is not null;
        _bufferMarkRoots = buffered;

        var t0 = GcStats.Timestamp();

        Write("Scan roots");

        if (_workerPool is not null)
        {
            // Partitioned stack scan (M8.2, stock's ScanContext.thread_number scheme):
            // participants call GcScanRoots concurrently and each stack-scans the
            // round-robin share IsThreadUsingAllocationContextHeap deals it. Young marks
            // trace their share in the same fan-out; buffered full marks leave the
            // stacks filled for ParallelDrainMark.
            PartitionedScanRoots(condemned, drain: young);
        }
        else
        {
            var scanRootsCallback = (delegate* unmanaged<GCObject**, ScanContext*, uint, void>)&ScanRootsCallback;
            _gcToClr.GcScanRoots((IntPtr)scanRootsCallback, condemned, 2, &scanContext);
        }

        var t1 = GcStats.Timestamp();

        if (young)
        {
            // The remembered set: references from sticky-marked old objects into the young
            // generation are only reachable through dirty cards (SPEC-M4)
            ScanCards();
        }

        var t2 = GcStats.Timestamp();

        // Objects queued for the finalizer thread by earlier collections are strong roots
        // from the start of the mark phase — before handle scanning and weak clearing, like
        // the stock order (mark_phase.cpp:3176; missing-features 3.3)
        MarkFReachableQueues();

        var t3 = GcStats.Timestamp();

        ScanHandles();
        ScanRefCountedHandles();

        long drainTicks = 0;

        if (buffered)
        {
            _bufferMarkRoots = false;

            var tDrain = GcStats.Timestamp();
            ParallelDrainMark();
            drainTicks = GcStats.Timestamp() - tDrain;
        }

        var t4 = GcStats.Timestamp();

        MarkTail(condemned, &scanContext);

        if (GcStats.Enabled)
        {
            // The parallel drain traces what root/handle enumeration buffered, so it counts
            // as root marking even though it runs after the handle scans
            GcStats.RootsTicks = t1 - t0 + drainTicks;
            GcStats.CardScanTicks = t2 - t1;
            GcStats.FReachableTicks = t3 - t2;
            GcStats.HandleTicks = t4 - t3 - drainTicks;
        }
    }

    /// <summary>
    /// The mark-dependent EE protocol that must run after the strong closure is
    /// complete, on final marks: dependent-handle fixpoint, RCW/ComWrappers detach,
    /// weak clearing, finalization promotion. Shared verbatim between the inline STW
    /// mark and pause B of a concurrent cycle (SPEC-M6 v2 §5.4) — the order is
    /// load-bearing (stock mark_phase.cpp:3385).
    /// </summary>
    private void MarkTail(int condemned, ScanContext* scanContext)
    {
        var t0 = GcStats.Timestamp();

        ScanDependentHandles();

        var t1 = GcStats.Timestamp();

        // After all strong marking, before weak clearing: the EE detaches unmarked
        // RCWs / ComWrappers here, consulting our IsPromoted (2.1)
        NotifyAfterGcScanRoots(condemned, 2, scanContext);

        var t2 = GcStats.Timestamp();

        ClearHandles([HandleType.HNDTYPE_WEAK_SHORT]);
        ScanForFinalization();
        ScanDependentHandles();
        ClearHandles([HandleType.HNDTYPE_WEAK_LONG, HandleType.HNDTYPE_DEPENDENT, HandleType.HNDTYPE_WEAK_INTERIOR_POINTER, HandleType.HNDTYPE_REFCOUNTED]);

        var weakPtrScanCallback = (delegate* unmanaged<GCObject**, nint, nint, nint, void>)&WeakPtrScanCallback;
        _gcToClr.SyncBlockCacheWeakPtrScan(weakPtrScanCallback, GCHandle.ToIntPtr(_handle), 0);

        if (GcStats.Enabled)
        {
            var t3 = GcStats.Timestamp();
            GcStats.DependentTicks = t1 - t0;
            GcStats.AfterScanTicks = t2 - t1;
            GcStats.WeakTicks = t3 - t2;
        }
    }

    [UnmanagedCallersOnly]
    private static void WeakPtrScanCallback(GCObject** obj, nint extraInfo, nint lp1, nint lp2)
    {
        var gcHeap = s_instance;

        var o = *obj;

        if (!gcHeap._nativeAllocator.IsInRange((nint)o)) // Ignore objects in frozen segments as they don't get marked
        {
            return;
        }

        if (!o->IsMarked())
        {
            *obj = null;
        }
    }

    [UnmanagedCallersOnly]
    private static void ScanRootsCallback(GCObject** obj, ScanContext* context, uint flags)
    {
        s_instance.ScanRoots(*obj, context, (GcCallFlags)flags);
    }

    private void ScanForFinalization()
    {
        PrepareForFinalization();

        // Mark the newly dead objects just moved to the f-reachable queues (resurrection);
        // the leftovers from previous collections were already marked at the top of the
        // mark phase, so re-walking them here is a cheap no-op
        MarkFReachableQueues();
    }

    private void MarkFReachableQueues()
    {
        ScanContext scanContext = default;

        foreach (GCObject* obj in _freachableQueue)
        {
            ScanRoots(obj, &scanContext, default);
        }

        foreach (GCObject* obj in _criticalFreachableQueue)
        {
            ScanRoots(obj, &scanContext, default);
        }
    }

    private void ScanHandles()
    {
        ScanContext scanContext = default;

        foreach (var handle in _gcHandleManager.Store.EnumerateHandlesOfType([HandleType.HNDTYPE_STRONG, HandleType.HNDTYPE_PINNED]))
        {
            var obj = handle->Object;
            if (obj != null)
            {
                ScanRoots(obj, &scanContext, default);
            }
        }
    }

    /// <summary>
    /// Ref-counted handles (one per CCW / ComWrappers wrapper): the EE decides liveness —
    /// promote iff the wrapper is still referenced from native code (missing-features 3.2).
    /// Like stock PromoteRefCounted, the callback runs for every handle, marked target or
    /// not, so EE-side bookkeeping always happens. Dead ones are nulled with the long-weak
    /// group after the finalization scan.
    /// </summary>
    private void ScanRefCountedHandles()
    {
        ScanContext scanContext = default;

        foreach (var handle in _gcHandleManager.Store.EnumerateHandlesOfType([HandleType.HNDTYPE_REFCOUNTED]))
        {
            var obj = handle->Object;

            if (obj != null && _gcToClr.RefCountedHandleCallbacks(obj))
            {
                ScanRoots(obj, &scanContext, default);
            }
        }
    }

    private void ScanDependentHandles()
    {
        bool markedObjects;
        ScanContext scanContext = default;

        do
        {
            markedObjects = false;

            foreach (var handle in _gcHandleManager.Store.EnumerateHandlesOfType([HandleType.HNDTYPE_DEPENDENT]))
            {
                // Target: primary
                // Dependent: secondary
                var primary = handle->Object;
                var secondary = (GCObject*)handle->ExtraInfo;

                if (primary == null || secondary == null)
                {
                    continue;
                }

                // Anything outside the GC heap (frozen segments) counts as always-alive, and
                // an out-of-range secondary must never count as "needs promotion" or the
                // fixpoint spins forever (missing-features 3.4; stock interface.cpp:783).
                // Range check first: out-of-range addresses must not index the mark bitmap.
                var primaryAlive = !_nativeAllocator.IsInRange((nint)primary) || primary->IsMarked();
                var secondaryNeedsMark = _nativeAllocator.IsInRange((nint)secondary) && !secondary->IsMarked();

                if (primaryAlive && secondaryNeedsMark)
                {
                    ScanRoots(secondary, &scanContext, default);
                    markedObjects = true;
                }
            }
        }
        while (markedObjects);
    }

    private void ScanRoots(GCObject* root, ScanContext* context, GcCallFlags flags)
    {
        if ((IntPtr)root == 0)
        {
            return;
        }

        if (!_nativeAllocator.IsInRange((IntPtr)root))
        {
            return;
        }

        if (flags.HasFlag(GcCallFlags.GC_CALL_INTERIOR))
        {
            root = ResolveInteriorPointer((nint)root);

            if (root == null)
            {
                return;
            }
        }

        if (_skipMarkedRootBuffering && root->IsMarked())
        {
            // Remark buffer-time mark skip (SPEC-M6 §9): marked here means fully traced
            // by the window drain; window-era stores are the card remark's job
            return;
        }

        if (_partitionedRootScan)
        {
            // M8.2: the context is the scanning participant's own, so the push is
            // single-threaded per stack; the trace runs in the same fan-out (young)
            // or in ParallelDrainMark (buffered full marks)
            _cardScanStacks![context->thread_number].Push((nint)root);
            return;
        }

        _markStack.Push((nint)root);

        if (!_bufferMarkRoots)
        {
            DrainMarkStack(_markStack, deferredCollectible: null);
        }
    }

    /// <summary>
    /// Runs GcScanRoots once per participant, concurrently (M8.2). The EE walks its full
    /// thread list for every call and consults IsThreadUsingAllocationContextHeap per
    /// thread, so each participant stack-scans the share dealt to its thread_number —
    /// stock server GC's partitioning scheme (gcenv.ee.cpp GcScanRoots). Worker threads
    /// are foreign to the target EE exactly like stock's server GC threads (raw utility
    /// threads with no EE Thread object; ScanStackRoots' precondition explicitly admits
    /// scanners the EE has never seen), and this is the one sanctioned exception to the
    /// workers-never-call-the-EE rule. The collecting thread's own stack may land on a
    /// worker: it stays preemptive for the whole collection, so its managed frames sit
    /// frozen below its transition frame like any blocked mutator's.
    ///
    /// With <paramref name="drain"/> (young marks) participants trace their shares in
    /// the same fan-out, balancing tails through the share queue, and the deferred
    /// collectible edges run on the GC thread afterwards. Without it (buffered full
    /// marks) the roots wait on the participant stacks for ParallelDrainMark.
    /// </summary>
    private void PartitionedScanRoots(int condemned, bool drain)
    {
        var stacks = _cardScanStacks!;
        var share = _markShareQueue!;
        var participants = stacks.Length;

        Array.Clear(_rootScanCursors!);
        Array.Clear(_rootScanClaims!);
        _rootScanParticipants = participants;
        _partitionedRootScan = true;

        var scanRootsCallback = (IntPtr)(delegate* unmanaged<GCObject**, ScanContext*, uint, void>)&ScanRootsCallback;
        var workerId = -1;
        var idle = 0;

        _workerPool!.Run(() =>
        {
            var id = Interlocked.Increment(ref workerId);

            ScanContext scanContext = default;
            scanContext.promotion = true;
            scanContext.thread_number = id;
            scanContext.thread_count = participants;
            scanContext._unused1 = GCHandle.ToIntPtr(_handle);

            _gcToClr.GcScanRoots(scanRootsCallback, condemned, 2, &scanContext);

            if (drain)
            {
                DrainWithSharing(stacks[id], _cardScanDeferred![id], share, ref idle, participants);
            }
        });

        _partitionedRootScan = false;
        _rootScanParticipants = 1;

        if (drain)
        {
            ProcessDeferredCollectible();
        }
    }

    /// <summary>
    /// Traces the strong roots a buffered full mark accumulated on <see cref="_markStack"/>
    /// (M5). The roots are dealt round-robin to the participants' stacks, but that alone
    /// cannot balance a full mark — a couple of stack slots typically own the entire live
    /// graph — so drains donate excess to the share queue and idle workers take from it. A
    /// participant that runs fully dry idles in the termination protocol: when every
    /// participant is idle at once, no one holds work that could refill the queue, and the
    /// phase is over.
    /// </summary>
    private void ParallelDrainMark()
    {
        var stacks = _cardScanStacks!;
        var share = _markShareQueue!;
        var participants = stacks.Length;

        for (int i = 0; !_markStack.IsEmpty; i++)
        {
            stacks[i % participants].Push(_markStack.Pop());
        }

        var workerId = -1;
        var idle = 0;

        _workerPool!.Run(() =>
        {
            var id = Interlocked.Increment(ref workerId);
            DrainWithSharing(stacks[id], _cardScanDeferred![id], share, ref idle, participants);
        });

        ProcessDeferredCollectible();
    }

    /// <summary>
    /// One participant's trace loop: drain the own stack (donating excess to the share
    /// queue), refill from the queue, and idle in the termination protocol when both run
    /// dry — when every participant is idle at once, no one holds work that could refill
    /// the queue, and the phase is over. A participant that enters late (M8.2: still
    /// inside its GcScanRoots call while others already drain) simply hasn't idled yet,
    /// so the termination check cannot trip before its roots are traced.
    /// </summary>
    private void DrainWithSharing(MarkStack stack, List<nint> deferred, MarkShareQueue share, ref int idle, int participants)
    {
        while (true)
        {
            DrainMarkStack(stack, deferred, share);

            if (share.TakeInto(stack, TakeChunk) > 0)
            {
                continue;
            }

            Interlocked.Increment(ref idle);

            var spin = new SpinWait();
            var working = false;

            while (!working)
            {
                if (Volatile.Read(ref idle) == participants)
                {
                    return;
                }

                if (!share.IsEmpty)
                {
                    // Leave idle before taking so the termination check can't trip
                    // while this worker holds work
                    Interlocked.Decrement(ref idle);

                    if (share.TakeInto(stack, TakeChunk) > 0)
                    {
                        working = true;
                    }
                    else
                    {
                        Interlocked.Increment(ref idle);
                    }

                    continue;
                }

                // Never Sleep(1): its ~15.6 ms timer quantum became the entire cost
                // of the remark drain (drain2 measured 15 ms marking ONE object) and
                // one quantum per pre-drain pass — any worker idle for >~50 µs slept
                // through the join. Yield/Sleep(0) still hand the core to runnable
                // mutators during a concurrent window.
                spin.SpinOnce(sleep1Threshold: -1);
            }
        }
    }

    /// <summary>
    /// The deferred collectible edges call into the EE, so only the GC thread takes
    /// them; anything they reach traces inline (and further collectible edges too).
    /// </summary>
    private void ProcessDeferredCollectible()
    {
        foreach (var deferred in _cardScanDeferred!)
        {
            foreach (var ptr in deferred)
            {
                var loaderAllocator = (GCObject*)_gcToClr.GetLoaderAllocatorObjectForGC((GCObject*)ptr);

                if (loaderAllocator != null)
                {
                    _markStack.Push((nint)loaderAllocator);
                }
            }

            deferred.Clear();
        }

        DrainMarkStack(_markStack, deferredCollectible: null);
    }

    // A drain donates the bottom half of its stack whenever it grows past the threshold,
    // and idle workers refill in TakeChunk-sized bites: big enough to amortize the lock,
    // small enough that the tail of the phase stays spread across participants.
    private const int DonateThreshold = 4096;
    private const int TakeChunk = 4096;

    /// <summary>
    /// The transitive trace: marks every unmarked reachable object on the stack. Entries
    /// may be raw field values (EnumerateObjectReferences pushes them unfiltered), so the
    /// pop side does all the filtering. Thread-safe for the parallel card scan (M5):
    /// objects are claimed with a CAS before enumeration, LiveBytes accumulates with
    /// interlocked adds, and the one EE call in the loop — the collectible
    /// LoaderAllocator edge — is deferred to <paramref name="deferredCollectible"/> when
    /// set, because worker threads must never call into the EE. Pass null on the GC
    /// thread to take the edge inline. With <paramref name="share"/> set (parallel full
    /// mark), excess work is donated for idle workers to take.
    /// </summary>
    private void DrainMarkStack(MarkStack stack, List<nint>? deferredCollectible, MarkShareQueue? share = null)
    {
        while (!stack.IsEmpty)
        {
            if (share is not null && stack.Count > DonateThreshold)
            {
                share.DonateFrom(stack, stack.Count / 2);
            }

            var ptr = stack.Pop();
            var o = (GCObject*)ptr;

            // Reject anything that isn't inside a live region BEFORE consulting marks:
            // the side bitmap only covers the heap range, so an out-of-range pointer
            // (frozen-segment references land here too) would index it out of bounds
            if (!_regionAllocator.TryGetIndex(ptr, out var regionIndex))
            {
                continue;
            }

            if (o->IsMarked())
            {
                continue;
            }

            var entry = _regionAllocator.GetEntry(regionIndex);

            if (entry->Kind == RegionKind.Free)
            {
                continue;
            }

            if (entry->Kind == RegionKind.SpanExtension)
            {
                entry = _regionAllocator.GetEntry(entry->SpanStartIndex);
            }

            if (!o->TryMark())
            {
                // Another worker claimed it and will enumerate it
                continue;
            }

            o->EnumerateObjectReferences(stack);

            if (o->MethodTable->Collectible)
            {
                // The type's managed LoaderAllocator must live as long as any instance —
                // this edge is all that keeps a collectible assembly's MethodTables and JIT
                // code alive while instances exist (missing-features 3.1)
                if (deferredCollectible is not null)
                {
                    deferredCollectible.Add(ptr);
                }
                else
                {
                    var loaderAllocator = (GCObject*)_gcToClr.GetLoaderAllocatorObjectForGC(o);

                    if (loaderAllocator != null)
                    {
                        stack.Push((nint)loaderAllocator);
                    }
                }
            }

            var markedSize = (long)Align((nint)o->ComputeSize());
            Interlocked.Add(ref entry->LiveBytes, (int)Math.Min(markedSize, int.MaxValue / 2));

            if (GcStats.Enabled)
            {
                Interlocked.Increment(ref GcStats.MarkedCount);
                Interlocked.Add(ref GcStats.MarkedBytes, markedSize);
            }
        }
    }

    /// <summary>
    /// Maps an interior pointer to its containing object (contract: obj ≤ ptr < obj + size),
    /// or null when it doesn't point inside a live heap object (SPEC-M2 §6).
    /// </summary>
    private GCObject* ResolveInteriorPointer(nint addr)
    {
        if (!_regionAllocator.TryGetIndex(addr, out var index))
        {
            return null;
        }

        var entry = _regionAllocator.GetEntry(index);

        if (entry->Kind == RegionKind.SpanExtension)
        {
            index = entry->SpanStartIndex;
            entry = _regionAllocator.GetEntry(index);
        }

        GCObject* result = null;

        switch (entry->Kind)
        {
            case RegionKind.SpanStart:
            {
                var obj = (GCObject*)(_regionAllocator.RegionBase(index) + IntPtr.Size);

                if (addr >= (nint)obj && addr < (nint)obj + (nint)obj->ComputeSize())
                {
                    result = obj;
                }

                break;
            }

            case RegionKind.SizeClass:
            {
                // O(1): block index arithmetic + allocated-bit check (SPEC-M2 §6)
                var classSize = Region.ClassSizes[entry->SizeClass];
                var offset = addr - _regionAllocator.RegionBase(index);
                var block = (int)(offset / classSize);

                if (block < RegionAllocator.BlockCount(entry->SizeClass)
                    && (entry->AllocatedBlocks & (1ul << block)) != 0)
                {
                    var obj = (GCObject*)(_regionAllocator.RegionBase(index) + (nint)block * classSize + IntPtr.Size);

                    if (addr >= (nint)obj && addr < (nint)obj + (nint)obj->ComputeSize())
                    {
                        result = obj;
                    }
                }

                break;
            }

            case RegionKind.Bump:
            {
                // Linear walk from the region base; bounded by the 2 MB region size.
                // No brick table by design — see SPEC-M2 §6 for why bricks go stale.
                var ptr = _regionAllocator.RegionBase(index) + IntPtr.Size;
                var end = entry->Cursor;

                while (ptr < end && ptr <= addr)
                {
                    var obj = (GCObject*)ptr;
                    var size = (nint)obj->ComputeSize();

                    if (addr < ptr + size)
                    {
                        result = obj;
                        break;
                    }

                    ptr = Align(ptr + size);
                }

                break;
            }
        }

        // A pointer into dead space resolves to a free-object plug: not a live object
        // (conservative-mode hardening, missing-features 9.1)
        if (result != null && result->MethodTable == _freeObjectMethodTable)
        {
            return null;
        }

        return result;
    }
}
