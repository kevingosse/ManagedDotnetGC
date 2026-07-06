// Forced-full stress harness: a tight GC.Collect loop racing checksummed, store-heavy
// mutators. Every knob-sensitive edge the collector has runs constantly under it —
// concurrent-cycle pause A/window/remark, card scanning over freshly stored slots,
// wholesale recycling, and (M6.5 stage 2) the mutator sweep-assist: mutators run dry
// mid-sweep and drain the plan themselves. A checksum mismatch or crash is a
// correctness failure; exit code 0 means clean.
//
// Usage: GcStress [seconds] [collectIntervalMs]   (default 30, 0)
//
// collectIntervalMs 0 = tight GC.Collect loop (every collection a forced full).
// A nonzero interval throttles the collector thread so budget-triggered YOUNG
// collections dominate between fulls — the ASP.NET-soak shape (the 2026-07-06 stage-2
// heap corruption only reproduced under young-heavy cadence; the tight-loop mode
// missed it because forced fulls never run the young card-scan/sweep path).
//
// Size mix crosses all three tiers: bump (< 32 KB), size classes, and spans, with a
// pinned-handle churn thread to keep pinning in the picture.

using System.Runtime.InteropServices;

var seconds = args.Length > 0 ? int.Parse(args[0]) : 30;
var collectIntervalMs = args.Length > 1 ? int.Parse(args[1]) : 0;
var mutatorCount = Math.Min(4, Environment.ProcessorCount - 1);
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
var failures = 0;
long verified = 0;
long allocatedNodes = 0;

var mutators = new Thread[mutatorCount];

for (var t = 0; t < mutatorCount; t++)
{
    var seed = 987654321 * (t + 1) + 12345;

    mutators[t] = new Thread(() =>
    {
        var rng = new Random(seed);

        // Thread-lifetime array: stores into it are the old→young card sources, and
        // its survivors smear across old regions exactly like the soh scenario
        var live = new Node?[4096];

        while (!stop.IsCancellationRequested)
        {
            // Weighted size mix: mostly bump-tier, some class-tier, occasional spans
            var roll = rng.Next(100);
            var size = roll < 85 ? rng.Next(16, 4000)
                : roll < 97 ? rng.Next(33_000, 120_000)
                : rng.Next(300_000, 2_500_000);

            var node = new Node(size, rng);
            live[rng.Next(live.Length)] = node;

            // Re-verify a random survivor: a sweep or assist that clobbered live
            // payload, or a card scan that missed a young target, shows up here
            var candidate = live[rng.Next(live.Length)];

            if (candidate is not null)
            {
                if (!candidate.Verify())
                {
                    Interlocked.Increment(ref failures);
                    Console.Error.WriteLine("CHECKSUM MISMATCH");
                }

                Interlocked.Increment(ref verified);
            }

            if ((Interlocked.Increment(ref allocatedNodes) & 63) == 0)
            {
                // Pinned churn: brief pins on payloads, the classic socket-buffer shape
                var pin = GCHandle.Alloc(node.Payload, GCHandleType.Pinned);
                pin.Free();
            }
        }
    });
}

var collector = new Thread(() =>
{
    while (!stop.IsCancellationRequested)
    {
        GC.Collect();

        if (collectIntervalMs > 0)
        {
            Thread.Sleep(collectIntervalMs);
        }
    }
});

foreach (var m in mutators)
{
    m.Start();
}

collector.Start();

foreach (var m in mutators)
{
    m.Join();
}

collector.Join();

Console.WriteLine($"GcStress: {seconds}s, {allocatedNodes:N0} nodes, {verified:N0} verifications, {failures} failures, gen2 count {GC.CollectionCount(2)}");
return failures == 0 ? 0 : 1;

sealed class Node
{
    public byte[] Payload;
    private readonly long _sum;

    public Node(int size, Random rng)
    {
        Payload = new byte[size];
        rng.NextBytes(Payload);
        _sum = Checksum(Payload);
    }

    public bool Verify() => Checksum(Payload) == _sum;

    private static long Checksum(byte[] payload)
    {
        long sum = 0;

        // Sample large payloads so verification stays allocation-bound, not CPU-bound
        var step = Math.Max(1, payload.Length / 4096);

        for (var i = 0; i < payload.Length; i += step)
        {
            sum = sum * 31 + payload[i];
        }

        return sum;
    }
}
