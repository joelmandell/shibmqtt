using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using ShibMqtt.Core;

namespace ShibMqtt.Benchmarks;

[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class TopicMatcherBenchmarks
{
    [Benchmark]
    public bool ExactMatch()
        => MqttTopicMatcher.IsMatch("sensors/site-3/device-12/temp", "sensors/site-3/device-12/temp");

    [Benchmark]
    public bool WildcardMatch()
        => MqttTopicMatcher.IsMatch("sensors/site-3/device-12/temp", "sensors/+/device-12/#");

    [Benchmark]
    public bool SystemTopicMatch()
        => MqttTopicMatcher.IsMatch("$SYS/broker/clients/connected", "$SYS/#");
}
