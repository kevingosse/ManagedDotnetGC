using NUnit.Framework;
using Shouldly;

namespace ManagedDotnetGC.Tests;

[TestFixture]
public unsafe class GCHandleStoreTests
{
    [Test]
    public void CreateHandleOfType_CreatesHandle()
    {
        var obj = (GCObject*)0x1234;
        var handleType = HandleType.HNDTYPE_STRONG;

        using var store = new GCHandleStore();

        var handle = store.CreateHandleOfType(obj, handleType);

        ((nint)handle).ShouldNotBe(0);
        ((nint)handle->Object).ShouldBe((nint)obj);
        handle->Type.ShouldBe(handleType);
    }

    [Test]
    public void CreateHandleOfType_WithDifferentTypes_CreatesCorrectly()
    {
        var obj = (GCObject*)0x5678;
        var handleTypes = new[]
        {
            HandleType.HNDTYPE_WEAK_SHORT,
            HandleType.HNDTYPE_WEAK_LONG,
            HandleType.HNDTYPE_STRONG,
            HandleType.HNDTYPE_PINNED,
            HandleType.HNDTYPE_DEPENDENT
        };

        using var store = new GCHandleStore();

        foreach (var handleType in handleTypes)
        {
            var handle = store.CreateHandleOfType(obj, handleType);
            handle->Type.ShouldBe(handleType);
            ((nint)handle->Object).ShouldBe((nint)obj);
        }
    }

    [Test]
    public void DestroyHandle_RemovesHandle()
    {
        var obj = (GCObject*)0x1234;

        using var store = new GCHandleStore();
        var handle = store.CreateHandleOfType(obj, HandleType.HNDTYPE_STRONG);
        store.DestroyHandle(handle);

        store.ContainsHandle(handle).ShouldBeFalse();
    }

    [Test]
    public void ContainsHandle_ReturnsTrueForCreatedHandle()
    {
        var obj = (GCObject*)0x1234;

        using var store = new GCHandleStore();
        var handle = store.CreateHandleOfType(obj, HandleType.HNDTYPE_STRONG);

        store.ContainsHandle(handle).ShouldBeTrue();
    }

    [Test]
    public void ContainsHandle_ReturnsFalseForDestroyedHandle()
    {
        var obj = (GCObject*)0x1234;

        using var store = new GCHandleStore();
        var handle = store.CreateHandleOfType(obj, HandleType.HNDTYPE_STRONG);
        store.DestroyHandle(handle);

        store.ContainsHandle(handle).ShouldBeFalse();
    }

    [Test]
    public void CreateHandleWithExtraInfo_StoresExtraInfo()
    {
        var obj = (GCObject*)0x1234;
        var extraInfo = (void*)0xDEADBEEF;

        using var store = new GCHandleStore();
        var handle = store.CreateHandleWithExtraInfo(obj, HandleType.HNDTYPE_STRONG, extraInfo);

        handle->ExtraInfo.ShouldBe((nint)extraInfo);
    }

    [Test]
    public void CreateDependentHandle_CreatesHandleWithSecondary()
    {
        var primary = (GCObject*)0x1111;
        var secondary = (GCObject*)0x2222;

        using var store = new GCHandleStore();
        var handle = store.CreateDependentHandle(primary, secondary);

        handle->Type.ShouldBe(HandleType.HNDTYPE_DEPENDENT);
        ((nint)handle->Object).ShouldBe((nint)primary);
        handle->ExtraInfo.ShouldBe((nint)secondary);
    }

    [Test]
    public void CreateMultipleHandles_AllAreIndependent()
    {
        var obj1 = (GCObject*)0x1000;
        var obj2 = (GCObject*)0x2000;
        var obj3 = (GCObject*)0x3000;

        using var store = new GCHandleStore();
        var handle1 = store.CreateHandleOfType(obj1, HandleType.HNDTYPE_STRONG);
        var handle2 = store.CreateHandleOfType(obj2, HandleType.HNDTYPE_WEAK_LONG);
        var handle3 = store.CreateHandleOfType(obj3, HandleType.HNDTYPE_PINNED);

        ((nint)handle1->Object).ShouldBe((nint)obj1);
        ((nint)handle2->Object).ShouldBe((nint)obj2);
        ((nint)handle3->Object).ShouldBe((nint)obj3);
        ((nint)handle1).ShouldNotBe((nint)handle2);
        ((nint)handle2).ShouldNotBe((nint)handle3);
    }

    [Test]
    public void DestroyHandle_DoesNotAffectOtherHandles()
    {
        var obj1 = (GCObject*)0x1000;
        var obj2 = (GCObject*)0x2000;

        using var store = new GCHandleStore();
        var handle1 = store.CreateHandleOfType(obj1, HandleType.HNDTYPE_STRONG);
        var handle2 = store.CreateHandleOfType(obj2, HandleType.HNDTYPE_STRONG);

        store.DestroyHandle(handle1);

        store.ContainsHandle(handle1).ShouldBeFalse();
        store.ContainsHandle(handle2).ShouldBeTrue();
        ((nint)handle2->Object).ShouldBe((nint)obj2);
    }

    [Test]
    public void EnumerateHandlesOfType_IncludesCreatedHandles()
    {
        var obj1 = (GCObject*)0x1000;
        var obj2 = (GCObject*)0x2000;

        using var store = new GCHandleStore();
        store.CreateHandleOfType(obj1, HandleType.HNDTYPE_STRONG);
        store.CreateHandleOfType(obj2, HandleType.HNDTYPE_STRONG);

        int count = 0;
        var objects = new List<nint>();
        foreach (var handle in store.EnumerateHandlesOfType([HandleType.HNDTYPE_STRONG]))
        {
            count++;
            objects.Add((nint)handle->Object);
        }

        count.ShouldBe(2);
        objects.ShouldContain((nint)obj1);
        objects.ShouldContain((nint)obj2);
    }

    [Test]
    public void EnumerateHandlesOfType_ExcludesDestroyedHandles()
    {        
        var obj1 = (GCObject*)0x1000;
        var obj2 = (GCObject*)0x2000;

        using var store = new GCHandleStore();
        var handle1 = store.CreateHandleOfType(obj1, HandleType.HNDTYPE_STRONG);
        var handle2 = store.CreateHandleOfType(obj2, HandleType.HNDTYPE_STRONG);

        store.DestroyHandle(handle1);

        var objects = new List<nint>();
        foreach (var handle in store.EnumerateHandlesOfType([HandleType.HNDTYPE_STRONG]))
        {
            objects.Add((nint)handle->Object);
        }

        objects.ShouldBe([(nint)obj2]);
    }

    [Test]
    public void EnumerateHandlesOfType_WithMultipleTypes()
    {
        var strongObj = (GCObject*)0x1000;
        var weakObj = (GCObject*)0x2000;

        using var store = new GCHandleStore();
        store.CreateHandleOfType(strongObj, HandleType.HNDTYPE_STRONG);
        store.CreateHandleOfType(weakObj, HandleType.HNDTYPE_WEAK_LONG);

        var objects = new List<nint>();
        foreach (var handle in store.EnumerateHandlesOfType([HandleType.HNDTYPE_STRONG, HandleType.HNDTYPE_WEAK_LONG]))
        {
            objects.Add((nint)handle->Object);
        }

        objects.ShouldBe([(nint)strongObj, (nint)weakObj]);
    }
}
