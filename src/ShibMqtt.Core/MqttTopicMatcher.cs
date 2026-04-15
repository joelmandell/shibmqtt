namespace ShibMqtt.Core;

/// <summary>
/// MQTT topic matching utility.
/// Supports single-level wildcard (+) and multi-level wildcard (#) as defined in MQTT 3.1.1 §4.7.
/// </summary>
public static class MqttTopicMatcher
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="topicName"/> matches the <paramref name="topicFilter"/>.
    /// </summary>
    /// <param name="topicName">Concrete topic name (e.g., "sensors/temperature/room1").</param>
    /// <param name="topicFilter">Filter that may contain + and/or # wildcards.</param>
    public static bool IsMatch(ReadOnlySpan<char> topicName, ReadOnlySpan<char> topicFilter)
    {
        // Topics beginning with $ are not delivered to subscribers using wildcards
        if (!topicName.IsEmpty && topicName[0] == '$')
        {
            if (!topicFilter.IsEmpty && (topicFilter[0] == '+' || topicFilter[0] == '#'))
                return false;
        }

        return MatchSegments(topicName, topicFilter);
    }

    private static bool MatchSegments(ReadOnlySpan<char> name, ReadOnlySpan<char> filter)
    {
        while (true)
        {
            if (filter.IsEmpty && name.IsEmpty) return true;
            if (filter.IsEmpty) return false;

            // Multi-level wildcard: # matches everything remaining
            if (filter[0] == '#') return true;

            if (name.IsEmpty)
            {
                // Edge case: filter "sport/#" should match "sport" (§4.7.1.2)
                // This is achieved by treating "sport/#" as "sport" when name is exhausted.
                return filter.SequenceEqual("#") || filter.SequenceEqual("/#");
            }

            // Get next segments
            int nameSlash  = name.IndexOf('/');
            int filterSlash = filter.IndexOf('/');

            ReadOnlySpan<char> nameSeg   = nameSlash   >= 0 ? name[..nameSlash]   : name;
            ReadOnlySpan<char> filterSeg = filterSlash >= 0 ? filter[..filterSlash] : filter;

            // Single-level wildcard matches any single segment
            if (!filterSeg.SequenceEqual("+") && !nameSeg.SequenceEqual(filterSeg))
                return false;

            if (nameSlash < 0 && filterSlash < 0) return true;
            // Name exhausted but filter still has segments – only a trailing "/#" is OK
            if (nameSlash < 0)
                return filter[(filterSlash + 1)..].SequenceEqual("#".AsSpan());
            // Filter exhausted but name still has segments
            if (filterSlash < 0) return false;

            name   = name[(nameSlash + 1)..];
            filter = filter[(filterSlash + 1)..];
        }
    }

    /// <summary>Returns <c>true</c> when <paramref name="topic"/> is a valid MQTT topic name (no wildcards allowed).</summary>
    public static bool IsValidTopicName(ReadOnlySpan<char> topic)
    {
        if (topic.IsEmpty) return false;
        foreach (char c in topic)
        {
            if (c == '+' || c == '#') return false;
        }
        return true;
    }

    /// <summary>Returns <c>true</c> when <paramref name="filter"/> is a valid MQTT topic filter.</summary>
    public static bool IsValidTopicFilter(ReadOnlySpan<char> filter)
    {
        if (filter.IsEmpty) return false;

        int i = 0;
        while (i < filter.Length)
        {
            char c = filter[i];

            if (c == '#')
            {
                // # must be the last character, preceded by nothing or /
                bool validPosition = i == filter.Length - 1 && (i == 0 || filter[i - 1] == '/');
                if (!validPosition) return false;
            }
            else if (c == '+')
            {
                // + must be surrounded by / or be at start/end
                bool prevOk = i == 0 || filter[i - 1] == '/';
                bool nextOk = i == filter.Length - 1 || filter[i + 1] == '/';
                if (!prevOk || !nextOk) return false;
            }

            i++;
        }

        return true;
    }
}
