namespace ManagedDotnetGC;

/// <summary>
/// Background pre-zeroing of pooled dirty regions (M7). Zero-at-carve (M4) moved all
/// zeroing out of pauses and out of the allocation locks, but left it on the allocating
/// thread's critical path (~1 s of app-thread time per 15 GB recycled). This thread spends
/// an idle core on it instead: after every collection it checks dirty regions out of the
/// pool, zeroes them with no locks held, and returns them flagged pre-zeroed, so carves
/// increasingly hand out memory with no zeroing debt. It is purely opportunistic — if it
/// falls behind (or never runs), carves zero inline exactly as before.
///
/// The thread lives on the GC dll's own runtime, invisible to the target EE, and never
/// calls into it. That is why it cannot use <see cref="GcAwareLock.Acquire"/> (mode
/// switches are EE calls) and spins on <see cref="GcAwareLock.TryAcquire"/> instead —
/// holders do bounded work. And because SuspendEE does not park this thread, every pool
/// operation happens under <see cref="RegionAllocator.ZeroerGate"/>, which the collecting
/// thread holds for the whole suspension: the sweep's lock-free pool pushes and TrimPool's
/// decommits can never interleave with a checkout. Sharded supply (M7) only changes which
/// lock guards what: hole-region checkouts take the reservoir lock (supply passes through
/// the reservoir; shards' private lists hold at most a pull batch), pool checkouts the
/// pool lock — the escape discipline is identical per lock.
/// </summary>
internal sealed class GcRegionZeroer
{
    private readonly RegionAllocator _regionAllocator;
    private readonly GcAwareLock _reservoirLock;
    private readonly GcAwareLock _poolLock;
    private readonly SemaphoreSlim _wake = new(0);

    public GcRegionZeroer(RegionAllocator regionAllocator, GcAwareLock reservoirLock, GcAwareLock poolLock)
    {
        _regionAllocator = regionAllocator;
        _reservoirLock = reservoirLock;
        _poolLock = poolLock;

        new Thread(Loop)
        {
            IsBackground = true,
            Name = "ManagedDotnetGC zeroer",
            // Below the app's threads: on a saturated machine the zeroer starves and
            // mutators just zero inline — the work never doubles up
            Priority = ThreadPriority.BelowNormal,
        }.Start();
    }

    /// <summary>Called after every collection: the sweep just refilled the pool.</summary>
    public void Kick()
    {
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    private void Loop()
    {
        while (true)
        {
            _wake.Wait();

            // Holes first: the hole-first carve policy consumes them before any pooled
            // region, so pre-zeroing there pays off soonest (and covers most windows)
            while (TryZeroHoleRegion())
            {
            }

            while (TryZeroPoolRegion())
            {
            }
        }
    }

    private bool TryZeroHoleRegion()
    {
        if (_regionAllocator.CollectorWaitingForGate)
        {
            return false; // stand down; the post-collection kick resumes the drain
        }

        // The gate is held across checkout, memset and checkin: a sweep rebuilds the
        // recycled lists and walks hole memory, so a starting collection must wait out
        // the region in flight (~100 µs) rather than race it
        lock (_regionAllocator.ZeroerGate)
        {
            int index;

            var locked = TryAcquireOrExclusive(_reservoirLock);

            try
            {
                if (!_regionAllocator.TryCheckOutDirtyHoleRegion(out index))
                {
                    return false;
                }
            }
            finally
            {
                if (locked)
                {
                    _reservoirLock.Release();
                }
            }

            // Reservoir lock released: mutators allocate freely everywhere else — this
            // region's holes are unreachable (unlinked) and window carves of its virgin
            // tail touch disjoint memory
            _regionAllocator.ZeroCheckedOutHoleBodies(index);

            locked = TryAcquireOrExclusive(_reservoirLock);

            try
            {
                _regionAllocator.CheckInZeroedHoleRegion(index);
            }
            finally
            {
                if (locked)
                {
                    _reservoirLock.Release();
                }
            }
        }

        return true;
    }

    private bool TryZeroPoolRegion()
    {
        if (_regionAllocator.CollectorWaitingForGate)
        {
            return false;
        }

        int index;
        nint regionBase;

        lock (_regionAllocator.ZeroerGate)
        {
            var locked = TryAcquireOrExclusive(_poolLock);

            try
            {
                if (!_regionAllocator.TryCheckOutDirtyRegion(out index, out regionBase))
                {
                    return false;
                }
            }
            finally
            {
                if (locked)
                {
                    _poolLock.Release();
                }
            }
        }

        // No locks held: the region is out of the pool, so carves cannot hand it out and
        // TrimPool cannot decommit it from under the memset (a checked-out pool region is
        // invisible to sweeps, so unlike the hole path this may straddle a collection)
        RegionAllocator.ZeroWindow(regionBase, Region.Size);

        lock (_regionAllocator.ZeroerGate)
        {
            var locked = TryAcquireOrExclusive(_poolLock);

            try
            {
                _regionAllocator.CheckInZeroedRegion(index);
            }
            finally
            {
                if (locked)
                {
                    _poolLock.Release();
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Acquires the given supply lock, or returns false once a collector is waiting on the
    /// gate — at which point allocator state is exclusively ours and the operation may
    /// proceed lock-free. The escape is what breaks the deadlock cycle: a contended
    /// GcAwareLock winner parks in DisablePreemptiveGC *holding the lock* until the
    /// collection finishes, the collector waits on our gate, and we would otherwise spin
    /// on that parked thread's lock forever. The exclusivity is sound because the world
    /// is suspended by then: every thread that wins a supply lock afterwards parks
    /// before touching allocator state, coop-mode threads are at safe points (never
    /// inside the critical section), and sweeps only run while the collector holds the
    /// gate — which it cannot get until we exit.
    /// </summary>
    private bool TryAcquireOrExclusive(GcAwareLock supplyLock)
    {
        while (!supplyLock.TryAcquire())
        {
            if (_regionAllocator.CollectorWaitingForGate)
            {
                return false;
            }

            Thread.SpinWait(20);
        }

        return true;
    }
}
