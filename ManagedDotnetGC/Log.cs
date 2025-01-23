namespace ManagedDotnetGC;

internal class Log
{
    public static void Write(string str)
    {
        Console.WriteLine($"[GC] {str}");
    }
}
