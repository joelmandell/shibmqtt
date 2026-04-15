using System.Net;
using System.Net.Sockets;
using ShibMqtt.Broker;
using ShibMqtt.Client;
using ShibMqtt.Core.Packets;
using ShibMqtt.Core.Protocol;

namespace ShibMqtt.Tests;

/// <summary>
/// End-to-end integration tests that spin up a real TCP broker and connect real clients.
/// </summary>
public class IntegrationTests : IAsyncDisposable
{
    private readonly MqttBroker _broker;
    private readonly IPEndPoint _endpoint;

    public IntegrationTests()
    {
        _broker = new MqttBroker();
        // Port 0 = OS picks a free port
        _endpoint = new IPEndPoint(IPAddress.Loopback, 0);
    }

    private async Task<IPEndPoint> StartBrokerAsync()
    {
        // Bind to port 0 then find out which port was chosen
        var tempSocket = new System.Net.Sockets.Socket(
            AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        tempSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)tempSocket.LocalEndPoint!).Port;
        tempSocket.Dispose();

        var ep = new IPEndPoint(IPAddress.Loopback, port);
        await _broker.StartAsync(ep);
        return ep;
    }

    [Fact]
    public async Task Client_CanConnect_AndDisconnect()
    {
        var ep = await StartBrokerAsync();
        await using var client = new MqttClient(new MqttClientOptions
        {
            ClientId = "test-connect",
            Host = "127.0.0.1",
            Port = ep.Port,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        });

        await client.ConnectAsync(TestTimeout());
        Assert.True(client.IsConnected);

        await client.DisconnectAsync();
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Client_CanPublish_AndReceive_Qos0()
    {
        var ep = await StartBrokerAsync();

        await using var publisher = new MqttClient(new MqttClientOptions
        {
            ClientId = "publisher",
            Host = "127.0.0.1",
            Port = ep.Port,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        });

        await using var subscriber = new MqttClient(new MqttClientOptions
        {
            ClientId = "subscriber",
            Host = "127.0.0.1",
            Port = ep.Port,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        });

        var ct = TestTimeout();
        await subscriber.ConnectAsync(ct);
        await publisher.ConnectAsync(ct);

        await subscriber.SubscribeAsync(
            [new TopicFilter { Topic = "test/topic", MaxQos = MqttQualityOfService.AtMostOnce }], ct);

        // Small delay to ensure subscription is registered on the broker
        await Task.Delay(100, ct);

        byte[] payload = System.Text.Encoding.UTF8.GetBytes("hello mqtt");
        await publisher.PublishAsync("test/topic", payload, cancellationToken: ct);

        // Read the first message with a timeout
        using var msgCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var msg in subscriber.Messages.WithCancellation(msgCts.Token))
        {
            Assert.Equal("test/topic", msg.Topic);
            Assert.Equal(payload, msg.Payload.ToArray());
            break;
        }

        await publisher.DisconnectAsync();
        await subscriber.DisconnectAsync();
    }

    [Fact]
    public async Task SubscriptionManager_Subscribe_Returns_GrantedQos()
    {
        var mgr = new SubscriptionManager();
        var filters = new[]
        {
            new TopicFilter { Topic = "a/b", MaxQos = MqttQualityOfService.AtLeastOnce },
        };

        var codes = mgr.Subscribe("client1", filters);

        Assert.Single(codes);
        Assert.Equal((byte)MqttQualityOfService.AtLeastOnce, codes[0]);
    }

    [Fact]
    public void SubscriptionManager_GetMatchingSubscribers_MultipleClients()
    {
        var mgr = new SubscriptionManager();
        mgr.Subscribe("c1", [new TopicFilter { Topic = "sensors/#", MaxQos = MqttQualityOfService.AtMostOnce }]);
        mgr.Subscribe("c2", [new TopicFilter { Topic = "sensors/temp", MaxQos = MqttQualityOfService.AtLeastOnce }]);
        mgr.Subscribe("c3", [new TopicFilter { Topic = "other/#", MaxQos = MqttQualityOfService.AtMostOnce }]);

        var matches = mgr.GetMatchingSubscribers("sensors/temp").ToList();

        Assert.Equal(2, matches.Count);
        Assert.Contains(matches, m => m.ClientId == "c1");
        Assert.Contains(matches, m => m.ClientId == "c2");
        Assert.DoesNotContain(matches, m => m.ClientId == "c3");
    }

    [Fact]
    public void SubscriptionManager_RetainedMessages_StoredAndRetrieved()
    {
        var mgr = new SubscriptionManager();
        var payload = new byte[] { 1, 2, 3 };
        var packet = new PublishPacket
        {
            Topic = "retained/topic",
            Payload = payload,
            Retain = true,
        };

        mgr.SetRetained("retained/topic", packet);

        var retained = mgr.GetRetainedMessages("retained/#").ToList();
        Assert.Single(retained);
        Assert.Equal(payload, retained[0].Payload.ToArray());
    }

    private static CancellationToken TestTimeout(int seconds = 10)
        => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    public async ValueTask DisposeAsync() => await _broker.DisposeAsync();
}
