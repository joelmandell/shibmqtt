namespace ShibMqtt.Core.Packets;

/// <summary>MQTT UNSUBSCRIBE packet (§3.10).</summary>
public sealed class UnsubscribePacket
{
    public ushort PacketIdentifier { get; init; }
    public required IReadOnlyList<string> TopicFilters { get; init; }
}

/// <summary>MQTT UNSUBACK packet (§3.11).</summary>
public readonly struct UnsubAckPacket
{
    public ushort PacketIdentifier { get; init; }
}
