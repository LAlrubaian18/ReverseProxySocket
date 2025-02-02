// ReverseProxy.Common/Models/MessageEnvelope.cs
using ProtoBuf;

namespace ReverseProxy.Common.Models
{
    [ProtoContract]
    public class MessageEnvelope
    {
        [ProtoMember(1)]
        public MessageType MessageType { get; set; }

        [ProtoMember(2)]
        public byte[] MessageData { get; set; }
    }

    public enum MessageType
    {
        Unknown = 0,
        ReverseProxyConnect = 1,
        ReverseProxyConnectResponse = 2,
        ReverseProxyData = 3,
        ReverseProxyDisconnect = 4
    }
}