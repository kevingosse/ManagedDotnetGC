using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests GC.GetGCMemoryInfo: heap size and committed bytes must be non-zero, the collection index
/// must increase across collections, and FinalizationPendingCount must reflect objects waiting for
/// their finalizer. (docs/missing-features.md items 6.1, 10)
/// </summary>
public class MemoryInfoTest() : TestBase("GC Memory Info", GcFeature.MemoryInfo)
{
    private static readonly ManualResetEventSlim GateEvent = new(false);
    private static volatile bool _gateEntered;

    private sealed class Gate
    {
        ~Gate()
        {
            _gateEntered = true;
            GateEvent.Wait();
        }
    }

    private sealed class Finalizable
    {
        ~Finalizable()
        {
        }
    }

    public override void Run()
    {
        TestBasicNumbers();
        TestFinalizationPendingCount();
    }

    public override void Cleanup()
    {
        GateEvent.Set();
        GC.WaitForPendingFinalizers();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestBasicNumbers()
    {
        // Hold some live data so the heap is definitely non-empty
        var liveData = new byte[4 * 1024 * 1024];
        liveData[0] = 1;

        GC.Collect();

        var info = GC.GetGCMemoryInfo();

        if (info.HeapSizeBytes <= 0)
        {
            throw new Exception($"GCMemoryInfo.HeapSizeBytes is {info.HeapSizeBytes}, expected > 0");
        }

        if (info.TotalCommittedBytes <= 0)
        {
            throw new Exception($"GCMemoryInfo.TotalCommittedBytes is {info.TotalCommittedBytes}, expected > 0");
        }

        if (info.Index <= 0)
        {
            throw new Exception($"GCMemoryInfo.Index is {info.Index}, expected > 0 after an induced collection");
        }

        long indexBefore = info.Index;

        GC.Collect();

        var infoAfter = GC.GetGCMemoryInfo();

        if (infoAfter.Index <= indexBefore)
        {
            throw new Exception($"GCMemoryInfo.Index did not increase across collections ({indexBefore} -> {infoAfter.Index})");
        }

        GC.KeepAlive(liveData);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestFinalizationPendingCount()
    {
        GateEvent.Reset();
        _gateEntered = false;

        // First provably block the finalizer thread, then queue finalizable objects behind it so
        // the pending count is stable when we read it.
        CreateGate();
        GC.Collect();

        var deadline = Environment.TickCount64 + 10_000;

        while (!_gateEntered)
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new Exception("Test setup failed: the gate finalizer did not run within 10 seconds");
            }

            Thread.Sleep(10);
        }

        CreateFinalizables();
        GC.Collect();

        var info = GC.GetGCMemoryInfo();

        if (info.FinalizationPendingCount < 1)
        {
            throw new Exception(
                $"GCMemoryInfo.FinalizationPendingCount is {info.FinalizationPendingCount} while " +
                "finalizable objects are queued behind a blocked finalizer thread");
        }

        GateEvent.Set();
        GC.WaitForPendingFinalizers();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateGate()
    {
        _ = new Gate();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateFinalizables()
    {
        for (int i = 0; i < 8; i++)
        {
            _ = new Finalizable();
        }
    }
}
