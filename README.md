# shibmqtt

A hyper-performant MQTT 3.1.1 broker and client library written in C# targeting **.NET 10**.

## Design Goals

| Goal | Technique |
|---|---|
| Zero-copy parsing | `ReadOnlySpan<byte>`, `SequenceReader<T>`, `System.IO.Pipelines` |
| Pool-backed payloads | `MemoryPool<byte>`, `IMemoryOwner<byte>` |
| Allocation-free encoding | `IBufferWriter<byte>`, `ArrayBufferWriter<byte>` |
| Back-pressure I/O | `System.IO.Pipelines` (`Pipe`, `PipeReader`, `PipeWriter`) |
| Ordered, bounded dispatch | `Channel<T>` (bounded, single-reader) |
| High-throughput socket I/O | `Socket.AcceptAsync`, `NetworkStream` with pipeline fill/drain |

## Project Structure

```
src/
  ShibMqtt.Core/     – MQTT protocol (packets, encoder, decoder, topic matcher)
  ShibMqtt.Broker/   – TCP broker (session management, subscriptions, message dispatch)
  ShibMqtt.Client/   – TCP client (connect, publish, subscribe)
tests/
  ShibMqtt.Tests/    – Unit & integration tests (xUnit)
```

## Quick Start

### Broker

```csharp
using System.Net;
using ShibMqtt.Broker;

await using var broker = new MqttBroker();
await broker.StartAsync(new IPEndPoint(IPAddress.Any, 1883));

Console.WriteLine("Broker listening on :1883 – press ENTER to stop");
Console.ReadLine();
```

### Client

```csharp
using ShibMqtt.Client;
using ShibMqtt.Core.Packets;
using ShibMqtt.Core.Protocol;

await using var client = new MqttClient(new MqttClientOptions
{
    ClientId = "demo-client",
    Host = "localhost",
    Port = 1883,
});

await client.ConnectAsync();

// Subscribe
await client.SubscribeAsync([new TopicFilter { Topic = "sensors/#", MaxQos = MqttQualityOfService.AtLeastOnce }]);

// Receive messages in background
_ = Task.Run(async () =>
{
    await foreach (var msg in client.Messages)
    {
        Console.WriteLine($"{msg.Topic}: {System.Text.Encoding.UTF8.GetString(msg.Payload.Span)}");
        msg.Dispose();
    }
});

// Publish
await client.PublishAsync("sensors/temp", System.Text.Encoding.UTF8.GetBytes("42.5"));

await client.DisconnectAsync();
```

## Building

```sh
dotnet build ShibMqtt.slnx
dotnet test  ShibMqtt.slnx
```

## Supported MQTT Packets

CONNECT · CONNACK · PUBLISH · PUBACK · PUBREC · PUBREL · PUBCOMP · SUBSCRIBE · SUBACK · UNSUBSCRIBE · UNSUBACK · PINGREQ · PINGRESP · DISCONNECT

## Topic Wildcards

| Wildcard | Meaning | Example filter | Matches |
|---|---|---|---|
| `+` | Any single segment | `sensors/+/temp` | `sensors/room1/temp` |
| `#` | All remaining segments | `sensors/#` | `sensors/a/b/c` |
