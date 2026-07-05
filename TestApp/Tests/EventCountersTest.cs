using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests that the process survives an EventCounters consumer — the in-process equivalent of
/// attaching dotnet-counters. The System.Runtime counters poll GC APIs on a timer; in particular
/// "gen-0-gc-budget" calls GC.GetGenerationBudget(0), which fail-fasts the custom GC as long as
/// that method throws. The test asserts that counter payloads actually arrive, including the
/// gen-0-gc-budget one. (docs/missing-features.md item 6.1)
/// </summary>
public class EventCountersTest() : TestBase("Event Counters", GcFeature.EventCounters)
{
    public override void Run()
    {
        using var listener = new CounterListener();

        if (!listener.WaitForCounter("gen-0-gc-budget", TimeSpan.FromSeconds(15)))
        {
            throw new Exception(
                $"No gen-0-gc-budget counter payload arrived within 15 seconds. " +
                $"Counters seen: {string.Join(", ", listener.SeenCounters)}");
        }
    }

    private sealed class CounterListener : EventListener
    {
        private readonly ConcurrentDictionary<string, byte> _seenCounters = new();
        private readonly ManualResetEventSlim _signal = new(false);
        private volatile string? _awaitedCounter;

        public IReadOnlyCollection<string> SeenCounters => _seenCounters.Keys.ToArray();

        public bool WaitForCounter(string name, TimeSpan timeout)
        {
            _awaitedCounter = name;

            if (_seenCounters.ContainsKey(name))
            {
                return true;
            }

            return _signal.Wait(timeout);
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "System.Runtime")
            {
                EnableEvents(eventSource, EventLevel.Informational, EventKeywords.None,
                    new Dictionary<string, string?> { ["EventCounterIntervalSec"] = "1" });
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName != "EventCounters" || eventData.Payload is not { Count: > 0 })
            {
                return;
            }

            if (eventData.Payload[0] is IDictionary<string, object> payload
                && payload.TryGetValue("Name", out var nameValue)
                && nameValue is string name)
            {
                _seenCounters.TryAdd(name, 0);

                if (name == _awaitedCounter)
                {
                    _signal.Set();
                }
            }
        }
    }
}
