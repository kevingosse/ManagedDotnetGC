namespace ManagedDotnetGC;

internal static class Log
{
    private static readonly bool _debug;

    static Log()
    {
        _debug = Environment.GetEnvironmentVariable("GC_DEBUG") == "1";
    }

    public static void Write(string str, bool newLine = true)
    {
        if (_debug)
        {
            Console.Write($"[GC] {str}{(newLine ? Environment.NewLine : string.Empty)}");
        }
    }
}
