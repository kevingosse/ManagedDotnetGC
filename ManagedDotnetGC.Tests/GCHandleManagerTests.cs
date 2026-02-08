using NUnit.Framework;
using Shouldly;

namespace ManagedDotnetGC.Tests;

[TestFixture]
public unsafe class GCHandleManagerTests
{
    [Test]
    public void GetGlobalHandleStore_ReturnsValidPointer()
    {
        using var manager = new GCHandleManager();
        var storePtr = manager.GetGlobalHandleStore();
        storePtr.ShouldNotBe(IntPtr.Zero);
    }

    [Test]
    public void CreateGlobalHandleOfType_CreatesHandle()
    {
        var obj = (GCObject*)0x1234;
        var handleType = HandleType.HNDTYPE_STRONG;

        using var manager = new GCHandleManager();
        var handle = manager.CreateGlobalHandleOfType(obj, handleType);

        ((nint)handle).ShouldNotBe(0);
        ((nint)handle->Object).ShouldBe((nint)obj);
        handle->Type.ShouldBe(handleType);
    }

    [Test]
    public void CreateGlobalHandleOfType_WithVariousTypes()
    {
        var obj = (GCObject*)0x5678;
        var handleTypes = new[]
        {
            HandleType.HNDTYPE_WEAK_SHORT,
            HandleType.HNDTYPE_STRONG,
            HandleType.HNDTYPE_PINNED
        };

        using var manager = new GCHandleManager();

        foreach (var handleType in handleTypes)
        {
            var handle = manager.CreateGlobalHandleOfType(obj, handleType);
            handle->Type.ShouldBe(handleType);
        }
    }

    [Test]
    public void CreateDuplicateHandle_CopiesHandleProperties()
    {
        var obj = (GCObject*)0x1234;

        using var manager = new GCHandleManager();
        var originalHandle = manager.CreateGlobalHandleOfType(obj, HandleType.HNDTYPE_STRONG);
        originalHandle->ExtraInfo = new(0xDEADBEEF);

        var duplicateHandle = manager.CreateDuplicateHandle(originalHandle);

        ((nint)duplicateHandle->Object).ShouldBe((nint)originalHandle->Object);
        duplicateHandle->Type.ShouldBe(originalHandle->Type);
        duplicateHandle->ExtraInfo.ShouldBe(originalHandle->ExtraInfo);
    }

    [Test]
    public void DestroyHandleOfType_RemovesHandle()
    {
        var obj = (GCObject*)0x1234;

        using var manager = new GCHandleManager();
        var handle = manager.CreateGlobalHandleOfType(obj, HandleType.HNDTYPE_STRONG);

        manager.DestroyHandleOfType(handle, HandleType.HNDTYPE_STRONG);

        handle->Type.ShouldBe(HandleType.HNDTYPE_FREE);
    }

    [Test]
    public void DestroyHandleOfUnknownType_RemovesHandle()
    {
        var obj = (GCObject*)0x1234;

        using var manager = new GCHandleManager();
        var handle = manager.CreateGlobalHandleOfType(obj, HandleType.HNDTYPE_STRONG);

        manager.DestroyHandleOfUnknownType(handle);

        handle->Type.ShouldBe(HandleType.HNDTYPE_FREE);
    }

    [Test]
    public void SetExtraInfoForHandle_StoresExtraInfo()
    {
        var obj = (GCObject*)0x1234;

        using var manager = new GCHandleManager();
        var handle = manager.CreateGlobalHandleOfType(obj, HandleType.HNDTYPE_STRONG);
        var extraInfo = (nint)0xCAFEBABE;

        manager.SetExtraInfoForHandle(handle, HandleType.HNDTYPE_STRONG, extraInfo);

        handle->ExtraInfo.ShouldBe(extraInfo);
    }

    [Test]
    public void GetExtraInfoFromHandle_ReturnsStoredExtraInfo()
    {
        var obj = (GCObject*)0x1234;

        using var manager = new GCHandleManager();
        var handle = manager.CreateGlobalHandleOfType(obj, HandleType.HNDTYPE_STRONG);
        var extraInfo = (nint)0xCAFEBABE;
        handle->ExtraInfo = extraInfo;

        var retrieved = manager.GetExtraInfoFromHandle(handle);

        retrieved.ShouldBe(extraInfo);
    }

    [Test]
    public void StoreObjectInHandle_UpdatesObject()
    {
        var obj1 = (GCObject*)0x1000;
        var obj2 = (GCObject*)0x2000;

        using var manager = new GCHandleManager();
        var handle = manager.CreateGlobalHandleOfType(obj1, HandleType.HNDTYPE_STRONG);

        manager.StoreObjectInHandle(handle, obj2);

        ((nint)handle->Object).ShouldBe((nint)obj2);
    }

    [Test]
    public void StoreObjectInHandleIfNull_StoresWhenNull()
    {
        var obj = (GCObject*)0x1234;

        using var manager = new GCHandleManager();
        var handle = manager.CreateGlobalHandleOfType(null, HandleType.HNDTYPE_STRONG);
        var result = manager.StoreObjectInHandleIfNull(handle, obj);

        result.ShouldBeTrue();
        ((nint)handle->Object).ShouldBe((nint)obj);
    }

    [Test]
    public void StoreObjectInHandleIfNull_DoesNotStoreWhenNotNull()
    {
        var obj1 = (GCObject*)0x1000;
        var obj2 = (GCObject*)0x2000;

        using var manager = new GCHandleManager();
        var handle = manager.CreateGlobalHandleOfType(obj1, HandleType.HNDTYPE_STRONG);
        var result = manager.StoreObjectInHandleIfNull(handle, obj2);

        result.ShouldBeFalse();
        ((nint)handle->Object).ShouldBe((nint)obj1);
    }

    [Test]
    public void SetDependentHandleSecondary_StoresSecondaryObject()
    {
        var primary = (GCObject*)0x1000;
        var secondary = (GCObject*)0x2000;

        using var manager = new GCHandleManager();
        var handle = manager.CreateGlobalHandleOfType(primary, HandleType.HNDTYPE_DEPENDENT);

        manager.SetDependentHandleSecondary(handle, secondary);

        handle->ExtraInfo.ShouldBe((nint)secondary);
    }

    [Test]
    public void GetDependentHandleSecondary_ReturnsSecondaryObject()
    {
        var primary = (GCObject*)0x1000;
        var secondary = (GCObject*)0x2000;

        using var manager = new GCHandleManager();
        var handle = manager.CreateGlobalHandleOfType(primary, HandleType.HNDTYPE_DEPENDENT);
        manager.SetDependentHandleSecondary(handle, secondary);

        var retrieved = manager.GetDependentHandleSecondary(handle);

        ((nint)retrieved).ShouldBe((nint)secondary);
    }

    [Test]
    public void InterlockedCompareExchangeObjectInHandle_SwapsWhenEqual()
    {
        var obj1 = (GCObject*)0x1000;
        var obj2 = (GCObject*)0x2000;

        using var manager = new GCHandleManager();
        var handle = manager.CreateGlobalHandleOfType(obj1, HandleType.HNDTYPE_STRONG);
        var previous = manager.InterlockedCompareExchangeObjectInHandle(handle, obj2, obj1);

        Assert.That((nint)previous, Is.EqualTo((nint)obj1));
        Assert.That((nint)handle->Object, Is.EqualTo((nint)obj2));
    }

    [Test]
    public void InterlockedCompareExchangeObjectInHandle_DoesNotSwapWhenNotEqual()
    {
        var obj1 = (GCObject*)0x1000;
        var obj2 = (GCObject*)0x2000;
        var obj3 = (GCObject*)0x3000;

        using var manager = new GCHandleManager();
        var handle = manager.CreateGlobalHandleOfType(obj1, HandleType.HNDTYPE_STRONG);
        var previous = manager.InterlockedCompareExchangeObjectInHandle(handle, obj3, obj2);

        Assert.That((nint)previous, Is.EqualTo((nint)obj1));
        Assert.That((nint)handle->Object, Is.EqualTo((nint)obj1));
    }

    [Test]
    public void HandleFetchType_ReturnsHandleType()
    {
        var handleType = HandleType.HNDTYPE_PINNED;

        using var manager = new GCHandleManager();
        var handle = manager.CreateGlobalHandleOfType((GCObject*)0x1234, handleType);

        var fetched = manager.HandleFetchType(handle);

        fetched.ShouldBe(handleType);
    }

    [Test]
    public void Store_ReturnsValidGCHandleStore()
    {
        using var manager = new GCHandleManager();
        var store = manager.Store;

        Assert.That(store, Is.Not.Null);
        Assert.That(store, Is.TypeOf<GCHandleStore>());
    }
}
