using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ManagedDotnetGC.Dac;

public unsafe class ClrDataTarget : ICLRDataTarget, IDisposable
{
    private readonly NativeObjects.ICLRDataTarget _clrDataTarget;
    private int _referenceCount;

    public ClrDataTarget()
    {
        _clrDataTarget = NativeObjects.ICLRDataTarget.Wrap(this);
    }

    public IntPtr ICLRDataTargetObject => _clrDataTarget;

    public void Dispose()
    {
        _clrDataTarget.Dispose();
    }

    public HResult QueryInterface(in Guid guid, out IntPtr ptr)
    {
        ptr = default;
        return HResult.E_NOINTERFACE;
    }

    public int AddRef()
    {
        return Interlocked.Increment(ref _referenceCount);
    }

    public int Release()
    {
        var value = Interlocked.Decrement(ref _referenceCount);

        if (value == 0)
        {
            Dispose();
        }

        return value;
    }

    public HResult GetMachineType(out uint machine)
    {
        var architecture = RuntimeInformation.ProcessArchitecture;

        // https://learn.microsoft.com/en-us/windows/win32/sysinfo/image-file-machine-constants
        if (architecture == Architecture.X86)
        {
            machine = 0x14c; // IMAGE_FILE_MACHINE_I386
        }
        else if (architecture == Architecture.X64)
        {
            machine = 0x8664; // IMAGE_FILE_MACHINE_AMD64
        }
        else if (architecture == Architecture.Arm64)
        {
            machine = 0xaa64; // IMAGE_FILE_MACHINE_ARM64
        }
        else
        {
            machine = 0;
            return HResult.E_FAIL;
        }        

        return HResult.S_OK;
    }

    public HResult GetPointerSize(out uint size)
    {
        size = (uint)IntPtr.Size;
        return HResult.S_OK;
    }

    public HResult GetImageBase(char* moduleName, out CLRDATA_ADDRESS baseAddress)
    {
        var name = new string(moduleName);

        foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
        {
            if (module.ModuleName == name)
            {
                baseAddress = new CLRDATA_ADDRESS(module.BaseAddress.ToInt64());
                return HResult.S_OK;
            }
        }

        baseAddress = default;
        return HResult.E_FAIL;
    }

    public HResult ReadVirtual(CLRDATA_ADDRESS address, byte* buffer, uint size, out uint done)
    {
        Unsafe.CopyBlock(buffer, (void*)(IntPtr)address.Value, size);
        done = size;
        return HResult.S_OK;
    }

    public HResult WriteVirtual(CLRDATA_ADDRESS address, byte* buffer, uint size, out uint done)
    {
        done = default;
        return HResult.E_NOTIMPL;
    }

    public HResult GetTLSValue(uint threadID, uint index, out CLRDATA_ADDRESS value)
    {
        value = default;
        return HResult.E_NOTIMPL;
    }

    public HResult SetTLSValue(uint threadID, uint index, CLRDATA_ADDRESS value)
    {
        return HResult.E_NOTIMPL;
    }

    public HResult GetCurrentThreadID(out uint threadID)
    {
        threadID = default;
        return HResult.E_NOTIMPL;
    }

    public HResult GetThreadContext(uint threadID, uint contextFlags, uint contextSize, byte* context)
    {
        return HResult.E_NOTIMPL;
    }

    public HResult SetThreadContext(uint threadID, uint contextSize, byte* context)
    {
        return HResult.E_NOTIMPL;
    }

    public HResult Request(uint reqCode, uint inBufferSize, byte* inBuffer, uint outBufferSize, byte* outBuffer)
    {
        return HResult.E_NOTIMPL;
    }
}
