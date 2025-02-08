using System.Runtime;
using TestApp;

Console.WriteLine("Hello, World!");

AppToGc.Initialize();

StaticClass.Root = new MyOwnObject();

var obj1 = new object();
var obj2 = new object();
var obj3 = new object();
var d1 = new DependentHandle(obj1, obj2);

Console.WriteLine($"Obj1: {obj1.GetHashCode()}, Obj2: {obj2.GetHashCode()}, Obj3: {obj3.GetHashCode()}");
Console.WriteLine($"DependentHandle: {d1.Target.GetHashCode()} - {d1.Dependent.GetHashCode()}");

d1.Dependent = obj3;

Console.WriteLine($"DependentHandle: {d1.Target.GetHashCode()} - {d1.Dependent.GetHashCode()}");

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

    GC.Collect();
}

public class MyOwnObject
{
    public string Str;
    public Object Obj;
}
