namespace ShibMqtt.Core.Encoding;

/// <summary>
/// Thrown when the incoming byte stream violates the MQTT protocol specification.
/// </summary>
public sealed class MqttProtocolViolationException : Exception
{
    public MqttProtocolViolationException(string message) : base(message) { }
    public MqttProtocolViolationException(string message, Exception inner) : base(message, inner) { }
}
