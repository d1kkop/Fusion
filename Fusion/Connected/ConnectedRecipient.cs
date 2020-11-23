using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace Fusion
{
    public enum ConnectionState
    {
        NotSet,
        Initiating,
        Active,
        Disconnected
    }

    public enum ConnectResult
    {
        NotSet,
        Succes,
        MaxUsers,
        InvalidPw,
        Timedout
    }

    public enum DisconnectReason
    {
        NotSet,
        Requested,
        Unreachable,
        Kicked
    }

    // --- Message structs -----------------------------------------------------------------------------------------------------------
    class ConnectMessage : IMessage
    {
        ConnectedNode m_Node;
        ConnectedRecipient m_Recipient;

        public ConnectMessage( ConnectedNode node, ConnectedRecipient recipient )
        {
            m_Node = node;
            m_Recipient = recipient;
        }
        public void Process()
        {
            m_Node.RaiseOnConnect( m_Recipient );
        }
    }

    class DisconnectMessage : IMessage
    {
        ConnectedNode m_Node;
        ConnectedRecipient m_Recipient;

        public DisconnectMessage( ConnectedNode node, ConnectedRecipient recipient )
        {
            m_Node = node;
            m_Recipient = recipient;
        }
        public void Process()
        {
            m_Node.RaiseOnDisconnect( m_Recipient );
        }
    }

    // --- Class -----------------------------------------------------------------------------------------------------------

    public class ConnectedRecipient : Recipient
    {
        object m_DisconnectLock = new object();
        ConnectStream m_ConnectStream;

        internal long LastReceivedPacketMs { get; private set; }

        public uint LocalId { get; private set; }
        public uint RemoteId { get; private set; }
        public bool IsServer { get; private set; }
        public ConnectionState ConnectionState { get; set; }
        public bool IsConnected => ConnectResult == ConnectResult.Succes;
        public ConnectResult ConnectResult { get; private set; }
        public DisconnectReason DisconnectReason { get; private set; }
        public ConnectedNode ConnectedNode => Node as ConnectedNode;

        internal ConnectedRecipient( ConnectedNode node, IPEndPoint endpoint, UdpClient udpClient ) :
            base( node, endpoint, udpClient )
        {
            IsServer  = false;
            LocalId   = (uint)(new Random()).Next();
            RemoteId  = uint.MaxValue;
            ConnectionState = ConnectionState.NotSet;
            m_ConnectStream = new ConnectStream( this );
            LastReceivedPacketMs = node.Stopwatch.ElapsedMilliseconds;
        }

        internal override void PrepareSend( BinaryWriter writer, StreamId streamId )
        {
            writer.BaseStream.Position = 0;
            writer.Write( LocalId );
            writer.Write( (byte)streamId );
        }

        internal override void Send( byte id, byte[] data, byte channel, SendMethod sendMethod, DeliveryTrace trace )
        {
            switch (sendMethod)
            {
                case SendMethod.Connect:
                m_ConnectStream.AddMessage( id, data );
                break;

                default:
                base.Send( id, data, channel, sendMethod, trace );
                break;
            }
        }

        internal override void FlushDataST( BinaryWriter writer )
        {
            base.FlushDataST( writer );
            m_ConnectStream.FlushST( writer );
        }

        internal override void ReceiveDataWT( BinaryReader reader, BinaryWriter writer )
        {
            LastReceivedPacketMs = ConnectedNode.Stopwatch.ElapsedMilliseconds;
            uint remoteId = reader.ReadUInt32();
            if (ConnectionState == ConnectionState.Active || ConnectionState == ConnectionState.Disconnected)
            {
                if (remoteId == RemoteId)
                {
                    base.ReceiveDataWT( reader, writer );
                }
                else
                {
                    Console.WriteLine( "Received invalid data from recipient when connect state was set to active." );
                    Debug.Assert( false );
                }
            }
            else if (ConnectionState == ConnectionState.Initiating || ConnectionState == ConnectionState.NotSet)
            {
                m_ConnectStream.ReceiveDataNewConnectionWT( reader, writer );
            }
        }

        internal override void ReceiveSystemMessageWT( BinaryReader reader, BinaryWriter writer, byte id, IPEndPoint endpoint, byte channel )
        {
            Debug.Assert( channel == ReliableStream.SystemChannel );
            Debug.Assert( id < (byte)SystemPacketId.Count );

            SystemPacketId enumId = (SystemPacketId)id;

            if (ConnectionState == ConnectionState.NotSet)
            {
                if (enumId == SystemPacketId.Connect)
                {
                    ReceiveConnectWT( reader, writer, channel );
                    return;
                }
            }
            else if (ConnectionState == ConnectionState.Initiating)
            {
                switch (enumId)
                {
                    case SystemPacketId.ConnectInvalidPw:
                    ReceiveConnectInvalidPwWT( reader, writer, channel );
                    return;

                    case SystemPacketId.ConnectMaxUsers:
                    ReceiveConnectMaxUsersWT( reader, writer, channel );
                    return;

                    case SystemPacketId.ConnectAccepted:
                    ReceiveConnectAcceptedWT( reader, writer, channel );
                    return;
                }
            }
            else if (ConnectionState == ConnectionState.Active)
            {
                switch (enumId)
                {
                    case SystemPacketId.IdPackRequest:
                    ConnectedNode.GroupManager.ReceiveIdPacketRequestWT( reader, writer, endpoint, channel );
                    return;

                    case SystemPacketId.IdPackProvide:
                    ConnectedNode.GroupManager.ReceiveIdPacketProvideWT( reader, writer, endpoint, channel );
                    return;

                    case SystemPacketId.CreateGroup:
                    ConnectedNode.GroupManager.ReceiveGroupCreatedWT( reader, writer, endpoint, channel );
                    return;

                    case SystemPacketId.DestroyGroup:
                    ConnectedNode.GroupManager.ReceiveGroupDestroyedWT( reader, writer, endpoint, channel );
                    return;

                    case SystemPacketId.DestroyAllGroups:
                    ConnectedNode.GroupManager.ReceiveDestroyAllGroupsWT( reader, writer, endpoint, channel );
                    return;

                    case SystemPacketId.Disconnect:
                    ReceiveDisconnectWT( reader, writer, channel );
                    return;
                }
            }

            // Packet not handled yet.
            base.ReceiveSystemMessageWT( reader, writer, id, endpoint, channel );
        }

        // --- Messages ---------------------------------------------------------------------------------------------

        internal void SendConnect( BinaryWriter writer, string pw )
        {
            Debug.Assert( ConnectionState == ConnectionState.NotSet );
            Debug.Assert( ConnectResult == ConnectResult.NotSet );
            ConnectionState = ConnectionState.Initiating;
            writer.ResetPosition();
            writer.Write( LocalId );
            writer.Write( pw );
            ConnectedNode.SendPrivate( (byte)SystemPacketId.Connect, writer.GetData(), ReliableStream.SystemChannel, SendMethod.Connect, EndPoint );
        }

        internal void ReceiveConnectWT( BinaryReader reader, BinaryWriter writer, byte channel )
        {
            uint remoteId = reader.ReadUInt32();

            // If already active, send already connected.
            if (ConnectionState == ConnectionState.Active)
            {
                SendConnectAccepted( writer, remoteId, channel );
                return;
            }

            // Check max users.
            if (ConnectedNode.NumRecipients >= ConnectedNode.MaxUsers+1/*Add one because num is already incremented when coming here*/ )
            {
                SendConnectMaxUsers( writer, remoteId, channel );
                return;
            }

            // Check valid Pw.
            string pw = reader.ReadString();
            if (ConnectedNode.Password != pw) // Note strings are immutable, so always thread safe.
            {
                SendConnectInvalidPw( writer, remoteId, channel );
                return;
            }

            // In client/server the state on receive side is NotSet.
            // In p2p, the state might be either in NotSet or Initiating becuase both parties try to connect to eachother.
            if (ConnectionState == ConnectionState.NotSet || ConnectionState == ConnectionState.Initiating)
            {
                ConnectionState = ConnectionState.Active;
                ConnectResult   = ConnectResult.Succes;
                Debug.Assert( RemoteId == uint.MaxValue );
                Debug.Assert( LocalId != uint.MaxValue );
                RemoteId = remoteId;
                ConnectedNode.AddMessage( new ConnectMessage( ConnectedNode, this ) );
                SendConnectAccepted( writer, remoteId, channel );
            }
        }

        internal void SendConnectMaxUsers( BinaryWriter writer, uint remoteId, byte channel )
        {
            writer.ResetPosition();
            writer.Write( remoteId );
            ConnectedNode.SendPrivate( (byte)SystemPacketId.ConnectMaxUsers, writer.GetData(), channel, SendMethod.Connect, EndPoint );
        }

        internal void ReceiveConnectInvalidPwWT( BinaryReader reader, BinaryWriter writer, byte channel )
        {
            // Sent localId is reflected. Check this.
            uint localId = reader.ReadUInt32();
            // In p2p, the connectState might have already changed to active as both parties try to connect to eachother.
            if (ConnectionState == ConnectionState.Initiating && LocalId == localId)
            {
                ConnectResult = ConnectResult.InvalidPw;
                ConnectedNode.AddMessage( new ConnectMessage( ConnectedNode, this ) );
            }
        }

        internal void SendConnectInvalidPw( BinaryWriter writer, uint remoteId, byte channel )
        {
            writer.ResetPosition();
            writer.Write( remoteId );
            ConnectedNode.SendPrivate( (byte)SystemPacketId.ConnectInvalidPw, writer.GetData(), channel, SendMethod.Connect, EndPoint );
        }

        internal void ReceiveConnectMaxUsersWT( BinaryReader reader, BinaryWriter writer, byte channel )
        {
            // Sent localId is reflected. Check this.
            uint localId = reader.ReadUInt32();
            // In p2p, the connectState might have already changed to active as both parties try to connect to eachother.
            if (ConnectionState == ConnectionState.Initiating && LocalId == localId)
            {
                ConnectResult = ConnectResult.MaxUsers;
                ConnectedNode.AddMessage( new ConnectMessage( ConnectedNode, this ) );
            }
        }

        internal void ReceiveConnectAcceptedWT( BinaryReader reader, BinaryWriter writer, byte channel )
        {
            // Sent localId, is reflected. Check this.
            uint localId  = reader.ReadUInt32();
            uint remoteId = reader.ReadUInt32();
            // In p2p, the connectState might have already changed to active as both parties try to connect to eachother.
            if (ConnectionState == ConnectionState.Initiating && LocalId == localId)
            {
                Debug.Assert( RemoteId == uint.MaxValue );
                RemoteId = remoteId;
                ConnectionState = ConnectionState.Active;
                ConnectResult   = ConnectResult.Succes;
                ConnectedNode.AddMessage( new ConnectMessage( ConnectedNode, this ) );
            }
        }

        internal void SendConnectAccepted( BinaryWriter writer, uint remoteId, byte channel )
        {
            writer.ResetPosition();
            writer.Write( remoteId );
            writer.Write( LocalId );
            ConnectedNode.SendPrivate( (byte)SystemPacketId.ConnectAccepted, writer.GetData(), channel, SendMethod.Connect, EndPoint );
        }

        internal void ReceiveDisconnectWT( BinaryReader reader, BinaryWriter writer, byte channel )
        {
            if (ConnectionState == ConnectionState.Active)
            {
                ConnectionState  = ConnectionState.Disconnected;
                DisconnectReason = DisconnectReason.Requested;
                ConnectedNode.AddMessage( new DisconnectMessage( ConnectedNode, this ) );
            }
        }

        internal DeliveryTrace SendDisconnect( BinaryWriter writer, byte channel, bool traceDelivery )
        {
            // Need disconnect lock because DisconnectReason can be set from main thread or from receiving thread.
            lock (m_DisconnectLock)
            {
                if (ConnectionState != ConnectionState.Active)
                {
                    return null;
                }
                ConnectionState  = ConnectionState.Disconnected;
                DisconnectReason = DisconnectReason.Requested;
            }
            // Note: Sending disconnect is done in reliable fashion, other than the connect sequence.
            return ConnectedNode.SendPrivate( (byte)SystemPacketId.Disconnect, null, channel, SendMethod.Reliable, EndPoint, null, traceDelivery );
        }

        internal void MarkAsLostConnectionWT()
        {
            // Need disconnect lock because DisconnectReason can be set from main thread or from receiving thread.
            lock (m_DisconnectLock)
            {
                if (ConnectionState != ConnectionState.Active)
                {
                    return;
                }
                ConnectionState  = ConnectionState.Disconnected;
                DisconnectReason = DisconnectReason.Unreachable;
            }
            ConnectedNode.AddMessage( new DisconnectMessage( ConnectedNode, this ) );
        }
    }
}
