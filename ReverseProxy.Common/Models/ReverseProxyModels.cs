// ReverseProxy.Common/Models/ReverseProxyModels.cs
using ProtoBuf;

namespace ReverseProxy.Common.Models
{
    [ProtoContract]
    public class ReverseProxyConnect
    {
        [ProtoMember(1)]
        public int ConnectionId { get; set; }
        
        [ProtoMember(2)]
        public string Target { get; set; }
        
        [ProtoMember(3)]
        public int Port { get; set; }
    }

    [ProtoContract]
    public class ReverseProxyConnectResponse
    {
        [ProtoMember(1)]
        public int ConnectionId { get; set; }
        
        [ProtoMember(2)]
        public bool IsConnected { get; set; }
        
        [ProtoMember(3)]
        public byte[] LocalAddress { get; set; }
        
        [ProtoMember(4)]
        public int LocalPort { get; set; }
        
        [ProtoMember(5)]
        public string HostName { get; set; }
    }

    [ProtoContract]
    public class ReverseProxyData
    {
        [ProtoMember(1)]
        public int ConnectionId { get; set; }
        
        [ProtoMember(2)]
        public byte[] Data { get; set; }
    }

    [ProtoContract]
    public class ReverseProxyDisconnect
    {
        [ProtoMember(1)]
        public int ConnectionId { get; set; }
    }
}