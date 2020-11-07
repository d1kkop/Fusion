using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace Fusion
{
    internal class Recipient
    {
        internal enum State
        {
            NotSet,
            Connecting,
            Connected,
            Disconnected,
            Lost,
            Kicked
        }

        UnreliableStream m_UnreliableStream;
        Dictionary<byte, ReliableStream> m_ReliableStreams;

        internal bool IsServer { get; private set; }
        internal State ConnectionState { get; }
        internal Node Node { get; }
        internal IPEndPoint EndPoint { get; }
        internal UdpClient UDPClient { get; }

        public Recipient( Node node, IPEndPoint endpoint, UdpClient recipient )
        {
            IsServer  = false;
            ConnectionState = State.NotSet;
            Node      = node;
            EndPoint  = endpoint;
            UDPClient = recipient;
            m_UnreliableStream = new UnreliableStream( this );
            m_ReliableStreams  = new Dictionary<byte, ReliableStream>();
        }

        internal void Sync()
        {
            lock(m_ReliableStreams)
            {
                foreach( var kvp in m_ReliableStreams)
                {
                    ReliableStream stream = kvp.Value;
                    stream.Sync();
                }
            }
            m_UnreliableStream.Sync();
        }

        internal void Send( byte id, byte[] data, byte channel, SendMethod sendMethod )
        {
            switch ( sendMethod)
            {
                case SendMethod.Reliable:
                {
                    ReliableStream stream;
                    lock (m_ReliableStreams)
                    {
                        if (!m_ReliableStreams.TryGetValue( channel, out stream ))
                        {
                            stream = new ReliableStream( this, channel );
                            m_ReliableStreams.Add( channel, stream );
                        }
                    }
                    stream.AddMessage( id, data );
                }
                break;

                case SendMethod.Unreliable:
                {
                    m_UnreliableStream.AddMessage( id, data );
                }
                break;
            }

        }

        internal void ReceiveDataWT( BinaryReader reader, BinaryWriter writer )
        {
            byte streamId = reader.ReadByte();
            switch (streamId)
            {
                case ReliableStream.RID:
                case ReliableStream.RACK:
                {
                    byte channel = reader.ReadByte();
                    ReliableStream stream;
                    lock (m_ReliableStreams)
                    {
                        if (!m_ReliableStreams.TryGetValue( channel, out stream ))
                        {
                            stream = new ReliableStream( this, channel );
                            m_ReliableStreams.Add( channel, stream );
                        }
                    }
                    if (streamId == ReliableStream.RID)
                        stream.ReceiveDataWT( reader, writer );
                    else
                        stream.ReceiveAckWT( reader );
                }
                break;

                case UnreliableStream.URID:
                {
                    m_UnreliableStream.ReceiveDataWT( reader );
                }
                break;

                default:
                Debug.WriteLine( "Unknown stream id detected, data ignored." );
                break;
            }
        }

        internal void FlushDataST( BinaryWriter writer )
        {
            lock(m_ReliableStreams)
            {
                foreach( var kvp in m_ReliableStreams)
                {
                    ReliableStream stream = kvp.Value;
                    stream.FlushST( writer );
                }
            }
            m_UnreliableStream.FlushST( writer );
        }

        internal void ReceiveSystemMessageWT( BinaryReader reader, BinaryWriter writer, byte id, IPEndPoint endpoint, byte channel )
        {
            Debug.Assert( id < (byte)SystemPacketId.Count );
            SystemPacketId enumId = (SystemPacketId)id;
            switch ( enumId )
            {
                case SystemPacketId.IdPackRequest:
                Node.GroupManager.ReceiveIdPacketRequestWT( reader, writer, endpoint, channel );
                break;

                case SystemPacketId.IdPackProvide:
                Node.GroupManager.ReceiveIdPacketProvideWT( reader, writer, endpoint, channel );
                break;

                case SystemPacketId.CreateGroup:
                Node.GroupManager.ReceiveGroupCreatedWT( reader, writer, endpoint, channel );
                break;

                case SystemPacketId.DestroyGroup:
                Node.GroupManager.ReceiveGroupDestroyedWT( reader, writer, endpoint, channel );
                break;

                case SystemPacketId.DestroyAllGroups:
                Node.GroupManager.ReceiveDestroyAllGroupsWT( reader, writer, endpoint, channel );
                break;

                case SystemPacketId.Connect:
                break;

                case SystemPacketId.Disconnect:
                break;

                case SystemPacketId.Count:
                break;

                default:
                throw new Exception( "Invalid reliable packet id." );
            }
        }
    }
}
