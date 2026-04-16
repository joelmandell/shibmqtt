using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ShibMqtt.Core.Packets;
using ShibMqtt.Core.Protocol;

namespace ShibMqtt.Broker;

/// <summary>
/// High-performance MQTT 3.1.1 broker.
/// Accepts TCP connections and spawns a <see cref="MqttClientSession"/> per client.
/// </summary>
public sealed class MqttBroker : IAsyncDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MqttBroker> _logger;
    private readonly ConcurrentDictionary<string, MqttClientSession> _sessions = new();
    private CancellationTokenSource? _cts;
    private Socket? _listener;

    public SubscriptionManager SubscriptionManager { get; } = new();

    public MqttBroker(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<MqttBroker>();
    }

    /// <summary>Starts the broker on the given endpoint.</summary>
    public async Task StartAsync(IPEndPoint endpoint, CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _listener = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Bind(endpoint);
        _listener.Listen(128);

        _logger.LogInformation("ShibMqtt broker listening on {Endpoint}", endpoint);

        _ = AcceptLoopAsync(_cts.Token);
        await Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener!.AcceptAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Accept error");
                continue;
            }

            client.NoDelay = true;

            var session = new MqttClientSession(
                client,
                this,
                _loggerFactory.CreateLogger<MqttClientSession>());

            _ = Task.Run(() => session.RunAsync(ct), ct);
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  Session lifecycle callbacks (called by MqttClientSession)
    // ──────────────────────────────────────────────────────────────

    internal (MqttConnectReturnCode ReturnCode, bool SessionPresent) OnClientConnecting(
        MqttClientSession session,
        ConnectPacket connect)
    {
        bool sessionPresent = !connect.CleanSession && SubscriptionManager.HasSubscriptions(connect.ClientId);

        // Disconnect existing session with the same client ID
        if (_sessions.TryRemove(connect.ClientId, out var existing))
        {
            _ = existing.DisposeAsync().AsTask();
        }

        _sessions[connect.ClientId] = session;
        _logger.LogInformation("Client connected: {ClientId}", connect.ClientId);

        return (MqttConnectReturnCode.Accepted, sessionPresent);
    }

    internal void OnSessionEnded(string clientId, bool cleanSession)
    {
        _sessions.TryRemove(clientId, out _);

        if (cleanSession)
            SubscriptionManager.RemoveClient(clientId);

        _logger.LogInformation("Client disconnected: {ClientId}", clientId);
    }

    // ──────────────────────────────────────────────────────────────
    //  Message dispatch
    // ──────────────────────────────────────────────────────────────

    internal async ValueTask DispatchAsync(PublishPacket publish, CancellationToken ct)
    {
        var subscribers = SubscriptionManager.GetMatchingSubscribers(publish.Topic);

        var tasks = new List<ValueTask>();
        foreach (var (clientId, grantedQos) in subscribers)
        {
            if (!_sessions.TryGetValue(clientId, out var session)) continue;

            // Downgrade QoS to the negotiated level
            var effectiveQos = (MqttQualityOfService)Math.Min((byte)publish.Qos, (byte)grantedQos);

            var deliveryPacket = new PublishPacket
            {
                Topic = publish.Topic,
                Qos = effectiveQos,
                Retain = false, // do not propagate retain flag on delivery
                Dup = false,
                PacketIdentifier = publish.PacketIdentifier,
                Payload = publish.Payload,
            };

            tasks.Add(session.DeliverAsync(deliveryPacket, ct));
        }

        foreach (var task in tasks)
            await task;
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _listener?.Dispose();

        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync();
        }

        _sessions.Clear();
        _cts?.Dispose();
    }
}
