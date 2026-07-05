using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

// One binary, two modes:
//   AspNetSample.exe serve [port]                      — Kestrel server (run under ManagedDotnetGC)
//   AspNetSample.exe load <baseUrl> <seconds> [workers] — load driver (run on the stock GC)
//
// The server's job is to look like a real web app to the GC: JSON churn, string building,
// LOH-band buffers, multi-MB responses, a retained cache with slow turnover, and Kestrel's
// own pinned socket buffers — all under sustained concurrency.

if (args.Length >= 1 && args[0] == "load")
{
    return await LoadDriver.Run(args);
}

var port = args.Length >= 2 && int.TryParse(args[1], out var p) ? p : 5211;

var builder = WebApplication.CreateBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

var app = builder.Build();

var startTime = Stopwatch.StartNew();
long totalRequests = 0;

// Retained working set: ~3000 entries × ~36 KB average ≈ 100 MB, slowly replaced under load
var cache = new ConcurrentDictionary<int, byte[]>();
const int CacheKeys = 3000;

app.Use(async (context, next) =>
{
    Interlocked.Increment(ref totalRequests);
    await next(context);
});

// Small-object churn: a list of DTOs serialized to JSON
app.MapGet("/items/{count:int}", (int count) =>
{
    count = Math.Clamp(count, 1, 1000);
    var items = new List<Item>(count);

    for (int i = 0; i < count; i++)
    {
        items.Add(new Item(i, $"item-{i:D6}", i * 3.14, DateTime.UtcNow.AddMinutes(-i),
            new[] { "alpha", "beta", "gamma" }[i % 3]));
    }

    return Results.Ok(items);
});

// Deserialize + aggregate + reserialize
app.MapPost("/orders", (Order order) =>
{
    var total = order.Lines.Sum(l => l.Quantity * l.UnitPrice);
    var summary = new OrderSummary(order.Id, order.Lines.Count, total,
        string.Join(",", order.Lines.Select(l => l.Sku)));
    return Results.Ok(summary);
});

// LOH-band and span-tier buffers
app.MapGet("/blob/{kb:int}", (int kb) =>
{
    kb = Math.Clamp(kb, 1, 16 * 1024);
    var buffer = new byte[kb * 1024];
    // Touch every page so the buffer can't be optimized away
    for (int i = 0; i < buffer.Length; i += 4096)
    {
        buffer[i] = (byte)i;
    }

    return Results.Bytes(buffer);
});

// Big-string churn through StringBuilder
app.MapGet("/report/{lines:int}", (int lines) =>
{
    lines = Math.Clamp(lines, 1, 5000);
    var sb = new StringBuilder(lines * 100);

    for (int i = 0; i < lines; i++)
    {
        sb.AppendLine($"{i:D8} | host-{i % 17:D3} | status={(i % 7 == 0 ? "WARN" : "OK  ")} | " +
                      $"latency={i * 0.37:F2}ms | bytes={i * 1021} | trace={Guid.NewGuid()}");
    }

    return Results.Text(sb.ToString());
});

// Retained-set churn: get-or-add, with a 10% chance of replacing the entry
app.MapGet("/cache/{key:int}", (int key) =>
{
    key = Math.Abs(key) % CacheKeys;

    var replace = Random.Shared.Next(10) == 0;
    var value = !replace && cache.TryGetValue(key, out var existing)
        ? existing
        : cache[key] = MakeEntry(key);

    // Read the entry so it behaves like data, not ballast
    long checksum = 0;
    for (int i = 0; i < value.Length; i += 512)
    {
        checksum += value[i];
    }

    return Results.Ok(new { key, size = value.Length, checksum });
});

// Process-level stats. Only GC APIs known to be implemented in ManagedDotnetGC are called
// here: CollectionCount → GetGcCount works; GetTotalMemory would return 0 (stub) anyway.
app.MapGet("/stats", () => Results.Ok(new
{
    uptimeSeconds = startTime.Elapsed.TotalSeconds,
    totalRequests = Interlocked.Read(ref totalRequests),
    workingSetMB = Environment.WorkingSet / (1024.0 * 1024.0),
    gcCount = GC.CollectionCount(0),
    // 0 under ManagedDotnetGC (GetTotalBytesInUse stub), > 0 under the stock GC:
    // the soak script uses this to verify the custom GC is actually loaded
    gcTotalMemory = GC.GetTotalMemory(false),
    cacheEntries = cache.Count,
    threads = Process.GetCurrentProcess().Threads.Count,
}));

app.Run();
return 0;

static byte[] MakeEntry(int key)
{
    var size = 4 * 1024 + (key * 40033 % (64 * 1024));
    var data = new byte[size];
    Random.Shared.NextBytes(data);
    return data;
}

record Item(int Id, string Name, double Value, DateTime Timestamp, string Category);

record OrderLine(string Sku, int Quantity, decimal UnitPrice);

record Order(int Id, List<OrderLine> Lines);

record OrderSummary(int Id, int LineCount, decimal Total, string Skus);

static class LoadDriver
{
    public static async Task<int> Run(string[] args)
    {
        if (args.Length < 3 || !int.TryParse(args[2], out var seconds))
        {
            Console.WriteLine("Usage: AspNetSample load <baseUrl> <seconds> [workers]");
            return 2;
        }

        var baseUrl = args[1].TrimEnd('/');
        var workers = args.Length >= 4 && int.TryParse(args[3], out var w) ? w : 32;

        using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };

        // Wait for the server to come up
        for (int i = 0; ; i++)
        {
            try
            {
                (await http.GetAsync("/stats")).EnsureSuccessStatusCode();
                break;
            }
            catch when (i < 30)
            {
                await Task.Delay(1000);
            }
        }

        Console.WriteLine($"# soak: {baseUrl}, {seconds}s, {workers} workers");
        Console.WriteLine("# elapsed_s total_reqs window_rps errors server_ws_mb server_gc_count server_threads");

        long totalOk = 0, totalErrors = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));

        var tasks = new List<Task>();
        for (int i = 0; i < workers; i++)
        {
            var seed = i;
            tasks.Add(Task.Run(() => Worker(http, seed, cts.Token,
                () => Interlocked.Increment(ref totalOk),
                () => Interlocked.Increment(ref totalErrors))));
        }

        var watch = Stopwatch.StartNew();
        long lastTotal = 0;
        var lastElapsed = 0.0;
        var statsFailures = 0;

        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var total = Interlocked.Read(ref totalOk);
            var elapsed = watch.Elapsed.TotalSeconds;
            var rps = (total - lastTotal) / Math.Max(1.0, elapsed - lastElapsed);
            lastTotal = total;
            lastElapsed = elapsed;

            try
            {
                var json = await http.GetStringAsync("/stats", CancellationToken.None);
                using var stats = JsonDocument.Parse(json);
                var root = stats.RootElement;
                Console.WriteLine($"{elapsed,8:F0} {total,12} {rps,8:F0} {Interlocked.Read(ref totalErrors),8} " +
                                  $"{root.GetProperty("workingSetMB").GetDouble(),10:F1} " +
                                  $"{root.GetProperty("gcCount").GetInt64(),8} " +
                                  $"{root.GetProperty("threads").GetInt32(),6}");
                statsFailures = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{elapsed,8:F0} {total,12} {rps,8:F0} {Interlocked.Read(ref totalErrors),8} STATS-FAIL: {ex.Message}");

                // Three strikes: the server is gone, stop burning the remaining soak time
                if (++statsFailures >= 3)
                {
                    Console.WriteLine("# server unreachable, aborting soak");
                    cts.Cancel();
                    break;
                }
            }
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
        }

        var finalOk = Interlocked.Read(ref totalOk);
        var finalErrors = Interlocked.Read(ref totalErrors);
        var errorRate = finalErrors / Math.Max(1.0, finalOk + finalErrors);

        // Final health check: the server must still be alive
        bool serverAlive;
        try
        {
            (await http.GetAsync("/stats")).EnsureSuccessStatusCode();
            serverAlive = true;
        }
        catch
        {
            serverAlive = false;
        }

        Console.WriteLine($"# done: {finalOk} ok, {finalErrors} errors ({errorRate:P3}), server alive: {serverAlive}");
        return serverAlive && errorRate < 0.005 ? 0 : 1;
    }

    private static async Task Worker(HttpClient http, int seed, CancellationToken token, Action onOk, Action onError)
    {
        var random = new Random(unchecked(12345 * (seed + 1)));

        while (!token.IsCancellationRequested)
        {
            try
            {
                var roll = random.Next(100);
                using var response = roll switch
                {
                    < 45 => await http.GetAsync($"/items/{random.Next(1, 51)}", token),
                    < 65 => await PostOrder(http, random, token),
                    < 80 => await http.GetAsync($"/report/{random.Next(50, 201)}", token),
                    < 90 => await http.GetAsync($"/cache/{random.Next(10000)}", token),
                    < 99 => await http.GetAsync($"/blob/{random.Next(8, 257)}", token),
                    _ => await http.GetAsync($"/blob/{random.Next(1024, 6145)}", token),
                };

                // Drain the body so Kestrel actually pushes the bytes
                await response.Content.ReadAsByteArrayAsync(token);

                if (response.IsSuccessStatusCode)
                {
                    onOk();
                }
                else
                {
                    onError();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                onError();
            }
        }
    }

    private static Task<HttpResponseMessage> PostOrder(HttpClient http, Random random, CancellationToken token)
    {
        var lines = new List<OrderLine>();
        var count = random.Next(5, 101);

        for (int i = 0; i < count; i++)
        {
            lines.Add(new OrderLine($"SKU-{random.Next(100000):D6}", random.Next(1, 20), random.Next(100, 100000) / 100m));
        }

        return http.PostAsJsonAsync("/orders", new Order(random.Next(), lines), token);
    }
}
