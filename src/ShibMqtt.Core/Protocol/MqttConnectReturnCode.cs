namespace ShibMqtt.Core.Protocol;

/// <summary>MQTT CONNACK return codes (MQTT 3.1.1 §3.2.2.3).</summary>
public enum MqttConnectReturnCode : byte
{
    Accepted = 0x00,
    UnacceptableProtocolVersion = 0x01,
    IdentifierRejected = 0x02,
    ServerUnavailable = 0x03,
    BadUsernameOrPassword = 0x04,
    NotAuthorized = 0x05,
}
