namespace ManagedDotnetGC.Dac;

[NativeObject]
public interface ICLRDataTarget2 : ICLRDataTarget
{
    public static readonly Guid Guid = new("6d05fae3-189c-4630-a6dc-1c251e1c01ab");

    HResult AllocVirtual(
        CLRDATA_ADDRESS addr,
        uint size,
        uint typeFlags,
        uint protectFlags,
        out CLRDATA_ADDRESS virt);
        
     HResult FreeVirtual(
        CLRDATA_ADDRESS addr,
        uint size,
        uint typeFlags);
}