using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ManagedDotnetGC
{
    public unsafe class GCHandleStore : IGCHandleStore
    {
        private readonly NativeStubs.IGCHandleStoreStub _gcHandleStoreStub;
        private readonly nint* _store;
        private int _handleCount;

        public GCHandleStore()
        {
            _store = (nint*)Marshal.AllocHGlobal(sizeof(nint) * 65535);

            for (int i = 0; i < 65535; i++)
            {
                _store[i] = 0;
            }

            _gcHandleStoreStub = NativeStubs.IGCHandleStoreStub.Wrap(this);
        }

        public IntPtr IGCHandleStoreObject => _gcHandleStoreStub;

        public void Uproot()
        {
            Console.WriteLine("GCHandleStore Uproot");
        }

        public bool ContainsHandle(OBJECTHANDLE handle)
        {
            Console.WriteLine("GCHandleStore ContainsHandle");
            return false;
        }

        public void DumpHandles()
        {
            Console.WriteLine($"Store location: {(nint)_store:x2}");

            for (int i = 0; i < _handleCount; i++)
            {
                Console.WriteLine($"{((nint)(_store + i)):x2} - {*(_store + i):x2}");
            }
        }

        public unsafe OBJECTHANDLE CreateHandleOfType(GCObject* obj, HandleType type)
        {
            //if (obj != null)
            //{
            //    Console.WriteLine($"GCHandleStore CreateHandleOfType - {(nint)obj:x2} -> {(*obj).MethodTable:x2}");
            //}
            //else
            //{
            //    Console.WriteLine($"GCHandleStore CreateHandleOfType - {(nint)obj:x2}");
            //}
            
            var handle = _store + _handleCount;

            *handle = (nint)obj;

            _handleCount++;

            return new OBJECTHANDLE((nint)handle);
        }

        public unsafe OBJECTHANDLE CreateHandleOfType2(GCObject* obj, HandleType type, int heapToAffinitizeTo)
        {
            Console.WriteLine($"GCHandleStore CreateHandleOfType2 - {(nint)obj:x2}");

            var handle = _store + _handleCount;

             *handle = (nint)obj;
            //*handle = (nint)(&obj);
            var result = new OBJECTHANDLE((nint)handle);
            _handleCount++;

            DumpHandles();
            Console.WriteLine($"Returning {result.Value:x2}");

            return result;
        }

        public unsafe OBJECTHANDLE CreateHandleWithExtraInfo(GCObject* obj, HandleType type, void* pExtraInfo)
        {
            Console.WriteLine("GCHandleStore CreateHandleWithExtraInfo");

            return new OBJECTHANDLE((nint)_store + (_handleCount++));
        }

        public unsafe OBJECTHANDLE CreateDependentHandle(GCObject* primary, GCObject* secondary)
        {
            Console.WriteLine("GCHandleStore CreateDependentHandle");

            return new OBJECTHANDLE((nint)_store + (_handleCount++));
        }

        public void Destructor()
        {
            Console.WriteLine("Destructor");
        }
    }
}
