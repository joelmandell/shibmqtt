namespace ShibMqtt.Core.Protocol;

/// <summary>MQTT protocol constants.</summary>
internal static class MqttConstants
{
    /// <summary>MQTT 3.1.1 protocol name bytes (UTF-8 encoded "MQTT").</summary>
    public static ReadOnlySpan<byte> ProtocolName => "MQTT"u8;

    /// <summary>MQTT 3.1.1 protocol level.</summary>
    public const byte ProtocolLevel = 4;

    /// <summary>Maximum payload size (256 MB).</summary>
    public const int MaxRemainingLength = 268_435_455;

    /// <summary>Maximum keep-alive value in seconds (18 hours).</summary>
    public const ushort MaxKeepAlive = 65535;

    /// <summary>Default keep-alive in seconds.</summary>
    public const ushort DefaultKeepAlive = 60;

    /// <summary>Default port for MQTT over TCP.</summary>
    public const int DefaultPort = 1883;

    /// <summary>Default port for MQTT over TLS.</summary>
    public const int DefaultTlsPort = 8883;
}
