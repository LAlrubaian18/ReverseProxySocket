// ReverseProxy.Common/Utilities/MessageSerializer.cs
using ProtoBuf;
using ReverseProxy.Common.Models;
using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ReverseProxy.Common.Utilities
{
    public static class MessageSerializer
    {
        private const int MAX_MESSAGE_SIZE = 1024 * 1024 * 10; // 10MB
        private static readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        public static async Task SendMessageAsync<T>(NetworkStream stream, T message, CancellationToken cancellationToken = default)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (message == null) throw new ArgumentNullException(nameof(message));

            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                var messageType = GetMessageType(typeof(T));
                var messageData = SerializeToBytes(message);

                var envelope = new MessageEnvelope
                {
                    MessageType = messageType,
                    MessageData = messageData
                };

                var envelopeData = SerializeToBytes(envelope);
                if (envelopeData.Length > MAX_MESSAGE_SIZE)
                {
                    throw new InvalidOperationException($"Message size ({envelopeData.Length} bytes) exceeds maximum allowed size ({MAX_MESSAGE_SIZE} bytes)");
                }

                var lengthBytes = BitConverter.GetBytes(envelopeData.Length);

                await stream.WriteAsync(lengthBytes, 0, lengthBytes.Length, cancellationToken);
                await stream.WriteAsync(envelopeData, 0, envelopeData.Length, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public static async Task<object> ReceiveMessageAsync(NetworkStream stream, CancellationToken cancellationToken = default)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            // Read message length (4 bytes)
            byte[] lengthBytes = new byte[4];
            int bytesRead = 0;
            while (bytesRead < 4)
            {
                int read = await stream.ReadAsync(lengthBytes, bytesRead, 4 - bytesRead, cancellationToken);
                if (read == 0) throw new EndOfStreamException("Connection closed while reading message length");
                bytesRead += read;
            }

            int length = BitConverter.ToInt32(lengthBytes, 0);
            if (length <= 0 || length > MAX_MESSAGE_SIZE)
            {
                throw new InvalidDataException($"Invalid message length: {length}");
            }

            // Read message data
            byte[] messageBytes = new byte[length];
            bytesRead = 0;
            while (bytesRead < length)
            {
                int read = await stream.ReadAsync(messageBytes, bytesRead, length - bytesRead, cancellationToken);
                if (read == 0) throw new EndOfStreamException("Connection closed while reading message data");
                bytesRead += read;
            }

            try
            {
                var envelope = DeserializeFromBytes<MessageEnvelope>(messageBytes);
                if (envelope == null) throw new InvalidDataException("Failed to deserialize message envelope");
                
                return DeserializeMessage(envelope);
            }
            catch (ProtoException ex)
            {
                throw new InvalidDataException($"Protocol buffer deserialization error: {ex.Message}");
            }
        }

        private static byte[] SerializeToBytes<T>(T obj)
        {
            using var ms = new MemoryStream();
            Serializer.Serialize(ms, obj);
            return ms.ToArray();
        }

        private static T DeserializeFromBytes<T>(byte[] data)
        {
            if (data == null || data.Length == 0)
                throw new ArgumentException("Data cannot be null or empty", nameof(data));

            using var ms = new MemoryStream(data);
            return Serializer.Deserialize<T>(ms);
        }

        private static MessageType GetMessageType(Type type)
        {
            return type.Name switch
            {
                nameof(ReverseProxyConnect) => MessageType.ReverseProxyConnect,
                nameof(ReverseProxyConnectResponse) => MessageType.ReverseProxyConnectResponse,
                nameof(ReverseProxyData) => MessageType.ReverseProxyData,
                nameof(ReverseProxyDisconnect) => MessageType.ReverseProxyDisconnect,
                _ => throw new ArgumentException($"Unknown message type: {type.Name}")
            };
        }

        private static object DeserializeMessage(MessageEnvelope envelope)
        {
            if (envelope?.MessageData == null)
                throw new ArgumentException("Invalid envelope or message data");

            using var ms = new MemoryStream(envelope.MessageData);
            return envelope.MessageType switch
            {
                MessageType.ReverseProxyConnect => Serializer.Deserialize<ReverseProxyConnect>(ms),
                MessageType.ReverseProxyConnectResponse => Serializer.Deserialize<ReverseProxyConnectResponse>(ms),
                MessageType.ReverseProxyData => Serializer.Deserialize<ReverseProxyData>(ms),
                MessageType.ReverseProxyDisconnect => Serializer.Deserialize<ReverseProxyDisconnect>(ms),
                _ => throw new ArgumentException($"Unknown message type: {envelope.MessageType}")
            };
        }
    }
}