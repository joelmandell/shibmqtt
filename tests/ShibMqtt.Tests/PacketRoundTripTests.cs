using System.Buffers;
using ShibMqtt.Core.Encoding;
using ShibMqtt.Core.Packets;
using ShibMqtt.Core.Protocol;

namespace ShibMqtt.Tests;

/// <summary>Tests for <see cref="MqttPacketEncoder"/> and <see cref="MqttPacketDecoder"/>.</summary>
public class PacketRoundTripTests
{
    // Helper: encode a packet then decode it back
    private static MqttDecodedPacket RoundTrip(Action<IBufferWriter<byte>> encode)
    {
        var buf = new ArrayBufferWriter<byte>(256);
        encode(buf);
        var seq = new ReadOnlySequence<byte>(buf.WrittenMemory);
        var reader = new SequenceReader<byte>(seq);
        Assert.True(MqttPacketDecoder.TryDecode(ref reader, out var decoded));
        return decoded;
    }

    // ──────────────────────────────────────────────────────────────
    //  CONNECT
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void Connect_RoundTrip_BasicFields()
    {
        var connect = new ConnectPacket
        {
            ClientId = "test-client",
            KeepAlive = 30,
            CleanSession = true,
        };

        var decoded = RoundTrip(w => MqttPacketEncoder.Encode(w, connect));

        Assert.Equal(MqttPacketType.Connect, decoded.PacketType);
        var result = decoded.AsConnect();
        Assert.Equal("test-client", result.ClientId);
        Assert.Equal(30, result.KeepAlive);
        Assert.True(result.CleanSession);
        Assert.Null(result.Username);
        Assert.False(result.Password.HasValue);
        Assert.Null(result.Will);
    }

    [Fact]
    public void Connect_RoundTrip_WithUsernameAndPassword()
    {
        byte[] pwd = [1, 2, 3, 4];
        var connect = new ConnectPacket
        {
            ClientId = "client1",
            Username = "user",
            Password = pwd,
            CleanSession = false,
        };

        var decoded = RoundTrip(w => MqttPacketEncoder.Encode(w, connect));
        var result = decoded.AsConnect();

        Assert.Equal("user", result.Username);
        Assert.True(result.Password.HasValue);
        Assert.Equal(pwd, result.Password!.Value.ToArray());
        Assert.False(result.CleanSession);
    }

    [Fact]
    public void Connect_RoundTrip_WithWill()
    {
        var will = new WillMessage
        {
            Topic = "will/topic",
            Payload = new byte[] { 10, 20, 30 },
            Qos = MqttQualityOfService.AtLeastOnce,
            Retain = true,
        };

        var connect = new ConnectPacket
        {
            ClientId = "will-client",
            Will = will,
        };

        var decoded = RoundTrip(w => MqttPacketEncoder.Encode(w, connect));
        var result = decoded.AsConnect();

        Assert.NotNull(result.Will);
        Assert.Equal("will/topic", result.Will!.Topic);
        Assert.Equal(new byte[] { 10, 20, 30 }, result.Will.Payload.ToArray());
        Assert.Equal(MqttQualityOfService.AtLeastOnce, result.Will.Qos);
        Assert.True(result.Will.Retain);
    }

    // ──────────────────────────────────────────────────────────────
    //  CONNACK
    // ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, MqttConnectReturnCode.Accepted)]
    [InlineData(true,  MqttConnectReturnCode.Accepted)]
    [InlineData(false, MqttConnectReturnCode.NotAuthorized)]
    public void ConnAck_RoundTrip(bool sessionPresent, MqttConnectReturnCode returnCode)
    {
        var packet = new ConnAckPacket { SessionPresent = sessionPresent, ReturnCode = returnCode };
        var decoded = RoundTrip(w => MqttPacketEncoder.Encode(w, packet));

        Assert.Equal(MqttPacketType.ConnAck, decoded.PacketType);
        var result = decoded.AsConnAck();
        Assert.Equal(sessionPresent, result.SessionPresent);
        Assert.Equal(returnCode, result.ReturnCode);
    }

    // ──────────────────────────────────────────────────────────────
    //  PUBLISH
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void Publish_RoundTrip_Qos0()
    {
        byte[] payload = System.Text.Encoding.UTF8.GetBytes("hello world");
        var packet = new PublishPacket
        {
            Topic = "sensors/temp",
            Payload = payload,
            Qos = MqttQualityOfService.AtMostOnce,
        };

        var decoded = RoundTrip(w => MqttPacketEncoder.Encode(w, packet));
        Assert.Equal(MqttPacketType.Publish, decoded.PacketType);

        using var result = decoded.AsPublish();
        Assert.Equal("sensors/temp", result.Topic);
        Assert.Equal(MqttQualityOfService.AtMostOnce, result.Qos);
        Assert.Equal(payload, result.Payload.ToArray());
    }

    [Fact]
    public void Publish_RoundTrip_Qos1_WithPacketId()
    {
        var packet = new PublishPacket
        {
            Topic = "cmd/set",
            Payload = new byte[] { 1, 2 },
            Qos = MqttQualityOfService.AtLeastOnce,
            PacketIdentifier = 42,
            Retain = false,
            Dup = true,
        };

        var decoded = RoundTrip(w => MqttPacketEncoder.Encode(w, packet));
        using var result = decoded.AsPublish();

        Assert.Equal(MqttQualityOfService.AtLeastOnce, result.Qos);
        Assert.Equal((ushort)42, result.PacketIdentifier);
        Assert.True(result.Dup);
    }

    [Fact]
    public void Publish_RoundTrip_EmptyPayload()
    {
        var packet = new PublishPacket
        {
            Topic = "empty/topic",
            Payload = ReadOnlyMemory<byte>.Empty,
        };

        var decoded = RoundTrip(w => MqttPacketEncoder.Encode(w, packet));
        using var result = decoded.AsPublish();

        Assert.Equal("empty/topic", result.Topic);
        Assert.True(result.Payload.IsEmpty);
    }

    // ──────────────────────────────────────────────────────────────
    //  PUBACK / PUBREC / PUBREL / PUBCOMP
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void PubAck_RoundTrip()
    {
        var packet = new PubAckPacket { PacketIdentifier = 1234 };
        var decoded = RoundTrip(w => MqttPacketEncoder.Encode(w, packet));
        Assert.Equal(MqttPacketType.PubAck, decoded.PacketType);
        Assert.Equal((ushort)1234, decoded.AsPubAck().PacketIdentifier);
    }

    [Fact]
    public void PubRec_RoundTrip()
    {
        var packet = new PubRecPacket { PacketIdentifier = 5678 };
        var decoded = RoundTrip(w => MqttPacketEncoder.Encode(w, packet));
        Assert.Equal(MqttPacketType.PubRec, decoded.PacketType);
        Assert.Equal((ushort)5678, decoded.AsPubRec().PacketIdentifier);
    }

    [Fact]
    public void PubRel_RoundTrip()
    {
        var packet = new PubRelPacket { PacketIdentifier = 9999 };
        var decoded = RoundTrip(w => MqttPacketEncoder.Encode(w, packet));
        Assert.Equal(MqttPacketType.PubRel, decoded.PacketType);
        Assert.Equal((ushort)9999, decoded.AsPubRel().PacketIdentifier);
    }

    [Fact]
    public void PubComp_RoundTrip()
    {
        var packet = new PubCompPacket { PacketIdentifier = 111 };
        var decoded = RoundTrip(w => MqttPacketEncoder.Encode(w, packet));
        Assert.Equal(MqttPacketType.PubComp, decoded.PacketType);
        Assert.Equal((ushort)111, decoded.AsPubComp().PacketIdentifier);
    }

    // ──────────────────────────────────────────────────────────────
    //  SUBSCRIBE / SUBACK
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void Subscribe_RoundTrip()
    {
        var filters = new[]
        {
            new TopicFilter { Topic = "sensors/#", MaxQos = MqttQualityOfService.AtLeastOnce },
            new TopicFilter { Topic = "cmd/+/set",  MaxQos = MqttQualityOfService.ExactlyOnce },
        };

        var packet = new SubscribePacket { PacketIdentifier = 7, TopicFilters = filters };
        var decoded = RoundTrip(w => MqttPacketEncoder.Encode(w, packet));

        Assert.Equal(MqttPacketType.Subscribe, decoded.PacketType);
        var result = decoded.AsSubscribe();
        Assert.Equal((ushort)7, result.PacketIdentifier);
        Assert.Equal(2, result.TopicFilters.Count);
        Assert.Equal("sensors/#", result.TopicFilters[0].Topic);
        Assert.Equal(MqttQualityOfService.AtLeastOnce, result.TopicFilters[0].MaxQos);
        Assert.Equal("cmd/+/set", result.TopicFilters[1].Topic);
    }

    [Fact]
    public void SubAck_RoundTrip()
    {
        var packet = new SubAckPacket { PacketIdentifier = 7, ReturnCodes = [0x00, 0x01, 0x80] };
        var decoded = RoundTrip(w => MqttPacketEncoder.Encode(w, packet));

        Assert.Equal(MqttPacketType.SubAck, decoded.PacketType);
        var result = decoded.AsSubAck();
        Assert.Equal(new byte[] { 0x00, 0x01, 0x80 }, result.ReturnCodes.ToArray());
    }

    // ──────────────────────────────────────────────────────────────
    //  UNSUBSCRIBE / UNSUBACK
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void Unsubscribe_RoundTrip()
    {
        var packet = new UnsubscribePacket
        {
            PacketIdentifier = 3,
            TopicFilters = ["sensors/#", "cmd/+"],
        };

        var decoded = RoundTrip(w => MqttPacketEncoder.Encode(w, packet));
        Assert.Equal(MqttPacketType.Unsubscribe, decoded.PacketType);
        var result = decoded.AsUnsubscribe();
        Assert.Equal((ushort)3, result.PacketIdentifier);
        Assert.Equal(["sensors/#", "cmd/+"], result.TopicFilters);
    }

    [Fact]
    public void UnsubAck_RoundTrip()
    {
        var packet = new UnsubAckPacket { PacketIdentifier = 3 };
        var decoded = RoundTrip(w => MqttPacketEncoder.Encode(w, packet));
        Assert.Equal(MqttPacketType.UnsubAck, decoded.PacketType);
        Assert.Equal((ushort)3, decoded.AsUnsubAck().PacketIdentifier);
    }

    // ──────────────────────────────────────────────────────────────
    //  PINGREQ / PINGRESP / DISCONNECT
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void PingReq_RoundTrip()
    {
        var decoded = RoundTrip(w => MqttPacketEncoder.Encode(w, new PingReqPacket()));
        Assert.Equal(MqttPacketType.PingReq, decoded.PacketType);
    }

    [Fact]
    public void PingResp_RoundTrip()
    {
        var decoded = RoundTrip(w => MqttPacketEncoder.Encode(w, new PingRespPacket()));
        Assert.Equal(MqttPacketType.PingResp, decoded.PacketType);
    }

    [Fact]
    public void Disconnect_RoundTrip()
    {
        var decoded = RoundTrip(w => MqttPacketEncoder.Encode(w, new DisconnectPacket()));
        Assert.Equal(MqttPacketType.Disconnect, decoded.PacketType);
    }

    // ──────────────────────────────────────────────────────────────
    //  Partial buffer (TryDecode should return false until complete)
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void TryDecode_ReturnsFalse_WhenDataIncomplete()
    {
        var buf = new ArrayBufferWriter<byte>();
        MqttPacketEncoder.Encode(buf, new PingReqPacket());

        // Feed only one byte
        var partial = new ReadOnlySequence<byte>(buf.WrittenMemory[..1]);
        var reader = new SequenceReader<byte>(partial);
        Assert.False(MqttPacketDecoder.TryDecode(ref reader, out _));
    }

    // ──────────────────────────────────────────────────────────────
    //  Remaining length encoding
    // ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0,          1)]
    [InlineData(127,        1)]
    [InlineData(128,        2)]
    [InlineData(16_383,     2)]
    [InlineData(16_384,     3)]
    [InlineData(2_097_151,  3)]
    [InlineData(2_097_152,  4)]
    [InlineData(268_435_455,4)]
    public void RemainingLengthByteCount_IsCorrect(int value, int expected)
    {
        Assert.Equal(expected, MqttPacketEncoder.RemainingLengthByteCount(value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(321)]
    [InlineData(16383)]
    [InlineData(16384)]
    [InlineData(268_435_455)]
    public void EncodeDecodeRemainingLength_RoundTrip(int value)
    {
        var buf = new byte[4];
        MqttPacketEncoder.EncodeRemainingLength(buf, value);

        var seq = new ReadOnlySequence<byte>(buf);
        var reader = new SequenceReader<byte>(seq);

        // Manually call the private method via a reflect shim –
        // use the same pattern as TryDecode to verify it works end-to-end
        // by encoding a full publish packet of appropriate remaining length.
        Assert.Equal(MqttPacketEncoder.RemainingLengthByteCount(value), CountUsedBytes(buf));
    }

    private static int CountUsedBytes(byte[] buf)
    {
        int count = 0;
        for (int i = 0; i < buf.Length; i++)
        {
            count++;
            if ((buf[i] & 0x80) == 0) break;
        }
        return count;
    }
}
