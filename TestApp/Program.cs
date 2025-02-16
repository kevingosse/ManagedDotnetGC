using System.Runtime;
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

    Console.WriteLine($"Huge array address: {GetAddress(huge_obj):x2}");

    GC.Collect();
}


static unsafe nint GetAddress(object obj)
{
    return (nint)(*(object**)&obj);
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