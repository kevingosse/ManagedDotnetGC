using System.Runtime.InteropServices;

namespace ManagedDotnetGC
{
    internal unsafe class GCHandleManager : IGCHandleManager
    {
        private readonly IGCToCLR _gcToClr;
        private readonly NativeStubs.IGCHandleManagerStub _gcHandleManagerStub;
        private readonly GCHandleStore _gcHandleStore;


        public GCHandleManager(IGCToCLR gcToClr)
        {
            _gcHandleStore = new GCHandleStore();
            _gcToClr = gcToClr;
            _gcHandleManagerStub = NativeStubs.IGCHandleManagerStub.Wrap(this);
        }

        public IntPtr IGCHandleManagerObject => _gcHandleManagerStub;

        public GCHandleStore Store => _gcHandleStore;

        public bool Initialize()
        {
            Console.WriteLine("[GC] GCHandleManager Initialize");
            return true;
        }

        public void Shutdown()
        {
        }

        public IntPtr GetGlobalHandleStore()
        {
            return _gcHandleStore.IGCHandleStoreObject;
        }

        public IntPtr CreateHandleStore()
        {
            Console.WriteLine("GCHandleManager CreateHandleStore");

            return default;
        }

        public void DestroyHandleStore(IntPtr store)
        {
            Console.WriteLine("[GC] GCHandleManager DestroyHandleStore");
        }

        public unsafe OBJECTHANDLE CreateGlobalHandleOfType(GCObject* obj, HandleType type)
        {
            return _gcHandleStore.CreateHandleOfType(obj, type);
        }

        public OBJECTHANDLE CreateDuplicateHandle(OBJECTHANDLE handle)
        {
            Console.WriteLine("GCHandleManager CreateDuplicateHandle");

            return handle;
        }

        public void DestroyHandleOfType(OBJECTHANDLE handle, HandleType type)
        {
            // Console.WriteLine("GCHandleManager DestroyHandleOfType");
        }

        public void DestroyHandleOfUnknownType(OBJECTHANDLE handle)
        {
            Console.WriteLine("GCHandleManager DestroyHandleOfUnknownType");
        }

        public unsafe void SetExtraInfoForHandle(OBJECTHANDLE handle, HandleType type, void* pExtraInfo)
        {
            Console.WriteLine("GCHandleManager SetExtraInfoForHandle");
        }

        public unsafe void* GetExtraInfoFromHandle(OBJECTHANDLE handle)
        {
            Console.WriteLine("GCHandleManager GetExtraInfoFromHandle");

            return null;
        }

        public unsafe void StoreObjectInHandle(OBJECTHANDLE handle, GCObject* obj)
        {
            Console.WriteLine("GCHandleManager StoreObjectInHandle");

        }

        public unsafe bool StoreObjectInHandleIfNull(OBJECTHANDLE handle, GCObject* obj)
        {
            Console.WriteLine("GCHandleManager StoreObjectInHandleIfNull");

            return false;
        }

        public unsafe void SetDependentHandleSecondary(OBJECTHANDLE handle, GCObject* obj)
        {
            Console.WriteLine("GCHandleManager SetDependentHandleSecondary");

        }

        public unsafe GCObject* GetDependentHandleSecondary(OBJECTHANDLE handle)
        {
            Console.WriteLine("GCHandleManager GetDependentHandleSecondary");

            return null;
        }

        public unsafe GCObject* InterlockedCompareExchangeObjectInHandle(OBJECTHANDLE handle, GCObject* obj, GCObject* comparandObject)
        {
            Console.WriteLine("GCHandleManager InterlockedCompareExchangeObjectInHandle");

            return null;
        }

        public HandleType HandleFetchType(OBJECTHANDLE handle)
        {
            Console.WriteLine("GCHandleManager HandleFetchType");

            return HandleType.HNDTYPE_WEAK_SHORT;
        }

        public unsafe void TraceRefCountedHandles(void* callback, uint* param1, uint* param2)
        {
            Console.WriteLine("GCHandleManager TraceRefCountedHandles");

        }
    }
}
