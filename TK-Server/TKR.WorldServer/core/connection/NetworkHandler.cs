﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using TKR.Shared;
using TKR.WorldServer.core.miscfile.thread;
using TKR.WorldServer.core.net;
using TKR.WorldServer.core.net.handlers;
using TKR.WorldServer.core.worlds;
using TKR.WorldServer.networking;
using TKR.WorldServer.networking.packets.outgoing;
using TKR.WorldServer.utils;

namespace TKR.WorldServer.core.connection
{
    public sealed class NetworkSendHandler
    {
        private readonly Client Client;
        private readonly SocketAsyncEventArgs SocketAsyncEventArgs;

        public NetworkSendHandler(Client client, SocketAsyncEventArgs e)
        {
            Client = client;

            SocketAsyncEventArgs = e;
            SocketAsyncEventArgs.Completed += OnCompleted;
        }

        public void SendMessage(OutgoingMessage message)
        {
            var sendToken = (SendToken)SocketAsyncEventArgs.UserToken;
            sendToken.Pending.Enqueue(message);
        }

        public void SetSocket(Socket socket)
        {
            SocketAsyncEventArgs.AcceptSocket = socket;
            StartSendAsync(SocketAsyncEventArgs);
        }

        private async void StartSendAsync(SocketAsyncEventArgs e, int delay = 0)
        {
            if (Client?.State == ProtocolState.Disconnected)
                return;

            try
            {
                var s = (SendToken)e.UserToken;

                if (delay > 0)
                    await Task.Delay(delay);

                if (s.BytesAvailable <= 0)
                {
                    s.Reset();
                    if (!FlushPending(s).HasValue)
                        return;
                }

                var willRaiseEvent = false;

                try
                {
                    var bytesToSend = s.BytesAvailable > ConnectionListener.BUFFER_SIZE ? ConnectionListener.BUFFER_SIZE : s.BytesAvailable;

                    e.SetBuffer(s.BufferOffset, bytesToSend);

                    Buffer.BlockCopy(s.Data, s.BytesSent, e.Buffer, s.BufferOffset, bytesToSend);

                    willRaiseEvent = e.AcceptSocket.SendAsync(e);
                }
                catch
                {
                }
                finally
                {
                    if (!willRaiseEvent)
                        OnCompleted(null, e);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"StartSend: {ex}");

                Client.Player.SendError("Unknown error sent to nexus");
                Client.Reconnect(new Reconnect()
                {
                    Host = "",
                    Port = Client.GameServer.Configuration.serverInfo.port,
                    GameId = World.NEXUS_ID,
                    Name = "Nexus"
                });
            }
        }

        private void OnCompleted(object sender, SocketAsyncEventArgs e)
        {
            var s = (SendToken)e.UserToken;

            if (Client.State == ProtocolState.Disconnected)
            {
                s.Reset();
                return;
            }

            if (e.SocketError != SocketError.Success)
            {
                Client.Disconnect("Send SocketError = " + e.SocketError);
                return;
            }

            s.BytesSent += e.BytesTransferred;
            s.BytesAvailable -= s.BytesSent;

            var delay = 0;
            if (s.BytesAvailable <= 0)
                delay = 16;

            StartSendAsync(e, delay);
        }

        private bool? FlushPending(SendToken s)
        {
            try
            {
                while (s.Pending.TryDequeue(out var packet))
                {
                    var bytesWritten = packet.Write(s.Data, s.BytesAvailable);
                    if (!bytesWritten.HasValue)
                        continue;

                    if (bytesWritten == 0)
                    {
                        s.Pending.Enqueue(packet);
                        return true;
                    }
                    s.BytesAvailable += bytesWritten.Value;
                }

                if (s.BytesAvailable <= 0)
                    return false;
            }
            catch (Exception e)
            {
                StaticLogger.Instance.Error(e);
                Client.Disconnect("Error when handling pending packets");
            }

            return true;
        }

        public void Reset()
        {
            ((SendToken)SocketAsyncEventArgs.UserToken).Clear();
        }
    }

    public sealed class NetworkReceiveHandler
    {
        private readonly Client Client;
        private readonly SocketAsyncEventArgs SocketAsyncEventArgs;

        public NetworkReceiveHandler(Client client, SocketAsyncEventArgs e)
        {
            Client = client;

            SocketAsyncEventArgs = e;
            SocketAsyncEventArgs.Completed += OnCompleted;
        }

        public void SetSocket(Socket socket)
        {
            SocketAsyncEventArgs.AcceptSocket = socket;
            StartReceive(SocketAsyncEventArgs);
        }

        private void StartReceive(SocketAsyncEventArgs e)
        {
            if (Client.State == ProtocolState.Disconnected)
                return;

            e.SetBuffer(e.Offset, ConnectionListener.BUFFER_SIZE);

            bool willRaiseEvent;
            try
            {
                willRaiseEvent = e.AcceptSocket.ReceiveAsync(e);
            }
            catch (Exception exception)
            {
                Client.Disconnect($"[{Client.Account?.Name}:{Client.Account?.AccountId} {Client.IpAddress}] {exception}");
                return;
            }

            if (!willRaiseEvent)
                OnCompleted(null, e);
        }

        private void OnCompleted(object sender, SocketAsyncEventArgs e)
        {
            var r = (ReceiveToken)e.UserToken;

            if (Client.State == ProtocolState.Disconnected)
            {
                r.Reset();
                return;
            }

            if (e.SocketError != SocketError.Success)
            {
                var msg = "";
                if (e.SocketError != SocketError.ConnectionReset)
                    msg = "Receive SocketError = " + e.SocketError;

                Client.Disconnect(msg);
                return;
            }

            var bytesNotRead = e.BytesTransferred;
            if (bytesNotRead == 0)
            {
                Client.Disconnect("bytesNotRead == 0");
                return;
            }

            while (bytesNotRead > 0)
            {
                bytesNotRead = ReadPacketBytes(e, r, bytesNotRead);

                if (r.BytesRead == ReceiveToken.PrefixLength)
                {
                    r.PacketLength = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(r.PacketBytes, 0));

                    if (r.PacketLength == 1014001516)
                    {
                        SendPolicyFile();
                        r.Reset();
                        break;
                    }

                    if (r.PacketLength < ReceiveToken.PrefixLength || r.PacketLength > ConnectionListener.BUFFER_SIZE)
                    {
                        r.Reset();
                        break;
                    }
                }

                if (r.BytesRead == r.PacketLength)
                {
                    if (Client.IsReady())
                    {
                        var id = r.GetPacketId();
                        var payload = r.GetPacketBody();
                        if (Client.Player == null) // read it instantly if there is no player otherwise we will append it to the world instance
                        {
                            if (id != MessageId.HELLO && id != MessageId.LOAD && id != MessageId.CREATE)
                            {
                                Console.WriteLine(id + " Received with null player");
                                Client.Disconnect("Invalid State");
                                continue;
                            }

                            var handler = MessageHandlers.GetHandler(id);
                            if (handler == null)
                            {
                                Client.PacketSpamAmount++;
                                if (Client.PacketSpamAmount > 32)
                                    Client.Disconnect($"Packet Spam: {Client.IpAddress}");
                                StaticLogger.Instance.Error($"Unknown MessageId: {id} - {Client.IpAddress}");
                                continue;
                            }

                            // todo redo
                            try
                            {
                                NReader rdr = null;
                                if (payload.Length != 0)
                                    rdr = new NReader(new MemoryStream(payload));
                                var time = new TickTime();
                                handler.Handle(Client, rdr, ref time);
                                rdr?.Dispose();
                            }
                            catch (Exception exx)
                            {
                                Console.WriteLine($"Error processing packet ({(Client.Account != null ? Client.Account.Name : "")}, {Client.IpAddress})\n{exx}");
                                if (!(exx is EndOfStreamException))
                                    StaticLogger.Instance.Error($"Error processing packet ({(Client.Account != null ? Client.Account.Name : "")}, {Client.IpAddress})\n{exx}");
                                Client.Disconnect($"Read Error for packet: {id}");
                            }
                        }
                        else
                            Client.Player.IncomingMessages.Enqueue(new InboundBuffer(Client, id, payload));
                    }
                    r.Reset();
                }
            }

            StartReceive(e);
        }

        private static int ReadPacketBytes(SocketAsyncEventArgs e, ReceiveToken r, int bytesNotRead)
        {
            var offset = r.BufferOffset + e.BytesTransferred - bytesNotRead;
            var remainingBytes = r.PacketLength - r.BytesRead;

            if (bytesNotRead < remainingBytes)
            {
                Buffer.BlockCopy(e.Buffer, offset, r.PacketBytes, r.BytesRead, bytesNotRead);
                r.BytesRead += bytesNotRead;
                return 0;
            }

            Buffer.BlockCopy(e.Buffer, offset, r.PacketBytes, r.BytesRead, remainingBytes);

            r.BytesRead = r.PacketLength;
            return bytesNotRead - remainingBytes;
        }

        private void SendPolicyFile()
        {
            Console.WriteLine("SendPolicyFile");
            try
            {
                var s = new NetworkStream(Client.Socket);
                var wtr = new NWriter(s);
                wtr.WriteNullTerminatedString(
                    @"<cross-domain-policy>" +
                    @"<allow-access-from domain=""*"" to-ports=""*"" />" +
                    @"</cross-domain-policy>");
                wtr.Write((byte)'\r');
                wtr.Write((byte)'\n');
            }
            catch (Exception e)
            {
                StaticLogger.Instance.Error(e.ToString());
            }
        }

        public void Reset()
        {
            ((ReceiveToken)SocketAsyncEventArgs.UserToken).Reset();
        }
    }

    public sealed class NetworkHandler
    {
        private readonly Client Client;
        //private readonly NetworkSendHandler NetworkSendHandler;
        private readonly NetworkReceiveHandler NetworkReceiveHandler;
        private Queue<OutgoingMessage> PendingSending;

        public NetworkHandler(Client client, SocketAsyncEventArgs send, SocketAsyncEventArgs receive)
        {
            Client = client;

            //NetworkSendHandler = new NetworkSendHandler(client, send);
            NetworkReceiveHandler = new NetworkReceiveHandler(client, receive);
            PendingSending = new Queue<OutgoingMessage>();
        }

        public void SetSocket(Socket socket)
        {
            Client.State = ProtocolState.Connected;

            //NetworkSendHandler.SetSocket(socket);
            NetworkReceiveHandler.SetSocket(socket);
        }

        public void SendMessage(ref OutgoingMessageData outgoingMessageData)
        {
            if (Client.State == ProtocolState.Disconnected)
                return;

            var data = outgoingMessageData.GetBuffer();
            try
            {
                _ = Client.Socket.Send(data);
            }
            catch (Exception e)
            {
                Client.Disconnect("Error sending bytes");
            }
        }

        public void SendPacket(OutgoingMessage pkt)
        {
            if (Client.Player == null)
                SendDirectly(pkt);
            else
                PendingSending.Enqueue(pkt);
            //NetworkSendHandler.SendMessage(pkt);
        }

        public void SendPackets(IEnumerable<OutgoingMessage> pkts)
        {
            foreach (var pkt in pkts)
                SendPacket(pkt);
        }

        public void Reset()
        {
            //NetworkSendHandler.Reset();
            NetworkReceiveHandler.Reset();
        }

        public void FlushIO()
        {
            while (PendingSending.Count > 0)
                SendDirectly(PendingSending.Dequeue());
        }

        private void SendDirectly(OutgoingMessage outgoingMessage)
        {
            try
            {
                var memoryStream = new MemoryStream();
                using (var wtr = new NWriter(memoryStream))
                {
                    outgoingMessage.Write(wtr);

                    var len = (int)memoryStream.Position;
                    var offset = len + 5;

                    var messageLenBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(offset));

                    var messageBytes = new byte[offset];
                    messageBytes[0] = messageLenBytes[0];
                    messageBytes[1] = messageLenBytes[1];
                    messageBytes[2] = messageLenBytes[2];
                    messageBytes[3] = messageLenBytes[3];
                    messageBytes[4] = (byte)outgoingMessage.MessageId;

                    var streamBuffer = memoryStream.GetBuffer();
                    Array.Resize(ref streamBuffer, len);

                    //outgoingMessage.Crypt(client, streamBuffer, 0, streamBuffer.Length);
                    Buffer.BlockCopy(streamBuffer, 0, messageBytes, 5, streamBuffer.Length);

                    if (Client.State != ProtocolState.Disconnected)
                    {
                        var bytesSent = Client.Socket.Send(messageBytes);
                        if (bytesSent != messageBytes.Length)
                            Client.Disconnect("Error sending bytes");
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"[SendDirectly] {e}");
            }
        }
    }
}
