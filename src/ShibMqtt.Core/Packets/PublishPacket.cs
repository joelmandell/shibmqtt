using System.Buffers;
using ShibMqtt.Core.Protocol;

namespace ShibMqtt.Core.Packets;

/// <summary>MQTT PUBLISH packet (§3.3). Payload is rented from the pool; callers must dispose.</summary>
public sealed class PublishPacket : IDisposable
{
    private bool _disposed;

    /// <summary>Topic name.</summary>
    public required string Topic { get; init; }

    /// <summary>Quality of Service level.</summary>
    public MqttQualityOfService Qos { get; init; }

    /// <summary>Retain flag: broker stores the last retained message for the topic.</summary>
    public bool Retain { get; init; }

    /// <summary>DUP flag: set when the packet is a re-delivery of an earlier packet.</summary>
    public bool Dup { get; init; }

    /// <summary>Packet identifier – only valid for QoS 1 and 2.</summary>
    public ushort PacketIdentifier { get; init; }

    /// <summary>Application message payload.</summary>
    public ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>
    /// Optional backing store for <see cref="Payload"/> allocated from an array pool.
    /// When set, it is returned to the pool on <see cref="Dispose"/>.
    /// </summary>
    public IMemoryOwner<byte>? PayloadOwner { get; init; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PayloadOwner?.Dispose();
    }
}
