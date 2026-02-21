using System.Runtime.CompilerServices;
using ManagedDotnetGC.Api;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests SyncBlock cache behavior - verifies syncblocks are created when needed
/// and cleaned up when the owning objects are collected.
/// </summary>
public class SyncBlockCacheTest() : TestBase("SyncBlock Cache")
{
    public override void Run()
    {
        var gc = GcApi.TryCreate() ?? throw new Exception("Failed to initialize GC API");

        TestSyncBlockCreation(gc);
        TestSyncBlockSurvivesGC(gc);
        TestSyncBlockCleanupAfterGC(gc);
    }

    private static void EnsureSyncBlock(object obj)
    {
        // SyncBlocks are created when a given object is used for both locking and hashcode
        lock (obj)
        {
            _ = obj.GetHashCode();
        }
    }

    /// <summary>
    /// Verifies that locking on objects causes syncblocks to be created.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestSyncBlockCreation(GcApi gc)
    {
        // Cleanup any existing syncblocks to get a clean baseline for the test
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var baseline = gc.GetSyncBlockCacheCount();

        const int count = 10;
        var objects = new object[count];

        for (int i = 0; i < count; i++)
        {
            objects[i] = new object();
            EnsureSyncBlock(objects[i]);
        }

        var afterLock = gc.GetSyncBlockCacheCount();

        if (afterLock < baseline + count)
        {
            throw new Exception(
                $"SyncBlock creation: expected at least {baseline + count} syncblocks after locking {count} objects, got {afterLock}");
        }

        GC.KeepAlive(objects);
    }

    /// <summary>
    /// Verifies that syncblocks are kept alive after a GC when the owning objects are still referenced.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestSyncBlockSurvivesGC(GcApi gc)
    {
        // Cleanup any existing syncblocks to get a clean baseline for the test
        GC.Collect();
        GC.WaitForPendingFinalizers();

        const int count = 10;
        var objects = new object[count];

        for (int i = 0; i < count; i++)
        {
            objects[i] = new object();
            EnsureSyncBlock(objects[i]);
        }

        var beforeGC = gc.GetSyncBlockCacheCount();

        GC.Collect();
        GC.WaitForPendingFinalizers();

        var afterGC = gc.GetSyncBlockCacheCount();

        if (afterGC < beforeGC)
        {
            throw new Exception(
                $"SyncBlock survival: expected syncblock count to remain at least {beforeGC} after GC with live objects, got {afterGC}");
        }

        GC.KeepAlive(objects);
    }

    /// <summary>
    /// Verifies that syncblocks are cleaned up when their owning objects are collected.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestSyncBlockCleanupAfterGC(GcApi gc)
    {
        // Cleanup any existing syncblocks to get a clean baseline for the test
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var baseline = gc.GetSyncBlockCacheCount();

        CreateObjects(10);

        var afterRelease = gc.GetSyncBlockCacheCount();

        if (afterRelease < baseline)
        {
            throw new Exception(
                $"SyncBlock cleanup: expected syncblocks to exist before GC, baseline={baseline}, afterRelease={afterRelease}");
        }

        // Collect the now-unreachable objects — syncblocks should be reclaimed
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var afterGC = gc.GetSyncBlockCacheCount();

        if (afterGC >= afterRelease)
        {
            throw new Exception(
                $"SyncBlock cleanup: expected syncblock count to decrease after GC, before={afterRelease}, after={afterGC}");
        }
    }

    /// <summary>
    /// Allocates objects and locks them, then returns without keeping references.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateObjects(int count)
    {
        for (int i = 0; i < count; i++)
        {
            EnsureSyncBlock(new object());
        }
    }
}
