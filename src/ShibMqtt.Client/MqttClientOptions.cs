using ShibMqtt.Core.Packets;
using ShibMqtt.Core.Protocol;

namespace ShibMqtt.Client;

/// <summary>
/// Options for <see cref="MqttClient"/>.
/// </summary>
public sealed class MqttClientOptions
{
    public required string ClientId { get; init; }
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 1883;
    public ushort KeepAlive { get; init; } = 60;
    public bool CleanSession { get; init; } = true;
    public string? Username { get; init; }
    public ReadOnlyMemory<byte>? Password { get; init; }
    public WillMessage? Will { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
