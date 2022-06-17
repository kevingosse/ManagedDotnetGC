using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ManagedDotnetGC
{
    public readonly unsafe struct GCDesc
    {
        private static readonly int s_GCDescSize = IntPtr.Size * 2;

        private readonly byte* _data;
        private readonly int _size;

        public GCDesc(byte* data, int size)
        {
            _data = data;
            _size = size;
        }

        public IEnumerable<(IntPtr ReferencedObject, int Offset)> WalkObject(IntPtr buffer, int size)
        {
            int series = GetNumSeries();
            int highest = GetHighestSeries();
            int curr = highest;
            
            if (series > 0)
            {
                int lowest = GetLowestSeries();
                do
                {
                    long offset = GetSeriesOffset(curr);
                    long stop = offset + GetSeriesSize(curr) + size;

                    while (offset < stop)
                    {
                        var ret = Marshal.ReadIntPtr(buffer, (int)offset);
                        if (ret != IntPtr.Zero)
                            yield return (ret, (int)offset);

                        offset += IntPtr.Size;
                    }

                    curr -= s_GCDescSize;
                } while (curr >= lowest);
            }
            else
            {
                long offset = GetSeriesOffset(curr);

                while (offset < size - IntPtr.Size)
                {
                    for (int i = 0; i > series; i--)
                    {
                        int nptrs = GetPointers(curr, i);
                        int skip = GetSkip(curr, i);

                        long stop = offset + (nptrs * IntPtr.Size);
                        do
                        {
                            var ret = Marshal.ReadIntPtr(buffer, (int)offset);
                            if (ret != IntPtr.Zero)
                                yield return (ret, (int)offset);

                            offset += IntPtr.Size;
                        } while (offset < stop);

                        offset += skip;
                    }
                }
            }
        }

        private int GetPointers(int curr, int i)
        {
            int offset = i * IntPtr.Size;
            
            if (IntPtr.Size == 4)
            {
                return *(short*)(_data + curr + offset);
            }

            return *(int*)(_data + curr + offset);
        }

        private int GetSkip(int curr, int i)
        {
            int offset = i * IntPtr.Size + IntPtr.Size / 2;
            
            if (IntPtr.Size == 4)
            {
                return *(short*)(_data + curr + offset);
            }

            return *(int*)(_data + curr + offset);
        }

        private int GetSeriesSize(int curr)
        {
            if (IntPtr.Size == 4)
            {
                return *(int*)(_data + curr);
            }

            return (int)*(long*)(_data + curr);
        }

        private long GetSeriesOffset(int curr)
        {
            long offset;
            
            if (IntPtr.Size == 4)
            {
                offset = *(uint*)(_data + curr + IntPtr.Size);
            }
            else
            {
                offset = *(long*)(_data + curr + IntPtr.Size);
            }

            return offset;
        }

        private int GetHighestSeries()
        {
            return _size - IntPtr.Size * 3;
        }

        private int GetLowestSeries()
        {
            return _size - ComputeSize(GetNumSeries());
        }

        private static int ComputeSize(int series)
        {
            return IntPtr.Size + series * IntPtr.Size * 2;
        }

        private int GetNumSeries()
        {
            if (IntPtr.Size == 4)
            {
                return *(int*)(_data + _size - IntPtr.Size);
            }

            return (int)*(long*)(_data + _size - IntPtr.Size);
        }
    }
}
