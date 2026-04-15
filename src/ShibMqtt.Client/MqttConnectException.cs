using ShibMqtt.Core.Protocol;

namespace ShibMqtt.Client;

/// <summary>Thrown when a broker rejects a CONNECT request.</summary>
public sealed class MqttConnectException : Exception
{
    public MqttConnectReturnCode ReturnCode { get; }

    public MqttConnectException(string message, MqttConnectReturnCode returnCode)
        : base(message)
    {
        ReturnCode = returnCode;
    }
}
