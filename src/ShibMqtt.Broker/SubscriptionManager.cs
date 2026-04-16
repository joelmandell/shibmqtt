using System.Collections.Concurrent;
using System.Threading;
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
    // clientId -> topic filter -> negotiated qos
    private readonly Dictionary<string, Dictionary<string, MqttQualityOfService>> _subscriptions =
        new(StringComparer.Ordinal);

    // Trie-backed filter index used on the publish hot path.
    private readonly SubscriptionNode _root = new();
    private readonly ReaderWriterLockSlim _subscriptionLock = new(LockRecursionPolicy.NoRecursion);

    // topic -> retained message
    private readonly ConcurrentDictionary<string, PublishPacket> _retainedMessages = new();

    /// <summary>Adds or replaces the given topic filters for <paramref name="clientId"/>.</summary>
    public IReadOnlyList<byte> Subscribe(string clientId, IReadOnlyList<TopicFilter> filters)
    {
        var returnCodes = new byte[filters.Count];

        _subscriptionLock.EnterWriteLock();
        try
        {
            if (!_subscriptions.TryGetValue(clientId, out var clientFilters))
            {
                clientFilters = new Dictionary<string, MqttQualityOfService>(StringComparer.Ordinal);
                _subscriptions[clientId] = clientFilters;
            }

            for (int i = 0; i < filters.Count; i++)
            {
                var filter = filters[i];
                if (!MqttTopicMatcher.IsValidTopicFilter(filter.Topic))
                {
                    returnCodes[i] = 0x80; // failure
                    continue;
                }

                if (clientFilters.TryGetValue(filter.Topic, out var previousQos))
                    RemoveFilterUnsafe(clientId, filter.Topic, previousQos);

                clientFilters[filter.Topic] = filter.MaxQos;
                AddFilterUnsafe(clientId, filter);

                returnCodes[i] = (byte)filter.MaxQos;
            }
        }
        finally
        {
            _subscriptionLock.ExitWriteLock();
        }

        return returnCodes;
    }

    /// <summary>Removes the given topic filters for <paramref name="clientId"/>.</summary>
    public void Unsubscribe(string clientId, IReadOnlyList<string> topicFilters)
    {
        _subscriptionLock.EnterWriteLock();
        try
        {
            if (!_subscriptions.TryGetValue(clientId, out var clientFilters))
                return;

            foreach (var filter in topicFilters)
            {
                if (!clientFilters.Remove(filter, out var qos))
                    continue;

                RemoveFilterUnsafe(clientId, filter, qos);
            }

            if (clientFilters.Count == 0)
                _subscriptions.Remove(clientId);
        }
        finally
        {
            _subscriptionLock.ExitWriteLock();
        }
    }

    /// <summary>Removes all subscriptions for <paramref name="clientId"/>.</summary>
    public void RemoveClient(string clientId)
    {
        _subscriptionLock.EnterWriteLock();
        try
        {
            if (!_subscriptions.Remove(clientId, out var filters))
                return;

            foreach (var (filter, qos) in filters)
                RemoveFilterUnsafe(clientId, filter, qos);
        }
        finally
        {
            _subscriptionLock.ExitWriteLock();
        }
    }

    public bool HasSubscriptions(string clientId)
    {
        _subscriptionLock.EnterReadLock();
        try
        {
            return _subscriptions.TryGetValue(clientId, out var filters) && filters.Count > 0;
        }
        finally
        {
            _subscriptionLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Returns the IDs of clients that have a matching subscription for <paramref name="topicName"/>,
    /// together with the negotiated maximum QoS level for delivery.
    /// </summary>
    public IEnumerable<(string ClientId, MqttQualityOfService GrantedQos)> GetMatchingSubscribers(string topicName)
    {
        Dictionary<string, MqttQualityOfService> matches = new(StringComparer.Ordinal);
        var segments = SplitTopicLevels(topicName);
        bool isSystemTopic = topicName.Length > 0 && topicName[0] == '$';

        _subscriptionLock.EnterReadLock();
        try
        {
            CollectMatches(_root, segments, 0, isSystemTopic, matches);
        }
        finally
        {
            _subscriptionLock.ExitReadLock();
        }

        return matches.Select(static match => (match.Key, match.Value)).ToArray();
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

    private void AddFilterUnsafe(string clientId, TopicFilter filter)
    {
        var node = _root;
        foreach (var segment in SplitTopicLevels(filter.Topic))
        {
            if (segment == "#")
            {
                node.MultiLevelSubscribers[clientId] = filter.MaxQos;
                return;
            }

            node = segment == "+"
                ? node.SingleLevelChild ??= new SubscriptionNode()
                : GetOrAddLiteralChild(node, segment);
        }

        node.ExactSubscribers[clientId] = filter.MaxQos;
    }

    private void RemoveFilterUnsafe(string clientId, string filter, MqttQualityOfService qos)
    {
        List<PathEntry> path = [];
        var node = _root;

        foreach (var segment in SplitTopicLevels(filter))
        {
            if (segment == "#")
            {
                if (node.MultiLevelSubscribers.TryGetValue(clientId, out var currentQos) && currentQos == qos)
                    node.MultiLevelSubscribers.Remove(clientId);

                PruneEmptyNodes(path, node);
                return;
            }

            path.Add(new PathEntry(node, segment));

            if (segment == "+")
            {
                if (node.SingleLevelChild is null)
                    return;

                node = node.SingleLevelChild;
                continue;
            }

            if (!node.LiteralChildren.TryGetValue(segment, out var child))
                return;

            node = child;
        }

        if (node.ExactSubscribers.TryGetValue(clientId, out var currentExactQos) && currentExactQos == qos)
            node.ExactSubscribers.Remove(clientId);

        PruneEmptyNodes(path, node);
    }

    private static SubscriptionNode GetOrAddLiteralChild(SubscriptionNode node, string segment)
    {
        if (node.LiteralChildren.TryGetValue(segment, out var child))
            return child;

        child = new SubscriptionNode();
        node.LiteralChildren[segment] = child;
        return child;
    }

    private static void CollectMatches(
        SubscriptionNode node,
        string[] segments,
        int depth,
        bool isSystemTopic,
        Dictionary<string, MqttQualityOfService> matches)
    {
        if (!(isSystemTopic && depth == 0))
            MergeSubscribers(node.MultiLevelSubscribers, matches);

        if (depth == segments.Length)
        {
            MergeSubscribers(node.ExactSubscribers, matches);
            return;
        }

        var segment = segments[depth];

        if (node.LiteralChildren.TryGetValue(segment, out var literalChild))
            CollectMatches(literalChild, segments, depth + 1, isSystemTopic, matches);

        if (!(isSystemTopic && depth == 0) && node.SingleLevelChild is not null)
            CollectMatches(node.SingleLevelChild, segments, depth + 1, isSystemTopic, matches);
    }

    private static void MergeSubscribers(
        Dictionary<string, MqttQualityOfService> source,
        Dictionary<string, MqttQualityOfService> destination)
    {
        foreach (var (clientId, qos) in source)
        {
            if (!destination.TryGetValue(clientId, out var current) || qos > current)
                destination[clientId] = qos;
        }
    }

    private static void PruneEmptyNodes(List<PathEntry> path, SubscriptionNode node)
    {
        if (!node.IsEmpty)
            return;

        for (int i = path.Count - 1; i >= 0; i--)
        {
            var entry = path[i];

            if (entry.Segment == "+")
            {
                entry.Parent.SingleLevelChild = null;
            }
            else
            {
                entry.Parent.LiteralChildren.Remove(entry.Segment);
            }

            node = entry.Parent;
            if (!node.IsEmpty)
                return;
        }
    }

    private static string[] SplitTopicLevels(string topic) => topic.Split('/', StringSplitOptions.None);

    private sealed class SubscriptionNode
    {
        public Dictionary<string, SubscriptionNode> LiteralChildren { get; } = new(StringComparer.Ordinal);
        public SubscriptionNode? SingleLevelChild { get; set; }
        public Dictionary<string, MqttQualityOfService> ExactSubscribers { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, MqttQualityOfService> MultiLevelSubscribers { get; } = new(StringComparer.Ordinal);

        public bool IsEmpty =>
            LiteralChildren.Count == 0 &&
            SingleLevelChild is null &&
            ExactSubscribers.Count == 0 &&
            MultiLevelSubscribers.Count == 0;
    }

    private readonly record struct PathEntry(SubscriptionNode Parent, string Segment);
}
