using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using ShibMqtt.Core.Packets;
using ShibMqtt.Core.Protocol;
using TextEncoding = System.Text.Encoding;

namespace ShibMqtt.Core.Encoding;

/// <summary>
/// High-performance MQTT 3.1.1 packet encoder.
/// All methods write directly into an <see cref="IBufferWriter{T}"/> to avoid allocations.
/// </summary>
public static class MqttPacketEncoder
{
    // ──────────────────────────────────────────────────────────────
    //  CONNECT
    // ──────────────────────────────────────────────────────────────

    public static void Encode(IBufferWriter<byte> writer, ConnectPacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(packet);

        // Pre-compute variable-header + payload length
        int clientIdLen = Utf8Length(packet.ClientId);
        int willTopicLen = packet.Will is not null ? Utf8Length(packet.Will.Topic) : 0;
        int willPayloadLen = packet.Will?.Payload.Length ?? 0;
        int usernameLen = packet.Username is not null ? Utf8Length(packet.Username) : 0;
        int passwordLen = packet.Password?.Length ?? 0;

        int variableHeaderLen =
            2 + 4         // protocol name length + "MQTT"
            + 1           // protocol level
            + 1           // connect flags
            + 2;          // keep-alive

        int payloadLen =
            2 + clientIdLen;

        if (packet.Will is not null)
        {
            payloadLen += 2 + willTopicLen + 2 + willPayloadLen;
        }

        if (packet.Username is not null)
        {
            payloadLen += 2 + usernameLen;
        }

        if (packet.Password.HasValue)
        {
            payloadLen += 2 + passwordLen;
        }

        int remainingLength = variableHeaderLen + payloadLen;

        // Fixed header (1 byte type/flags)
        WriteFixedHeader(writer, MqttPacketType.Connect, 0, remainingLength);

        Span<byte> header = writer.GetSpan(variableHeaderLen);

        // Protocol name
        int pos = 0;
        BinaryPrimitives.WriteUInt16BigEndian(header[pos..], 4);
        pos += 2;
        MqttConstants.ProtocolName.CopyTo(header[pos..]);
        pos += 4;

        // Protocol level
        header[pos++] = MqttConstants.ProtocolLevel;

        // Connect flags
        byte flags = 0;
        if (packet.CleanSession) flags |= 0x02;
        if (packet.Will is not null)
        {
            flags |= 0x04;
            flags |= (byte)((byte)packet.Will.Qos << 3);
            if (packet.Will.Retain) flags |= 0x20;
        }
        if (packet.Username is not null) flags |= 0x80;
        if (packet.Password.HasValue)    flags |= 0x40;
        header[pos++] = flags;

        // Keep-alive
        BinaryPrimitives.WriteUInt16BigEndian(header[pos..], packet.KeepAlive);
        pos += 2;

        writer.Advance(pos);

        // Payload
        WriteUtf8String(writer, packet.ClientId);

        if (packet.Will is not null)
        {
            WriteUtf8String(writer, packet.Will.Topic);
            WriteBinaryData(writer, packet.Will.Payload.Span);
        }

        if (packet.Username is not null)
        {
            WriteUtf8String(writer, packet.Username);
        }

        if (packet.Password.HasValue)
        {
            WriteBinaryData(writer, packet.Password.Value.Span);
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  CONNACK
    // ──────────────────────────────────────────────────────────────

    public static void Encode(IBufferWriter<byte> writer, in ConnAckPacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);

        Span<byte> span = writer.GetSpan(4);
        span[0] = (byte)((byte)MqttPacketType.ConnAck << 4);
        span[1] = 2; // remaining length
        span[2] = packet.SessionPresent ? (byte)0x01 : (byte)0x00;
        span[3] = (byte)packet.ReturnCode;
        writer.Advance(4);
    }

    // ──────────────────────────────────────────────────────────────
    //  PUBLISH
    // ──────────────────────────────────────────────────────────────

    public static void Encode(IBufferWriter<byte> writer, PublishPacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(packet);

        int topicLen = Utf8Length(packet.Topic);
        int remainingLength = 2 + topicLen + packet.Payload.Length;
        if (packet.Qos > MqttQualityOfService.AtMostOnce)
        {
            remainingLength += 2; // packet identifier
        }

        byte flags = (byte)((byte)packet.Qos << 1);
        if (packet.Retain) flags |= 0x01;
        if (packet.Dup)    flags |= 0x08;

        WriteFixedHeader(writer, MqttPacketType.Publish, flags, remainingLength);
        WriteUtf8String(writer, packet.Topic);

        if (packet.Qos > MqttQualityOfService.AtMostOnce)
        {
            Span<byte> id = writer.GetSpan(2);
            BinaryPrimitives.WriteUInt16BigEndian(id, packet.PacketIdentifier);
            writer.Advance(2);
        }

        if (!packet.Payload.IsEmpty)
        {
            Span<byte> dest = writer.GetSpan(packet.Payload.Length);
            packet.Payload.Span.CopyTo(dest);
            writer.Advance(packet.Payload.Length);
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  PUBACK / PUBREC / PUBREL / PUBCOMP
    // ──────────────────────────────────────────────────────────────

    public static void Encode(IBufferWriter<byte> writer, in PubAckPacket packet)
        => WritePacketIdentifierPacket(writer, MqttPacketType.PubAck, 0, packet.PacketIdentifier);

    public static void Encode(IBufferWriter<byte> writer, in PubRecPacket packet)
        => WritePacketIdentifierPacket(writer, MqttPacketType.PubRec, 0, packet.PacketIdentifier);

    public static void Encode(IBufferWriter<byte> writer, in PubRelPacket packet)
        => WritePacketIdentifierPacket(writer, MqttPacketType.PubRel, 0x02, packet.PacketIdentifier);

    public static void Encode(IBufferWriter<byte> writer, in PubCompPacket packet)
        => WritePacketIdentifierPacket(writer, MqttPacketType.PubComp, 0, packet.PacketIdentifier);

    // ──────────────────────────────────────────────────────────────
    //  SUBSCRIBE
    // ──────────────────────────────────────────────────────────────

    public static void Encode(IBufferWriter<byte> writer, SubscribePacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(packet);

        int payloadLen = 0;
        foreach (var filter in packet.TopicFilters)
        {
            payloadLen += 2 + Utf8Length(filter.Topic) + 1;
        }

        int remainingLength = 2 + payloadLen; // 2 = packet identifier
        WriteFixedHeader(writer, MqttPacketType.Subscribe, 0x02, remainingLength);

        Span<byte> id = writer.GetSpan(2);
        BinaryPrimitives.WriteUInt16BigEndian(id, packet.PacketIdentifier);
        writer.Advance(2);

        foreach (var filter in packet.TopicFilters)
        {
            WriteUtf8String(writer, filter.Topic);
            Span<byte> qosByte = writer.GetSpan(1);
            qosByte[0] = (byte)filter.MaxQos;
            writer.Advance(1);
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  SUBACK
    // ──────────────────────────────────────────────────────────────

    public static void Encode(IBufferWriter<byte> writer, SubAckPacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(packet);

        int remainingLength = 2 + packet.ReturnCodes.Count;
        WriteFixedHeader(writer, MqttPacketType.SubAck, 0, remainingLength);

        Span<byte> id = writer.GetSpan(2);
        BinaryPrimitives.WriteUInt16BigEndian(id, packet.PacketIdentifier);
        writer.Advance(2);

        Span<byte> codes = writer.GetSpan(packet.ReturnCodes.Count);
        for (int i = 0; i < packet.ReturnCodes.Count; i++)
        {
            codes[i] = packet.ReturnCodes[i];
        }
        writer.Advance(packet.ReturnCodes.Count);
    }

    // ──────────────────────────────────────────────────────────────
    //  UNSUBSCRIBE
    // ──────────────────────────────────────────────────────────────

    public static void Encode(IBufferWriter<byte> writer, UnsubscribePacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(packet);

        int payloadLen = 0;
        foreach (var topic in packet.TopicFilters)
        {
            payloadLen += 2 + Utf8Length(topic);
        }

        int remainingLength = 2 + payloadLen;
        WriteFixedHeader(writer, MqttPacketType.Unsubscribe, 0x02, remainingLength);

        Span<byte> id = writer.GetSpan(2);
        BinaryPrimitives.WriteUInt16BigEndian(id, packet.PacketIdentifier);
        writer.Advance(2);

        foreach (var topic in packet.TopicFilters)
        {
            WriteUtf8String(writer, topic);
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  UNSUBACK
    // ──────────────────────────────────────────────────────────────

    public static void Encode(IBufferWriter<byte> writer, in UnsubAckPacket packet)
        => WritePacketIdentifierPacket(writer, MqttPacketType.UnsubAck, 0, packet.PacketIdentifier);

    // ──────────────────────────────────────────────────────────────
    //  PINGREQ / PINGRESP / DISCONNECT
    // ──────────────────────────────────────────────────────────────

    public static void Encode(IBufferWriter<byte> writer, in PingReqPacket _)
        => WriteEmptyPacket(writer, MqttPacketType.PingReq);

    public static void Encode(IBufferWriter<byte> writer, in PingRespPacket _)
        => WriteEmptyPacket(writer, MqttPacketType.PingResp);

    public static void Encode(IBufferWriter<byte> writer, in DisconnectPacket _)
        => WriteEmptyPacket(writer, MqttPacketType.Disconnect);

    // ──────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────

    private static void WriteFixedHeader(IBufferWriter<byte> writer, MqttPacketType type, byte flags, int remainingLength)
    {
        int rlBytes = RemainingLengthByteCount(remainingLength);
        Span<byte> header = writer.GetSpan(1 + rlBytes);
        header[0] = (byte)(((byte)type << 4) | (flags & 0x0F));
        EncodeRemainingLength(header[1..], remainingLength);
        writer.Advance(1 + rlBytes);
    }

    private static void WriteEmptyPacket(IBufferWriter<byte> writer, MqttPacketType type)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Span<byte> span = writer.GetSpan(2);
        span[0] = (byte)((byte)type << 4);
        span[1] = 0;
        writer.Advance(2);
    }

    private static void WritePacketIdentifierPacket(
        IBufferWriter<byte> writer,
        MqttPacketType type,
        byte flags,
        ushort packetIdentifier)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Span<byte> span = writer.GetSpan(4);
        span[0] = (byte)(((byte)type << 4) | (flags & 0x0F));
        span[1] = 2; // remaining length
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], packetIdentifier);
        writer.Advance(4);
    }

    private static void WriteUtf8String(IBufferWriter<byte> writer, string value)
    {
        int byteCount = TextEncoding.UTF8.GetByteCount(value);
        Span<byte> span = writer.GetSpan(2 + byteCount);
        BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)byteCount);
        TextEncoding.UTF8.GetBytes(value, span[2..]);
        writer.Advance(2 + byteCount);
    }

    private static void WriteBinaryData(IBufferWriter<byte> writer, ReadOnlySpan<byte> data)
    {
        Span<byte> span = writer.GetSpan(2 + data.Length);
        BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)data.Length);
        data.CopyTo(span[2..]);
        writer.Advance(2 + data.Length);
    }

    /// <summary>Encode remaining length using the MQTT variable-length encoding.</summary>
    public static void EncodeRemainingLength(Span<byte> dest, int value)
    {
        int i = 0;
        do
        {
            byte encoded = (byte)(value % 128);
            value /= 128;
            if (value > 0) encoded |= 0x80;
            dest[i++] = encoded;
        } while (value > 0);
    }

    /// <summary>Number of bytes needed to encode <paramref name="value"/> as a variable-length integer.</summary>
    public static int RemainingLengthByteCount(int value) => value switch
    {
        <= 127 => 1,
        <= 16_383 => 2,
        <= 2_097_151 => 3,
        _ => 4,
    };

    private static int Utf8Length(string s) => TextEncoding.UTF8.GetByteCount(s);
}
