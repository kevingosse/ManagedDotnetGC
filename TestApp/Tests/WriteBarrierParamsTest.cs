using ManagedDotnetGC.Api;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Verifies the WriteBarrierParameters the GC passed to the EE: the WriteBarrierOp::Initialize
/// contract wants real heap bounds and a non-null card table (debug-flavor runtimes assert on it).
/// The ephemeral_low = -1 trick that disables the card-marking path is fine and is not asserted on.
/// (docs/missing-features.md item 7.1)
/// </summary>
public class WriteBarrierParamsTest() : TestBase("Write Barrier Parameters", GcFeature.GcInternals)
{
    public override bool RequiresCustomGcApi => true;

    public override void Run()
    {
        var gc = GcApi.TryCreate() ?? throw new Exception("Failed to initialize GC API");

        var lowest = gc.GetWriteBarrierParameter(WriteBarrierParameterKind.LowestAddress);
        var highest = gc.GetWriteBarrierParameter(WriteBarrierParameterKind.HighestAddress);

        if (lowest == 0 || highest == 0)
        {
            throw new Exception(
                $"The write barrier heap bounds were never set (lowest={lowest:x}, highest={highest:x}): " +
                "Initialize must advertise the reservation range");
        }

        if (lowest >= highest)
        {
            throw new Exception($"Inverted write barrier heap bounds: lowest={lowest:x} >= highest={highest:x}");
        }

        // A live heap object must fall inside the advertised bounds
        var probe = new byte[128];
        var address = (ulong)Utils.GetAddress(probe);

        if (address < lowest || address >= highest)
        {
            throw new Exception(
                $"A heap object at {address:x} falls outside the advertised write barrier bounds [{lowest:x}, {highest:x})");
        }

        GC.KeepAlive(probe);

        var cardTable = gc.GetWriteBarrierParameter(WriteBarrierParameterKind.CardTable);

        if (cardTable == 0)
        {
            throw new Exception(
                "The card table passed to the EE is null: the Initialize contract wants a real " +
                "(dummy-sized) card table even if the ephemeral trick keeps it clean");
        }
    }
}
