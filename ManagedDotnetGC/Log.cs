using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ManagedDotnetGC
{
    internal class Log
    {
        public static void Write(string str)
        {
            Console.WriteLine($"[GC] {str}");
        }
    }
}
