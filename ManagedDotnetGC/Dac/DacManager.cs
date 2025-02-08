using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ManagedDotnetGC.Dac;

public class DacManager : IDisposable
{
    private static readonly Guid IClrDataProcessGuid = new("5c552ab6-fc09-4cb3-8e36-22fa03c798b7");

    private IntPtr _libraryHandle;

    private DacManager(IntPtr libraryHandle, IntPtr dac)
    {
        _libraryHandle = libraryHandle;
        Dac = NativeObjects.ISOSDacInterface.Wrap(dac);
    }

    public NativeObjects.ISOSDacInterfaceInvoker Dac { get; private set; }

    public static unsafe HResult TryLoad(out DacManager? dacManager)
    {
        var coreclr = GetLibraryName("coreclr");

        var module = Process.GetCurrentProcess().Modules
            .Cast<ProcessModule>()
            .FirstOrDefault(m => m.ModuleName == coreclr);

        if (module == null)
        {
            Log.Write($"{coreclr} not found");
            dacManager = null;
            return HResult.E_FAIL;
        }

        var dacPath = Path.Combine(
            Path.GetDirectoryName(module.FileName)!,
            GetLibraryName("mscordaccore"));

        if (!File.Exists(dacPath))
        {
            Log.Write($"The DAC wasn't found at the expected path ({dacPath})");
            dacManager = null;
            return HResult.E_FAIL;
        }

        var library = NativeLibrary.Load(dacPath);

        try
        {
            var export = NativeLibrary.GetExport(library, "CLRDataCreateInstance");
            var createInstance = (delegate* unmanaged[Stdcall]<in Guid, IntPtr, out IntPtr, HResult>)export;

            var dataTarget = new ClrDataTarget();
            var result = createInstance(IClrDataProcessGuid, dataTarget.ICLRDataTargetObject, out var pUnk);

            var unknown = NativeObjects.IUnknown.Wrap(pUnk);
            result = unknown.QueryInterface(ISOSDacInterface.Guid, out var sosDacInterfacePtr);

            dacManager = result ? new DacManager(library, sosDacInterfacePtr) : null;
            return result;
        }
        catch
        {
            NativeLibrary.Free(library);
            throw;
        }
    }

    private static string GetLibraryName(string name)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return $"{name}.dll";
        }
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return $"lib{name}.so";
        }
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return $"lib{name}.dylib";
        }
        
        throw new PlatformNotSupportedException();
    }

    public unsafe string? GetObjectName(CLRDATA_ADDRESS address)
    {
        var result = Dac.GetObjectClassName(address, 0, null, out var needed);

        if (!result)
        {
            return null;
        }

        Span<char> str = stackalloc char[(int)needed];

        fixed (char* p = str)
        {
            result = Dac.GetObjectClassName(address, needed, p, out _);

            if (!result)
            {
                return null;
            }

            return new string(str);
        }
    }

    public void Dispose()
    {
        Dac.Release();
        NativeLibrary.Free(_libraryHandle);
    }
}
