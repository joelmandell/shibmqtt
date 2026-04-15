using ShibMqtt.Core.Packets;
using ShibMqtt.Core.Protocol;

namespace ShibMqtt.Core.Encoding;

/// <summary>
/// Discriminated union wrapping the result of decoding an MQTT packet.
/// Use <see cref="PacketType"/> to switch on the kind, then cast via the typed properties.
/// </summary>
public readonly struct MqttDecodedPacket
{
    private readonly object _packet;

    public MqttDecodedPacket(ConnectPacket packet)    { _packet = packet; PacketType = MqttPacketType.Connect; }
    public MqttDecodedPacket(ConnAckPacket packet)    { _packet = packet; PacketType = MqttPacketType.ConnAck; }
    public MqttDecodedPacket(PublishPacket packet)    { _packet = packet; PacketType = MqttPacketType.Publish; }
    public MqttDecodedPacket(PubAckPacket packet)     { _packet = packet; PacketType = MqttPacketType.PubAck; }
    public MqttDecodedPacket(PubRecPacket packet)     { _packet = packet; PacketType = MqttPacketType.PubRec; }
    public MqttDecodedPacket(PubRelPacket packet)     { _packet = packet; PacketType = MqttPacketType.PubRel; }
    public MqttDecodedPacket(PubCompPacket packet)    { _packet = packet; PacketType = MqttPacketType.PubComp; }
    public MqttDecodedPacket(SubscribePacket packet)  { _packet = packet; PacketType = MqttPacketType.Subscribe; }
    public MqttDecodedPacket(SubAckPacket packet)     { _packet = packet; PacketType = MqttPacketType.SubAck; }
    public MqttDecodedPacket(UnsubscribePacket packet){ _packet = packet; PacketType = MqttPacketType.Unsubscribe; }
    public MqttDecodedPacket(UnsubAckPacket packet)   { _packet = packet; PacketType = MqttPacketType.UnsubAck; }
    public MqttDecodedPacket(PingReqPacket packet)    { _packet = packet; PacketType = MqttPacketType.PingReq; }
    public MqttDecodedPacket(PingRespPacket packet)   { _packet = packet; PacketType = MqttPacketType.PingResp; }
    public MqttDecodedPacket(DisconnectPacket packet) { _packet = packet; PacketType = MqttPacketType.Disconnect; }

    public MqttPacketType PacketType { get; }

    public ConnectPacket    AsConnect()     => (ConnectPacket)_packet;
    public ConnAckPacket    AsConnAck()     => (ConnAckPacket)_packet;
    public PublishPacket    AsPublish()     => (PublishPacket)_packet;
    public PubAckPacket     AsPubAck()      => (PubAckPacket)_packet;
    public PubRecPacket     AsPubRec()      => (PubRecPacket)_packet;
    public PubRelPacket     AsPubRel()      => (PubRelPacket)_packet;
    public PubCompPacket    AsPubComp()     => (PubCompPacket)_packet;
    public SubscribePacket  AsSubscribe()   => (SubscribePacket)_packet;
    public SubAckPacket     AsSubAck()      => (SubAckPacket)_packet;
    public UnsubscribePacket AsUnsubscribe() => (UnsubscribePacket)_packet;
    public UnsubAckPacket   AsUnsubAck()    => (UnsubAckPacket)_packet;
    public PingReqPacket    AsPingReq()     => (PingReqPacket)_packet;
    public PingRespPacket   AsPingResp()    => (PingRespPacket)_packet;
    public DisconnectPacket AsDisconnect()  => (DisconnectPacket)_packet;
}
