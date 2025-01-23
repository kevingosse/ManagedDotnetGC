namespace ManagedDotnetGC.Dac;

[NativeObject]
public unsafe interface ICLRDataTarget : Interfaces.IUnknown
{
    HResult GetMachineType(out uint machine);

    HResult GetPointerSize(out uint size);

    HResult GetImageBase(char* moduleName, out CLRDATA_ADDRESS baseAddress);

    HResult ReadVirtual(
        CLRDATA_ADDRESS address,
        byte* buffer,
        uint size,
        out uint done);

    HResult WriteVirtual(
        CLRDATA_ADDRESS address,
        byte* buffer,
        uint size,
        out uint done);

    HResult GetTLSValue(
        uint threadID,
        uint index,
        out CLRDATA_ADDRESS value);

    HResult SetTLSValue(
        uint threadID,
        uint index,
        CLRDATA_ADDRESS value);

    HResult GetCurrentThreadID(
        out uint threadID);

    HResult GetThreadContext(
        uint threadID,
        uint contextFlags,
        uint contextSize,
        byte* context);

    HResult SetThreadContext(
        uint threadID,
        uint contextSize,
        byte* context);

    HResult Request(
        uint reqCode,
        uint inBufferSize,
        byte* inBuffer,
        uint outBufferSize,
        byte* outBuffer);
}
