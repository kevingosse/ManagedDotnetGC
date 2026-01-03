namespace ManagedDotnetGC;

internal class Log
{
    public static void Write(string str, bool newLine = true)
    {
        Console.Write($"[GC] {str}{(newLine ? Environment.NewLine : string.Empty)}");
    }
}
