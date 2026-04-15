using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ShibMqtt.Core.Encoding;
using ShibMqtt.Core.Packets;
using ShibMqtt.Core.Protocol;

namespace ShibMqtt.Broker;

/// <summary>
/// Represents one connected MQTT client session.
/// Uses <see cref="System.IO.Pipelines"/> for zero-copy I/O and a
/// <see cref="Channel{T}"/> for back-pressure-aware outbound queuing.
/// </summary>
public sealed class MqttClientSession : IAsyncDisposable
{
    private static readonly TimeSpan KeepAliveGrace = TimeSpan.FromSeconds(5);

    private readonly Socket _socket;
    private readonly MqttBroker _broker;
    private readonly ILogger _logger;

    // Outbound packet queue – bounded to limit memory under back-pressure
    private readonly Channel<Action<IBufferWriter<byte>>> _outboundChannel =
        Channel.CreateBounded<Action<IBufferWriter<byte>>>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

    private string _clientId = string.Empty;
    private ushort _keepAlive;
    private bool _cleanSession;
    private CancellationTokenSource? _cts;

    public string ClientId => _clientId;
    public bool IsConnected { get; private set; }

    public MqttClientSession(Socket socket, MqttBroker broker, ILogger logger)
    {
        _socket = socket;
        _broker = broker;
        _logger = logger;
    }

    /// <summary>Starts the inbound and outbound processing loops and returns when the session ends.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;

        var networkStream = new NetworkStream(_socket, ownsSocket: false);
        var pipe = new Pipe(new PipeOptions(
            minimumSegmentSize: 4096,
            pauseWriterThreshold: 1024 * 1024,   // 1 MB
            resumeWriterThreshold: 512 * 1024));  // 512 KB

        try
        {
            await Task.WhenAll(
                FillPipeAsync(networkStream, pipe.Writer, token),
                ReadPipeAsync(pipe.Reader, token),
                WritePipeAsync(networkStream, token));
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{ClientId}] Session error", _clientId);
        }
        finally
        {
            IsConnected = false;
            _outboundChannel.Writer.TryComplete();

            if (!string.IsNullOrEmpty(_clientId))
                _broker.OnSessionEnded(_clientId, _cleanSession);

            _socket.Dispose();
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  Network → Pipe
    // ──────────────────────────────────────────────────────────────

    private static async Task FillPipeAsync(NetworkStream stream, PipeWriter writer, CancellationToken ct)
    {
        const int minimumBufferSize = 4096;
        try
        {
            while (true)
            {
                var memory = writer.GetMemory(minimumBufferSize);
                int bytesRead = await stream.ReadAsync(memory, ct);
                if (bytesRead == 0) break;

                writer.Advance(bytesRead);
                var result = await writer.FlushAsync(ct);
                if (result.IsCompleted || result.IsCanceled) break;
            }
        }
        finally
        {
            await writer.CompleteAsync();
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  Pipe → Packet dispatch
    // ──────────────────────────────────────────────────────────────

    private async Task ReadPipeAsync(PipeReader reader, CancellationToken ct)
    {
        DateTime? deadline = null;

        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(ct);
                var buffer = result.Buffer;

                while (TryDecodePacket(ref buffer, out var decoded))
                {
                    await HandlePacketAsync(decoded, ct);

                    if (_keepAlive > 0)
                        deadline = DateTime.UtcNow.AddSeconds(_keepAlive) + KeepAliveGrace;
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted || result.IsCanceled) break;

                if (deadline.HasValue && DateTime.UtcNow > deadline.Value)
                {
                    _logger.LogWarning("[{ClientId}] Keep-alive timeout", _clientId);
                    break;
                }
            }
        }
        finally
        {
            await reader.CompleteAsync();
            _cts?.Cancel();
        }
    }

    private static bool TryDecodePacket(ref ReadOnlySequence<byte> buffer, out MqttDecodedPacket packet)
    {
        var reader = new SequenceReader<byte>(buffer);
        if (MqttPacketDecoder.TryDecode(ref reader, out packet))
        {
            buffer = buffer.Slice(reader.Position);
            return true;
        }
        return false;
    }

    // ──────────────────────────────────────────────────────────────
    //  Packet handlers
    // ──────────────────────────────────────────────────────────────

    private async ValueTask HandlePacketAsync(MqttDecodedPacket decoded, CancellationToken ct)
    {
        switch (decoded.PacketType)
        {
            case MqttPacketType.Connect:
                await HandleConnectAsync(decoded.AsConnect(), ct);
                break;

            case MqttPacketType.Publish:
                await HandlePublishAsync(decoded.AsPublish(), ct);
                break;

            case MqttPacketType.PubAck:
                // QoS 1 ack – tracked per session in a full implementation
                break;

            case MqttPacketType.PubRec:
                var pubRec = decoded.AsPubRec();
                await EnqueueAsync(w => MqttPacketEncoder.Encode(w, new PubRelPacket { PacketIdentifier = pubRec.PacketIdentifier }), ct);
                break;

            case MqttPacketType.PubRel:
                var pubRel = decoded.AsPubRel();
                await EnqueueAsync(w => MqttPacketEncoder.Encode(w, new PubCompPacket { PacketIdentifier = pubRel.PacketIdentifier }), ct);
                break;

            case MqttPacketType.PubComp:
                // QoS 2 complete – tracked per session in a full implementation
                break;

            case MqttPacketType.Subscribe:
                await HandleSubscribeAsync(decoded.AsSubscribe(), ct);
                break;

            case MqttPacketType.Unsubscribe:
                await HandleUnsubscribeAsync(decoded.AsUnsubscribe(), ct);
                break;

            case MqttPacketType.PingReq:
                await EnqueueAsync(w => MqttPacketEncoder.Encode(w, new PingRespPacket()), ct);
                break;

            case MqttPacketType.Disconnect:
                _cts?.Cancel();
                break;

            default:
                _logger.LogWarning("[{ClientId}] Unexpected packet: {Type}", _clientId, decoded.PacketType);
                break;
        }
    }

    private async ValueTask HandleConnectAsync(ConnectPacket connect, CancellationToken ct)
    {
        _clientId = connect.ClientId;
        _keepAlive = connect.KeepAlive;
        _cleanSession = connect.CleanSession;

        var returnCode = _broker.OnClientConnecting(this, connect);
        bool sessionPresent = !connect.CleanSession && returnCode == MqttConnectReturnCode.Accepted
            && _broker.HasSession(_clientId);

        var connAck = new ConnAckPacket { SessionPresent = sessionPresent, ReturnCode = returnCode };
        await EnqueueAsync(w => MqttPacketEncoder.Encode(w, connAck), ct);

        if (returnCode != MqttConnectReturnCode.Accepted)
        {
            _cts?.Cancel();
            return;
        }

        IsConnected = true;
    }

    private async ValueTask HandlePublishAsync(PublishPacket publish, CancellationToken ct)
    {
        if (publish.Retain)
            _broker.SubscriptionManager.SetRetained(publish.Topic, publish);

        await _broker.DispatchAsync(publish, ct);

        switch (publish.Qos)
        {
            case MqttQualityOfService.AtLeastOnce:
                await EnqueueAsync(w => MqttPacketEncoder.Encode(w, new PubAckPacket { PacketIdentifier = publish.PacketIdentifier }), ct);
                break;
            case MqttQualityOfService.ExactlyOnce:
                await EnqueueAsync(w => MqttPacketEncoder.Encode(w, new PubRecPacket { PacketIdentifier = publish.PacketIdentifier }), ct);
                break;
        }
    }

    private async ValueTask HandleSubscribeAsync(SubscribePacket subscribe, CancellationToken ct)
    {
        var returnCodes = _broker.SubscriptionManager.Subscribe(_clientId, subscribe.TopicFilters);
        var subAck = new SubAckPacket { PacketIdentifier = subscribe.PacketIdentifier, ReturnCodes = returnCodes };
        await EnqueueAsync(w => MqttPacketEncoder.Encode(w, subAck), ct);

        // Deliver retained messages for each matching filter
        foreach (var filter in subscribe.TopicFilters)
        {
            foreach (var retained in _broker.SubscriptionManager.GetRetainedMessages(filter.Topic))
            {
                var copy = retained; // capture
                await EnqueueAsync(w => MqttPacketEncoder.Encode(w, copy), ct);
            }
        }
    }

    private async ValueTask HandleUnsubscribeAsync(UnsubscribePacket unsubscribe, CancellationToken ct)
    {
        _broker.SubscriptionManager.Unsubscribe(_clientId, unsubscribe.TopicFilters);
        await EnqueueAsync(w => MqttPacketEncoder.Encode(w, new UnsubAckPacket { PacketIdentifier = unsubscribe.PacketIdentifier }), ct);
    }

    // ──────────────────────────────────────────────────────────────
    //  Outbound publishing
    // ──────────────────────────────────────────────────────────────

    /// <summary>Enqueues a publish for delivery to this client.</summary>
    public async ValueTask DeliverAsync(PublishPacket packet, CancellationToken ct)
    {
        await EnqueueAsync(w => MqttPacketEncoder.Encode(w, packet), ct);
    }

    private ValueTask EnqueueAsync(Action<IBufferWriter<byte>> write, CancellationToken ct)
        => _outboundChannel.Writer.WriteAsync(write, ct);

    // ──────────────────────────────────────────────────────────────
    //  Outbound writer loop
    // ──────────────────────────────────────────────────────────────

    private async Task WritePipeAsync(NetworkStream stream, CancellationToken ct)
    {
        // Use a pooled ArrayBufferWriter for zero-alloc outbound serialisation
        var buffer = new ArrayBufferWriter<byte>(4096);

        try
        {
            await foreach (var write in _outboundChannel.Reader.ReadAllAsync(ct))
            {
                buffer.Clear();
                write(buffer);

                await stream.WriteAsync(buffer.WrittenMemory, ct);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        await ValueTask.CompletedTask;
    }
}
