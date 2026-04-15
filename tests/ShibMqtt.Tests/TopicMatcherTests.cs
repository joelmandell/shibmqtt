using ShibMqtt.Core;

namespace ShibMqtt.Tests;

/// <summary>Tests for <see cref="MqttTopicMatcher"/>.</summary>
public class TopicMatcherTests
{
    // ──────────────────────────────────────────────────────────────
    //  Exact match
    // ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("sport/tennis/player1", "sport/tennis/player1", true)]
    [InlineData("sport/tennis/player1", "sport/tennis/player2", false)]
    [InlineData("sport",                "sport",                true)]
    [InlineData("a/b/c/d",             "a/b/c/d",              true)]
    public void ExactMatch(string topic, string filter, bool expected)
        => Assert.Equal(expected, MqttTopicMatcher.IsMatch(topic, filter));

    // ──────────────────────────────────────────────────────────────
    //  Multi-level wildcard (#)
    // ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("sport/tennis/player1", "sport/#",    true)]
    [InlineData("sport/tennis/player1", "#",          true)]
    [InlineData("sport",               "sport/#",    true)]   // § 4.7.1.2
    [InlineData("sport/tennis",        "sport/#",    true)]
    [InlineData("finance",             "sport/#",    false)]
    [InlineData("sport/tennis/player1", "sport/tennis/#", true)]
    public void MultilevelWildcard(string topic, string filter, bool expected)
        => Assert.Equal(expected, MqttTopicMatcher.IsMatch(topic, filter));

    // ──────────────────────────────────────────────────────────────
    //  Single-level wildcard (+)
    // ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("sport/tennis/player1", "sport/+/player1",  true)]
    [InlineData("sport/tennis/player1", "sport/+/player2",  false)]
    [InlineData("sport/tennis",         "+/+",              true)]
    [InlineData("sport",                "+",                true)]
    [InlineData("sport/tennis/player1", "+",                false)]  // only top level
    [InlineData("sport/tennis",         "sport/+",          true)]
    [InlineData("sport/tennis/player1", "sport/+/#",        true)]
    public void SingleLevelWildcard(string topic, string filter, bool expected)
        => Assert.Equal(expected, MqttTopicMatcher.IsMatch(topic, filter));

    // ──────────────────────────────────────────────────────────────
    //  $ (system) topics must not match bare wildcards
    // ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("$SYS/monitor/clients", "#",     false)]
    [InlineData("$SYS/monitor/clients", "+/monitor/clients", false)]
    [InlineData("$SYS/monitor/clients", "$SYS/#", true)]
    [InlineData("$SYS/broker",          "$SYS/+", true)]
    public void SystemTopics(string topic, string filter, bool expected)
        => Assert.Equal(expected, MqttTopicMatcher.IsMatch(topic, filter));

    // ──────────────────────────────────────────────────────────────
    //  IsValidTopicName
    // ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("sensors/temp",  true)]
    [InlineData("",              false)]
    [InlineData("sensors/+",     false)]
    [InlineData("sensors/#",     false)]
    public void IsValidTopicName(string topic, bool expected)
        => Assert.Equal(expected, MqttTopicMatcher.IsValidTopicName(topic));

    // ──────────────────────────────────────────────────────────────
    //  IsValidTopicFilter
    // ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("#",          true)]
    [InlineData("sport/#",    true)]
    [InlineData("sport/+",    true)]
    [InlineData("+/+/#",      true)]
    [InlineData("",           false)]
    [InlineData("sport#",     false)]  // # not preceded by /
    [InlineData("sport+",     false)]  // + not surrounded by /
    [InlineData("#/sport",    false)]  // # must be last
    [InlineData("sport/+extra", false)] // + not followed by /
    public void IsValidTopicFilter(string filter, bool expected)
        => Assert.Equal(expected, MqttTopicMatcher.IsValidTopicFilter(filter));
}
