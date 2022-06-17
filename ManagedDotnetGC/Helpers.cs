using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ManagedDotnetGC
{
    internal static unsafe class DacExtensions
    {
        public static string? GetObjectTypeName(this ISOSDacInterface dac, CLRDATA_ADDRESS address)
        {
            var result = dac.GetObjectClassName(address, 0, null, out var needed);

            if (!result.IsOK)
            {
                return null;
            }
            
            Span<char> str = stackalloc char[(int)needed];

            fixed (char* p = &str[0])
            {
                dac.GetObjectClassName(address, needed, p, out _);
                return new string(str);
            }
        }
    }
}
