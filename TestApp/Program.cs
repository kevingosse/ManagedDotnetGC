using Spectre.Console;
using System.Runtime;
using System.Runtime.CompilerServices;
using TestApp;

Console.WriteLine("Hello, World!");

//while (true)
//{
//    new NonFinalizableObject();
//    new FinalizableObject();
//    Console.ReadLine();
//}

StaticClass.Root = new MyOwnObject();

var obj1 = new object();
var obj2 = new object();
var obj3 = new object();
var d1 = new DependentHandle(obj1, obj2);

Console.WriteLine($"Obj1: {obj1.GetHashCode()}, Obj2: {obj2.GetHashCode()}, Obj3: {obj3.GetHashCode()}");
Console.WriteLine($"DependentHandle: {d1.Target.GetHashCode()} - {d1.Dependent.GetHashCode()}");

d1.Dependent = obj3;

Console.WriteLine($"DependentHandle: {d1.Target.GetHashCode()} - {d1.Dependent.GetHashCode()}");

var array = new byte[32720];

Array.Fill(array, (byte)0xCC);

for (int i = 0; i < array.Length; i++)
{
    if (array[i] != 0xCC)
    {
        Console.WriteLine("Error");
    }
}

if (!WeakReferenceTest.Run())
{
    AnsiConsole.MarkupLine("[bold red]WeakReferenceTest failed[/]");
    return;
}

while (true)
{
    Console.WriteLine("Press return to allocate");

    if (Console.ReadLine() == "q")
    {
        return;
    }

    for (int i = 0; i < 10; i++)
    {
        var obj = new MyOwnObject { Str = new string('c', 10), Obj = new() };
    }

    var bigObj = new byte[100_000];
    Array.Fill(bigObj, (byte)0xFF);

    for (int i = 0; i < bigObj.Length; i++)
    {
        if (bigObj[i] != 0xFF)
        {
            Console.WriteLine($"Error - {bigObj[i]:x2}");
        }
    }

    var huge_obj = new byte[1024 * 1024 * 8];
    Array.Fill(huge_obj, (byte)0xEE);

    for (int i = 0; i < huge_obj.Length; i++)
    {
        if (huge_obj[i] != 0xEE)
        {
            Console.WriteLine("Error");
        }
    }

    Console.WriteLine($"Huge array address: {Utils.GetAddress(huge_obj):x2}");

    var weakRef = GetWeakReference();
    Console.WriteLine($"Before collection, weak reference is alive: {weakRef.IsAlive}");

    GC.Collect();

    Console.WriteLine($"After collection, weak reference is alive: {weakRef.IsAlive}");
    Console.WriteLine($"Target: {Utils.GetAddress(weakRef.Target):x2}");
}

[MethodImpl(MethodImplOptions.NoInlining)]
static WeakReference GetWeakReference()
{
    return new(new());
}

public class MyOwnObject
{
    public string Str;
    public Object Obj;
}

public class FinalizableObject
{
    public ulong Value = 0xFFFFFFFFFFFFFFFF;

    ~FinalizableObject()
    {
        Console.WriteLine("Finalized");
    }
}

public class NonFinalizableObject
{
    public ulong Value = 0xCCCCCCCCCCCCCCCC;
}