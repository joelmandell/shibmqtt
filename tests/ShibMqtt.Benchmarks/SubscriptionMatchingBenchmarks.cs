using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using ShibMqtt.Broker;
using ShibMqtt.Core.Packets;
using ShibMqtt.Core.Protocol;

namespace ShibMqtt.Benchmarks;

[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class SubscriptionMatchingBenchmarks
{
    private SubscriptionManager _subscriptionManager = null!;

    [Params(128, 1024, 4096)]
    public int ClientCount { get; set; }

    [Params(2, 6)]
    public int FiltersPerClient { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _subscriptionManager = new SubscriptionManager();

        for (int clientIndex = 0; clientIndex < ClientCount; clientIndex++)
            _subscriptionManager.Subscribe($"client-{clientIndex}", CreateFilters(clientIndex));
    }

    [Benchmark]
    public int GetMatchingSubscribers()
    {
        int count = 0;
        foreach (var _ in _subscriptionManager.GetMatchingSubscribers("sensors/site-3/device-12/temp"))
            count++;

        return count;
    }

    private TopicFilter[] CreateFilters(int clientIndex)
    {
        var filters = new TopicFilter[FiltersPerClient];

        for (int filterIndex = 0; filterIndex < filters.Length; filterIndex++)
        {
            string topic = filterIndex switch
            {
                0 => $"sensors/site-{clientIndex % 16}/#",
                1 => $"sensors/+/device-{clientIndex % 32}/temp",
                2 => $"alerts/site-{clientIndex % 8}/device-{clientIndex % 64}",
                3 => $"sensors/site-{clientIndex % 16}/device-{clientIndex % 64}/humidity",
                4 => "metrics/+/cpu",
                _ => $"sensors/site-{clientIndex % 16}/device-{clientIndex % 64}/temp",
            };

            filters[filterIndex] = new TopicFilter
            {
                Topic = topic,
                MaxQos = (MqttQualityOfService)(filterIndex % 3),
            };
        }

        return filters;
    }
}
