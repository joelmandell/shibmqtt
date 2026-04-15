using ShibMqtt.Core.Protocol;

namespace ShibMqtt.Core.Packets;

/// <summary>A single topic filter in a SUBSCRIBE packet.</summary>
public readonly struct TopicFilter
{
    public required string Topic { get; init; }
    public MqttQualityOfService MaxQos { get; init; }
}

/// <summary>MQTT SUBSCRIBE packet (§3.8).</summary>
public sealed class SubscribePacket
{
    public ushort PacketIdentifier { get; init; }
    public required IReadOnlyList<TopicFilter> TopicFilters { get; init; }
}

/// <summary>MQTT SUBACK packet (§3.9).</summary>
public sealed class SubAckPacket
{
    public ushort PacketIdentifier { get; init; }

    /// <summary>
    /// Return codes per subscribed topic: 0x00-0x02 = granted QoS, 0x80 = failure.
    /// </summary>
    public required IReadOnlyList<byte> ReturnCodes { get; init; }
}
