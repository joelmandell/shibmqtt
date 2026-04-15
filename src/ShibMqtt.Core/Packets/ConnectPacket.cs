using ShibMqtt.Core.Protocol;

namespace ShibMqtt.Core.Packets;

/// <summary>MQTT CONNECT packet (§3.1).</summary>
public sealed class ConnectPacket
{
    /// <summary>Client identifier (must be 1-23 characters for MQTT 3.1.1 brokers; empty means broker assigns one).</summary>
    public required string ClientId { get; init; }

    /// <summary>Keep-alive interval in seconds. 0 = disabled.</summary>
    public ushort KeepAlive { get; init; } = MqttConstants.DefaultKeepAlive;

    /// <summary>Whether to start a clean session.</summary>
    public bool CleanSession { get; init; } = true;

    /// <summary>Optional username for authentication.</summary>
    public string? Username { get; init; }

    /// <summary>Optional password for authentication (only valid when Username is set).</summary>
    public ReadOnlyMemory<byte>? Password { get; init; }

    /// <summary>Optional last-will message.</summary>
    public WillMessage? Will { get; init; }
}

/// <summary>Last-will message carried in a CONNECT packet.</summary>
public sealed class WillMessage
{
    public required string Topic { get; init; }
    public required ReadOnlyMemory<byte> Payload { get; init; }
    public MqttQualityOfService Qos { get; init; }
    public bool Retain { get; init; }
}
