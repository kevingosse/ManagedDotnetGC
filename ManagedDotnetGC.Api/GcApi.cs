using NativeObjects;

namespace ManagedDotnetGC.Api;

public class GcApi : IGc
{
    private readonly IGcInvoker _gc;

    private GcApi(IGcInvoker gc)
    {
        _gc = gc;
    }

    public static GcApi? TryCreate()
    {
        if (!GC.GetConfigurationVariables().TryGetValue("GC API", out var address))
        {
            return null;
        }

        return new GcApi(new((nint)(long)address));
    }

    public void Test() => _gc.Test();
}
