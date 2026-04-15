using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using ShibMqtt.Core.Encoding;
using ShibMqtt.Core.Packets;
using ShibMqtt.Core.Protocol;

namespace ShibMqtt.Client;

/// <summary>
/// High-performance MQTT 3.1.1 client.
/// Uses <see cref="System.IO.Pipelines"/> for zero-copy network I/O and
/// <see cref="Channel{T}"/> for ordered packet dispatch.
/// </summary>
public sealed class MqttClient : IAsyncDisposable
{
    private readonly MqttClientOptions _options;

    private Socket? _socket;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;

    // Pending request completions keyed by packet identifier (QoS ack tracking)
    private readonly Dictionary<ushort, TaskCompletionSource<bool>> _pendingAcks = new();
    private ushort _nextPacketId = 1;

    // Inbound published messages
    private readonly Channel<PublishPacket> _inboundChannel = Channel.CreateUnbounded<PublishPacket>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });

    // Reusable write buffer (not thread-safe – all writes come from the caller)
    private readonly ArrayBufferWriter<byte> _writeBuffer = new(4096);

    public bool IsConnected { get; private set; }

    /// <summary>Receives all inbound PUBLISH messages as an async sequence.</summary>
    public IAsyncEnumerable<PublishPacket> Messages => _inboundChannel.Reader.ReadAllAsync();

    public MqttClient(MqttClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    // ──────────────────────────────────────────────────────────────
    //  Connect / Disconnect
    // ──────────────────────────────────────────────────────────────

    /// <summary>Establishes a TCP connection to the broker and sends CONNECT.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
            throw new InvalidOperationException("Already connected.");

        var endPoints = await Dns.GetHostAddressesAsync(_options.Host, cancellationToken);
        if (endPoints.Length == 0)
            throw new InvalidOperationException($"Could not resolve host: {_options.Host}");

        _socket = new Socket(endPoints[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        await _socket.ConnectAsync(new IPEndPoint(endPoints[0], _options.Port), cancellationToken);
        _stream = new NetworkStream(_socket, ownsSocket: false);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Send CONNECT
        var connectPacket = new ConnectPacket
        {
            ClientId = _options.ClientId,
            KeepAlive = _options.KeepAlive,
            CleanSession = _options.CleanSession,
            Username = _options.Username,
            Password = _options.Password,
            Will = _options.Will,
        };

        await SendPacketAsync(w => MqttPacketEncoder.Encode(w, connectPacket), cancellationToken);

        // Wait for CONNACK
        using var timeoutCts = new CancellationTokenSource(_options.ConnectTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var connAck = await ReceiveConnAckAsync(linkedCts.Token);
        if (!connAck.IsSuccess)
            throw new MqttConnectException($"CONNACK failed: {connAck.ReturnCode}", connAck.ReturnCode);

        IsConnected = true;

        // Start background receive loop
        _receiveLoop = Task.Run(() => RunReceiveLoopAsync(_cts.Token), _cts.Token);
    }

    /// <summary>Sends DISCONNECT and closes the connection.</summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected) return;

        try
        {
            await SendPacketAsync(w => MqttPacketEncoder.Encode(w, new DisconnectPacket()), cancellationToken);
        }
        finally
        {
            await CloseAsync();
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  Publish
    // ──────────────────────────────────────────────────────────────

    /// <summary>Publishes a message. Awaits QoS acknowledgment for QoS 1/2.</summary>
    public async Task PublishAsync(
        string topic,
        ReadOnlyMemory<byte> payload,
        MqttQualityOfService qos = MqttQualityOfService.AtMostOnce,
        bool retain = false,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        ushort packetId = qos > MqttQualityOfService.AtMostOnce ? NextPacketId() : (ushort)0;

        var packet = new PublishPacket
        {
            Topic = topic,
            Payload = payload,
            Qos = qos,
            Retain = retain,
            PacketIdentifier = packetId,
        };

        if (qos == MqttQualityOfService.AtMostOnce)
        {
            await SendPacketAsync(w => MqttPacketEncoder.Encode(w, packet), cancellationToken);
            return;
        }

        // QoS 1: wait for PUBACK
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingAcks) _pendingAcks[packetId] = tcs;

        await SendPacketAsync(w => MqttPacketEncoder.Encode(w, packet), cancellationToken);

        using var reg = cancellationToken.Register(() => tcs.TrySetCanceled());
        await tcs.Task;
    }

    // ──────────────────────────────────────────────────────────────
    //  Subscribe / Unsubscribe
    // ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<byte>> SubscribeAsync(
        IReadOnlyList<TopicFilter> filters,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        ushort packetId = NextPacketId();
        var tcs = new TaskCompletionSource<SubAckPacket>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_pendingAcks)
            _pendingSubAcks[packetId] = tcs;

        var packet = new SubscribePacket { PacketIdentifier = packetId, TopicFilters = filters };
        await SendPacketAsync(w => MqttPacketEncoder.Encode(w, packet), cancellationToken);

        using var reg = cancellationToken.Register(() => tcs.TrySetCanceled());
        var subAck = await tcs.Task;
        return subAck.ReturnCodes;
    }

    public async Task UnsubscribeAsync(IReadOnlyList<string> topics, CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        ushort packetId = NextPacketId();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_pendingAcks)
            _pendingUnsubAcks[packetId] = tcs;

        var packet = new UnsubscribePacket { PacketIdentifier = packetId, TopicFilters = topics };
        await SendPacketAsync(w => MqttPacketEncoder.Encode(w, packet), cancellationToken);

        using var reg = cancellationToken.Register(() => tcs.TrySetCanceled());
        await tcs.Task;
    }

    // Separate dictionaries for SUBACK and UNSUBACK
    private readonly Dictionary<ushort, TaskCompletionSource<SubAckPacket>> _pendingSubAcks = new();
    private readonly Dictionary<ushort, TaskCompletionSource<bool>> _pendingUnsubAcks = new();

    // ──────────────────────────────────────────────────────────────
    //  Receive loop
    // ──────────────────────────────────────────────────────────────

    private async Task RunReceiveLoopAsync(CancellationToken ct)
    {
        var pipe = new Pipe(new PipeOptions(minimumSegmentSize: 4096));
        var fillTask = FillPipeAsync(_stream!, pipe.Writer, ct);
        var readTask = ReadPipeAsync(pipe.Reader, ct);
        await Task.WhenAll(fillTask, readTask);
    }

    private static async Task FillPipeAsync(NetworkStream stream, PipeWriter writer, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var mem = writer.GetMemory(4096);
                int read = await stream.ReadAsync(mem, ct);
                if (read == 0) break;
                writer.Advance(read);
                var flush = await writer.FlushAsync(ct);
                if (flush.IsCompleted || flush.IsCanceled) break;
            }
        }
        finally { await writer.CompleteAsync(); }
    }

    private async Task ReadPipeAsync(PipeReader reader, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(ct);
                var buffer = result.Buffer;

                while (TryDecodePacket(ref buffer, out var decoded))
                {
                    HandleInboundPacket(decoded);
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted || result.IsCanceled) break;
            }
        }
        finally { await reader.CompleteAsync(); }
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

    private void HandleInboundPacket(MqttDecodedPacket decoded)
    {
        switch (decoded.PacketType)
        {
            case MqttPacketType.Publish:
                _inboundChannel.Writer.TryWrite(decoded.AsPublish());
                break;

            case MqttPacketType.PubAck:
                var pubAck = decoded.AsPubAck();
                TaskCompletionSource<bool>? tcs;
                lock (_pendingAcks)
                {
                    _pendingAcks.TryGetValue(pubAck.PacketIdentifier, out tcs);
                    _pendingAcks.Remove(pubAck.PacketIdentifier);
                }
                tcs?.TrySetResult(true);
                break;

            case MqttPacketType.SubAck:
                var subAck = decoded.AsSubAck();
                TaskCompletionSource<SubAckPacket>? subTcs;
                lock (_pendingAcks)
                {
                    _pendingSubAcks.TryGetValue(subAck.PacketIdentifier, out subTcs);
                    _pendingSubAcks.Remove(subAck.PacketIdentifier);
                }
                subTcs?.TrySetResult(subAck);
                break;

            case MqttPacketType.UnsubAck:
                var unsubAck = decoded.AsUnsubAck();
                TaskCompletionSource<bool>? unsubTcs;
                lock (_pendingAcks)
                {
                    _pendingUnsubAcks.TryGetValue(unsubAck.PacketIdentifier, out unsubTcs);
                    _pendingUnsubAcks.Remove(unsubAck.PacketIdentifier);
                }
                unsubTcs?.TrySetResult(true);
                break;

            case MqttPacketType.PingResp:
                break; // keep-alive response, no action needed

            default:
                break;
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────

    private async ValueTask<ConnAckPacket> ReceiveConnAckAsync(CancellationToken ct)
    {
        // Read bytes until we have a full CONNACK (always 4 bytes)
        byte[] buf = new byte[4];
        int read = 0;
        while (read < 4)
        {
            int n = await _stream!.ReadAsync(buf.AsMemory(read), ct);
            if (n == 0) throw new IOException("Connection closed before CONNACK.");
            read += n;
        }

        var seq = new ReadOnlySequence<byte>(buf);
        var reader = new SequenceReader<byte>(seq);
        if (!MqttPacketDecoder.TryDecode(ref reader, out var decoded) ||
            decoded.PacketType != MqttPacketType.ConnAck)
        {
            throw new MqttProtocolViolationException("Expected CONNACK, received something else.");
        }

        return decoded.AsConnAck();
    }

    private async ValueTask SendPacketAsync(Action<IBufferWriter<byte>> write, CancellationToken ct)
    {
        _writeBuffer.Clear();
        write(_writeBuffer);
        await _stream!.WriteAsync(_writeBuffer.WrittenMemory, ct);
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
            throw new InvalidOperationException("Not connected.");
    }

    private ushort NextPacketId()
    {
        if (_nextPacketId == 0) _nextPacketId = 1; // skip 0
        return _nextPacketId++;
    }

    private async Task CloseAsync()
    {
        IsConnected = false;
        _cts?.Cancel();
        _inboundChannel.Writer.TryComplete();

        if (_receiveLoop is not null)
        {
            try { await _receiveLoop; }
            catch (OperationCanceledException) { }
        }

        _stream?.Dispose();
        _socket?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _cts?.Dispose();
    }
}
