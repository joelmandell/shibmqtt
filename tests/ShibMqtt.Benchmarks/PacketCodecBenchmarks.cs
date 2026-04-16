using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using ShibMqtt.Core.Encoding;
using ShibMqtt.Core.Packets;
using ShibMqtt.Core.Protocol;

namespace ShibMqtt.Benchmarks;

[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class PacketCodecBenchmarks
{
    private readonly ArrayBufferWriter<byte> _writer = new(4096);
    private byte[] _encodedPublish = [];
    private PublishPacket _publishPacket = null!;

    [Params(32, 256, 1024)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        byte[] payload = new byte[PayloadSize];
        for (int i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i % 251);

        _publishPacket = new PublishPacket
        {
            Topic = "bench/site-3/device-12/temp",
            Payload = payload,
            Qos = MqttQualityOfService.AtLeastOnce,
            PacketIdentifier = 42,
        };

        _writer.Clear();
        MqttPacketEncoder.Encode(_writer, _publishPacket);
        _encodedPublish = _writer.WrittenMemory.ToArray();
    }

    [Benchmark]
    public int EncodePublish()
    {
        _writer.Clear();
        MqttPacketEncoder.Encode(_writer, _publishPacket);
        return _writer.WrittenCount;
    }

    [Benchmark]
    public int DecodePublish()
    {
        var sequence = new ReadOnlySequence<byte>(_encodedPublish);
        var reader = new SequenceReader<byte>(sequence);

        if (!MqttPacketDecoder.TryDecode(ref reader, out var decoded))
            throw new InvalidOperationException("Benchmark payload did not decode.");

        using var publish = decoded.AsPublish();
        return publish.Payload.Length;
    }
}
