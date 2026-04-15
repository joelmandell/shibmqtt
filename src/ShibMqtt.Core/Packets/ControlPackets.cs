namespace ShibMqtt.Core.Packets;

/// <summary>MQTT PINGREQ packet (§3.12) – fixed 2-byte packet, no payload.</summary>
public readonly struct PingReqPacket { }

/// <summary>MQTT PINGRESP packet (§3.13) – fixed 2-byte packet, no payload.</summary>
public readonly struct PingRespPacket { }

/// <summary>MQTT DISCONNECT packet (§3.14) – fixed 2-byte packet, no payload.</summary>
public readonly struct DisconnectPacket { }
