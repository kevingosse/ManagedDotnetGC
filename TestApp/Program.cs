using TestApp;

Console.WriteLine("Hello, World!");

StaticClass.Root = new MyOwnObject();

while (true)
{
    Console.WriteLine("Press return to allocate");
    
    if (Console.ReadLine() == "q")
    {
        return;
    }

    // var b = new byte[1024 * 1024 * 8];

    for (int i = 0; i < 1024 * 1024; i++)
    {
        var obj = new MyOwnObject { Str = new string('c', 10), Obj = new() };
    }

    GC.Collect();
}


public class MyOwnObject
{
    public string Str;
    public Object Obj;
}
