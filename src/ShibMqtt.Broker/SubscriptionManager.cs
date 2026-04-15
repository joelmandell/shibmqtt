using System.Collections.Concurrent;
using ShibMqtt.Core;
using ShibMqtt.Core.Packets;
using ShibMqtt.Core.Protocol;

namespace ShibMqtt.Broker;

/// <summary>
/// Manages topic subscriptions and routes published messages to matching subscribers.
/// Thread-safe; all public methods are safe to call concurrently.
/// </summary>
public sealed class SubscriptionManager
{
    // clientId -> list of (topicFilter, maxQos)
    private readonly ConcurrentDictionary<string, List<TopicFilter>> _subscriptions = new();

    // topic -> retained message
    private readonly ConcurrentDictionary<string, PublishPacket> _retainedMessages = new();

    /// <summary>Adds or replaces the given topic filters for <paramref name="clientId"/>.</summary>
    public IReadOnlyList<byte> Subscribe(string clientId, IReadOnlyList<TopicFilter> filters)
    {
        var returnCodes = new byte[filters.Count];

        var clientFilters = _subscriptions.GetOrAdd(clientId, _ => []);

        lock (clientFilters)
        {
            for (int i = 0; i < filters.Count; i++)
            {
                var filter = filters[i];
                if (!MqttTopicMatcher.IsValidTopicFilter(filter.Topic))
                {
                    returnCodes[i] = 0x80; // failure
                    continue;
                }

                // Replace existing entry for the same topic filter, if any
                int existing = clientFilters.FindIndex(f => f.Topic == filter.Topic);
                if (existing >= 0)
                    clientFilters[existing] = filter;
                else
                    clientFilters.Add(filter);

                returnCodes[i] = (byte)filter.MaxQos;
            }
        }

        return returnCodes;
    }

    /// <summary>Removes the given topic filters for <paramref name="clientId"/>.</summary>
    public void Unsubscribe(string clientId, IReadOnlyList<string> topicFilters)
    {
        if (!_subscriptions.TryGetValue(clientId, out var clientFilters))
            return;

        lock (clientFilters)
        {
            foreach (var filter in topicFilters)
            {
                clientFilters.RemoveAll(f => f.Topic == filter);
            }
        }
    }

    /// <summary>Removes all subscriptions for <paramref name="clientId"/>.</summary>
    public void RemoveClient(string clientId) => _subscriptions.TryRemove(clientId, out _);

    /// <summary>
    /// Returns the IDs of clients that have a matching subscription for <paramref name="topicName"/>,
    /// together with the negotiated maximum QoS level for delivery.
    /// </summary>
    public IEnumerable<(string ClientId, MqttQualityOfService GrantedQos)> GetMatchingSubscribers(string topicName)
    {
        foreach (var (clientId, filters) in _subscriptions)
        {
            MqttQualityOfService? best = null;

            lock (filters)
            {
                foreach (var filter in filters)
                {
                    if (MqttTopicMatcher.IsMatch(topicName, filter.Topic))
                    {
                        if (best is null || filter.MaxQos > best.Value)
                            best = filter.MaxQos;
                    }
                }
            }

            if (best.HasValue)
                yield return (clientId, best.Value);
        }
    }

    /// <summary>Stores or clears a retained message for <paramref name="topic"/>.</summary>
    public void SetRetained(string topic, PublishPacket? packet)
    {
        if (packet is null || packet.Payload.IsEmpty)
        {
            _retainedMessages.TryRemove(topic, out var existing);
            existing?.Dispose();
        }
        else
        {
            // AddOrUpdate: the updateValueFactory disposes the previous entry before replacing it.
            _retainedMessages.AddOrUpdate(topic, packet, (_, prev) =>
            {
                prev.Dispose();
                return packet;
            });
        }
    }

    /// <summary>Returns all retained messages whose topic matches <paramref name="filter"/>.</summary>
    public IEnumerable<PublishPacket> GetRetainedMessages(string filter)
    {
        foreach (var (topic, packet) in _retainedMessages)
        {
            if (MqttTopicMatcher.IsMatch(topic, filter))
                yield return packet;
        }
    }
}
