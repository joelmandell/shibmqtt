using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using ShibMqtt.Core.Packets;
using ShibMqtt.Core.Protocol;
using TextEncoding = System.Text.Encoding;

namespace ShibMqtt.Core.Encoding;

/// <summary>
/// High-performance MQTT 3.1.1 packet decoder.
/// Uses <see cref="SequenceReader{T}"/> to parse directly from pipe-supplied buffers with zero copies.
/// </summary>
public static class MqttPacketDecoder
{
    /// <summary>
    /// Attempts to decode one complete MQTT packet from <paramref name="reader"/>.
    /// Returns <c>true</c> and advances the reader past the packet on success.
    /// Returns <c>false</c> if there is not yet enough data (the reader position is unchanged).
    /// </summary>
    public static bool TryDecode(ref SequenceReader<byte> reader, out MqttDecodedPacket packet)
    {
        packet = default;
        var snapshot = reader;

        // ── Fixed header ─────────────────────────────────────────
        if (!reader.TryRead(out byte firstByte))
        {
            reader = snapshot;
            return false;
        }

        var type = (MqttPacketType)(firstByte >> 4);
        byte flags = (byte)(firstByte & 0x0F);

        // ── Remaining length (variable-length encoding) ──────────
        if (!TryDecodeRemainingLength(ref reader, out int remainingLength))
        {
            reader = snapshot;
            return false;
        }

        // ── Ensure full payload is available ─────────────────────
        if (reader.Remaining < remainingLength)
        {
            reader = snapshot;
            return false;
        }

        // Slice a view over exactly the packet body
        var bodySeq = reader.Sequence.Slice(reader.Position, remainingLength);
        reader.Advance(remainingLength);

        packet = type switch
        {
            MqttPacketType.Connect     => DecodeConnect(bodySeq),
            MqttPacketType.ConnAck     => DecodeConnAck(bodySeq),
            MqttPacketType.Publish     => DecodePublish(bodySeq, flags),
            MqttPacketType.PubAck      => DecodePubAck(bodySeq),
            MqttPacketType.PubRec      => DecodePubRec(bodySeq),
            MqttPacketType.PubRel      => DecodePubRel(bodySeq),
            MqttPacketType.PubComp     => DecodePubComp(bodySeq),
            MqttPacketType.Subscribe   => DecodeSubscribe(bodySeq),
            MqttPacketType.SubAck      => DecodeSubAck(bodySeq),
            MqttPacketType.Unsubscribe => DecodeUnsubscribe(bodySeq),
            MqttPacketType.UnsubAck    => DecodeUnsubAck(bodySeq),
            MqttPacketType.PingReq     => new MqttDecodedPacket(new PingReqPacket()),
            MqttPacketType.PingResp    => new MqttDecodedPacket(new PingRespPacket()),
            MqttPacketType.Disconnect  => new MqttDecodedPacket(new DisconnectPacket()),
            _                          => throw new MqttProtocolViolationException($"Unknown packet type: {type}"),
        };

        return true;
    }

    // ──────────────────────────────────────────────────────────────
    //  Packet-specific decoders
    // ──────────────────────────────────────────────────────────────

    private static MqttDecodedPacket DecodeConnect(ReadOnlySequence<byte> body)
    {
        var r = new SequenceReader<byte>(body);

        // Protocol name
        ReadUtf8String(ref r, out var protoName);
        if (protoName != "MQTT")
            throw new MqttProtocolViolationException("Invalid protocol name.");

        // Protocol level
        r.TryRead(out byte level);
        if (level != MqttConstants.ProtocolLevel)
            throw new MqttProtocolViolationException($"Unsupported protocol level: {level}");

        // Connect flags
        r.TryRead(out byte connectFlags);
        bool cleanSession     = (connectFlags & 0x02) != 0;
        bool hasWill          = (connectFlags & 0x04) != 0;
        var willQos           = (MqttQualityOfService)((connectFlags >> 3) & 0x03);
        bool willRetain       = (connectFlags & 0x20) != 0;
        bool hasPassword      = (connectFlags & 0x40) != 0;
        bool hasUsername      = (connectFlags & 0x80) != 0;

        // Keep-alive
        r.TryReadBigEndian(out short keepAliveRaw);
        ushort keepAlive = (ushort)keepAliveRaw;

        // Client ID
        ReadUtf8String(ref r, out var clientId);

        // Will
        WillMessage? will = null;
        if (hasWill)
        {
            ReadUtf8String(ref r, out var willTopic);
            ReadBinaryData(ref r, out var willPayload);
            will = new WillMessage
            {
                Topic = willTopic,
                Payload = willPayload,
                Qos = willQos,
                Retain = willRetain,
            };
        }

        // Username
        string? username = null;
        if (hasUsername) ReadUtf8String(ref r, out username);

        // Password
        ReadOnlyMemory<byte>? password = null;
        if (hasPassword)
        {
            ReadBinaryData(ref r, out var pwdBytes);
            password = pwdBytes;
        }

        var packet = new ConnectPacket
        {
            ClientId = clientId,
            KeepAlive = keepAlive,
            CleanSession = cleanSession,
            Username = username,
            Password = password,
            Will = will,
        };

        return new MqttDecodedPacket(packet);
    }

    private static MqttDecodedPacket DecodeConnAck(ReadOnlySequence<byte> body)
    {
        var r = new SequenceReader<byte>(body);
        r.TryRead(out byte ackFlags);
        r.TryRead(out byte returnCode);

        return new MqttDecodedPacket(new ConnAckPacket
        {
            SessionPresent = (ackFlags & 0x01) != 0,
            ReturnCode = (MqttConnectReturnCode)returnCode,
        });
    }

    private static MqttDecodedPacket DecodePublish(ReadOnlySequence<byte> body, byte flags)
    {
        bool dup    = (flags & 0x08) != 0;
        var qos     = (MqttQualityOfService)((flags >> 1) & 0x03);
        bool retain = (flags & 0x01) != 0;

        var r = new SequenceReader<byte>(body);
        ReadUtf8String(ref r, out var topic);

        ushort packetId = 0;
        if (qos > MqttQualityOfService.AtMostOnce)
        {
            r.TryReadBigEndian(out short idRaw);
            packetId = (ushort)idRaw;
        }

        // Remaining bytes are the payload – use pool to avoid allocation
        int payloadLen = (int)r.Remaining;
        IMemoryOwner<byte>? owner = null;
        ReadOnlyMemory<byte> payload;

        if (payloadLen > 0)
        {
            owner = MemoryPool<byte>.Shared.Rent(payloadLen);
            var mem = owner.Memory[..payloadLen];
            r.Sequence.Slice(r.Position).CopyTo(mem.Span);
            payload = mem;
        }
        else
        {
            payload = ReadOnlyMemory<byte>.Empty;
        }

        return new MqttDecodedPacket(new PublishPacket
        {
            Topic = topic,
            Qos = qos,
            Retain = retain,
            Dup = dup,
            PacketIdentifier = packetId,
            Payload = payload,
            PayloadOwner = owner,
        });
    }

    private static MqttDecodedPacket DecodePubAck(ReadOnlySequence<byte> body)
    {
        var r = new SequenceReader<byte>(body);
        r.TryReadBigEndian(out short id);
        return new MqttDecodedPacket(new PubAckPacket { PacketIdentifier = (ushort)id });
    }

    private static MqttDecodedPacket DecodePubRec(ReadOnlySequence<byte> body)
    {
        var r = new SequenceReader<byte>(body);
        r.TryReadBigEndian(out short id);
        return new MqttDecodedPacket(new PubRecPacket { PacketIdentifier = (ushort)id });
    }

    private static MqttDecodedPacket DecodePubRel(ReadOnlySequence<byte> body)
    {
        var r = new SequenceReader<byte>(body);
        r.TryReadBigEndian(out short id);
        return new MqttDecodedPacket(new PubRelPacket { PacketIdentifier = (ushort)id });
    }

    private static MqttDecodedPacket DecodePubComp(ReadOnlySequence<byte> body)
    {
        var r = new SequenceReader<byte>(body);
        r.TryReadBigEndian(out short id);
        return new MqttDecodedPacket(new PubCompPacket { PacketIdentifier = (ushort)id });
    }

    private static MqttDecodedPacket DecodeSubscribe(ReadOnlySequence<byte> body)
    {
        var r = new SequenceReader<byte>(body);
        r.TryReadBigEndian(out short idRaw);
        ushort packetId = (ushort)idRaw;

        var filters = new List<TopicFilter>();
        while (r.Remaining > 0)
        {
            ReadUtf8String(ref r, out var topic);
            r.TryRead(out byte qosByte);
            filters.Add(new TopicFilter { Topic = topic, MaxQos = (MqttQualityOfService)(qosByte & 0x03) });
        }

        return new MqttDecodedPacket(new SubscribePacket
        {
            PacketIdentifier = packetId,
            TopicFilters = filters,
        });
    }

    private static MqttDecodedPacket DecodeSubAck(ReadOnlySequence<byte> body)
    {
        var r = new SequenceReader<byte>(body);
        r.TryReadBigEndian(out short idRaw);
        ushort packetId = (ushort)idRaw;

        var codes = new List<byte>();
        while (r.Remaining > 0)
        {
            r.TryRead(out byte code);
            codes.Add(code);
        }

        return new MqttDecodedPacket(new SubAckPacket { PacketIdentifier = packetId, ReturnCodes = codes });
    }

    private static MqttDecodedPacket DecodeUnsubscribe(ReadOnlySequence<byte> body)
    {
        var r = new SequenceReader<byte>(body);
        r.TryReadBigEndian(out short idRaw);
        ushort packetId = (ushort)idRaw;

        var topics = new List<string>();
        while (r.Remaining > 0)
        {
            ReadUtf8String(ref r, out var topic);
            topics.Add(topic);
        }

        return new MqttDecodedPacket(new UnsubscribePacket { PacketIdentifier = packetId, TopicFilters = topics });
    }

    private static MqttDecodedPacket DecodeUnsubAck(ReadOnlySequence<byte> body)
    {
        var r = new SequenceReader<byte>(body);
        r.TryReadBigEndian(out short id);
        return new MqttDecodedPacket(new UnsubAckPacket { PacketIdentifier = (ushort)id });
    }

    // ──────────────────────────────────────────────────────────────
    //  Primitive helpers
    // ──────────────────────────────────────────────────────────────

    private static bool TryDecodeRemainingLength(ref SequenceReader<byte> reader, out int value)
    {
        value = 0;
        int multiplier = 1;

        for (int i = 0; i < 4; i++)
        {
            if (!reader.TryRead(out byte encodedByte))
                return false;

            value += (encodedByte & 0x7F) * multiplier;
            if ((encodedByte & 0x80) == 0)
                return true;

            multiplier <<= 7;
        }

        throw new MqttProtocolViolationException("Malformed remaining-length encoding.");
    }

    private static void ReadUtf8String(ref SequenceReader<byte> reader, out string value)
    {
        reader.TryReadBigEndian(out short lenRaw);
        int len = (ushort)lenRaw;

        if (len == 0)
        {
            value = string.Empty;
            return;
        }

        // Fast path: contiguous memory
        if (reader.UnreadSpan.Length >= len)
        {
            value = TextEncoding.UTF8.GetString(reader.UnreadSpan[..len]);
            reader.Advance(len);
            return;
        }

        // Slow path: data spans multiple segments
        byte[]? rented = null;
        Span<byte> buf = len <= 256
            ? stackalloc byte[len]
            : (rented = ArrayPool<byte>.Shared.Rent(len)).AsSpan(0, len);

        try
        {
            reader.TryCopyTo(buf);
            reader.Advance(len);
            value = TextEncoding.UTF8.GetString(buf);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void ReadBinaryData(ref SequenceReader<byte> reader, out ReadOnlyMemory<byte> value)
    {
        reader.TryReadBigEndian(out short lenRaw);
        int len = (ushort)lenRaw;

        if (len == 0)
        {
            value = ReadOnlyMemory<byte>.Empty;
            return;
        }

        var owner = MemoryPool<byte>.Shared.Rent(len);
        var mem = owner.Memory[..len];
        reader.TryCopyTo(mem.Span);
        reader.Advance(len);
        // Intentionally not returning owner here – callers manage lifetime
        value = mem;
    }
}
