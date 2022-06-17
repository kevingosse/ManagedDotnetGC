namespace ManagedDotnetGC;

[GenerateNativeStub]
public unsafe interface ICLRDataTarget2 : ICLRDataTarget
{
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