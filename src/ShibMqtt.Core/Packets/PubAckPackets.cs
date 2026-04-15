namespace ShibMqtt.Core.Packets;

/// <summary>MQTT PUBACK packet (QoS 1 acknowledgment, §3.4).</summary>
public readonly struct PubAckPacket
{
    public ushort PacketIdentifier { get; init; }
}

/// <summary>MQTT PUBREC packet (QoS 2 step 1, §3.5).</summary>
public readonly struct PubRecPacket
{
    public ushort PacketIdentifier { get; init; }
}

/// <summary>MQTT PUBREL packet (QoS 2 step 2, §3.6).</summary>
public readonly struct PubRelPacket
{
    public ushort PacketIdentifier { get; init; }
}

/// <summary>MQTT PUBCOMP packet (QoS 2 step 3, §3.7).</summary>
public readonly struct PubCompPacket
{
    public ushort PacketIdentifier { get; init; }
}
