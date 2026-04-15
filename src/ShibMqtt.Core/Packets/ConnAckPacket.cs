using ShibMqtt.Core.Protocol;

namespace ShibMqtt.Core.Packets;

/// <summary>MQTT CONNACK packet (§3.2).</summary>
public readonly struct ConnAckPacket
{
    /// <summary>Session-present flag: true when the broker has a persisted session for this client.</summary>
    public bool SessionPresent { get; init; }

    /// <summary>Return code indicating the result of the connection attempt.</summary>
    public MqttConnectReturnCode ReturnCode { get; init; }

    /// <summary>Convenience property: true when ReturnCode is Accepted.</summary>
    public bool IsSuccess => ReturnCode == MqttConnectReturnCode.Accepted;
}
