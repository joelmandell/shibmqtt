namespace ShibMqtt.Core.Protocol;

/// <summary>MQTT Quality of Service levels.</summary>
public enum MqttQualityOfService : byte
{
    /// <summary>At most once delivery – fire and forget.</summary>
    AtMostOnce = 0,

    /// <summary>At least once delivery – acknowledged delivery.</summary>
    AtLeastOnce = 1,

    /// <summary>Exactly once delivery – assured delivery.</summary>
    ExactlyOnce = 2,
}
