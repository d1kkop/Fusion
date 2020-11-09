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

        public bool IsServer { get; private set; }
        public ConnectionState ConnectionState { get; set; }
        public bool IsConnected => ConnectResult == ConnectResult.Succes;
        public ConnectResult ConnectResult { get; private set; }
        public DisconnectReason DisconnectReason { get; private set; }
        internal ConnectedNode ConnectedNode => Node as ConnectedNode;

        internal ConnectedRecipient( ConnectedNode node, IPEndPoint endpoint, UdpClient udpClient ):
            base( node, endpoint, udpClient)
        {
            IsServer  = false;
            ConnectionState = ConnectionState.NotSet;
        }

        internal override void ReceiveSystemMessageWT( BinaryReader reader, BinaryWriter writer, byte id, IPEndPoint endpoint, byte channel )
        {
            Debug.Assert( channel == ReliableStream.SystemChannel );
            Debug.Assert( id < (byte)SystemPacketId.Count );

            // If we receive a disconnect packet, we set this state to disconnected so that other packets are processed. 
            // This happens in the WT thread.
            // If we disconnect ourselves, we may still receive a disconnect packet from the remote, but Sync is no longer 
            // callled after a request to disconnect so no messages will be processed or any discrepancy therefore of can occur.
            if (ConnectionState == ConnectionState.Disconnected)
            {
                return;
            }

            SystemPacketId enumId = (SystemPacketId)id;
            switch (enumId)
            {
                case SystemPacketId.IdPackRequest:
                ConnectedNode.GroupManager.ReceiveIdPacketRequestWT( reader, writer, endpoint, channel );
                break;

                case SystemPacketId.IdPackProvide:
                ConnectedNode.GroupManager.ReceiveIdPacketProvideWT( reader, writer, endpoint, channel );
                break;

                case SystemPacketId.CreateGroup:
                ConnectedNode.GroupManager.ReceiveGroupCreatedWT( reader, writer, endpoint, channel );
                break;

                case SystemPacketId.DestroyGroup:
                ConnectedNode.GroupManager.ReceiveGroupDestroyedWT( reader, writer, endpoint, channel );
                break;

                case SystemPacketId.DestroyAllGroups:
                ConnectedNode.GroupManager.ReceiveDestroyAllGroupsWT( reader, writer, endpoint, channel );
                break;

                case SystemPacketId.Connect:
                ReceiveConnectWT( reader, writer, channel );
                break;

                case SystemPacketId.ConnectInvalidPw:
                ReceiveConnectInvalidPwWT( reader, writer, channel );
                break;

                case SystemPacketId.ConnectMaxUsers:
                ReceiveConnectMaxUsersWT( reader, writer, channel );
                break;

                case SystemPacketId.ConnectAccepted:
                ReceiveConnectAcceptedWT( reader, writer, channel );
                break;

                case SystemPacketId.Disconnect:
                ReceiveDisconnectWT( reader, writer, channel );
                break;

                case SystemPacketId.Count:
                break;

                default:
                base.ReceiveSystemMessageWT( reader, writer, id, endpoint, channel );
                break;
            }
        }

        // --- Messages ---------------------------------------------------------------------------------------------

        internal void SendConnect( BinaryWriter writer, string pw )
        {
            Debug.Assert( ConnectionState == ConnectionState.NotSet );
            Debug.Assert( ConnectResult == ConnectResult.NotSet );
            ConnectionState = ConnectionState.Initiating;
            writer.ResetPosition();
            writer.Write( pw );
            ConnectedNode.SendPrivate( (byte)SystemPacketId.Connect, writer.GetData(), ReliableStream.SystemChannel, SendMethod.Reliable, EndPoint );
        }

        internal void ReceiveConnectWT( BinaryReader reader, BinaryWriter writer, byte channel )
        {
            // If already active, send already connected.
            if ( ConnectionState == ConnectionState.Active )
            {
                return;
            }

            // Check max users.
            if (ConnectedNode.NumRecipients >= ConnectedNode.MaxUsers+1/*Add one because num is already incremented when coming here*/)
            {
                SendConnectMaxUsers( writer, channel );
                return;
            }

            // Check valid Pw.
            string pw = reader.ReadString();
            if (ConnectedNode.Password != pw) // Note strings are immutable, so always thread safe.
            {
                SendConnectInvalidPw( writer, channel );
                return;
            }

            // In p2p, it may be set already from an accept message.
            if (ConnectionState == ConnectionState.NotSet)
            {
                ConnectionState = ConnectionState.Active;
                ConnectResult   = ConnectResult.Succes;
                ConnectedNode.AddMessage( new ConnectMessage( ConnectedNode, this ) );
                SendConnectAccepted( writer, channel );
            }
        }

        internal void SendConnectMaxUsers( BinaryWriter writer, byte channel )
        {
            ConnectedNode.SendPrivate( (byte)SystemPacketId.ConnectMaxUsers, null, channel, SendMethod.Reliable, EndPoint );
        }

        internal void ReceiveConnectInvalidPwWT( BinaryReader reader, BinaryWriter writer, byte channel )
        {
            // Consider both client/server ne p2p situation.
            if (ConnectionState == ConnectionState.NotSet)
            {
                ConnectResult = ConnectResult.InvalidPw;
                ConnectedNode.AddMessage( new ConnectMessage( ConnectedNode, this ) );
            }
        }

        internal void SendConnectInvalidPw( BinaryWriter writer, byte channel )
        {
            ConnectedNode.SendPrivate( (byte)SystemPacketId.ConnectInvalidPw, null, channel, SendMethod.Reliable, EndPoint );
        }

        internal void ReceiveConnectMaxUsersWT( BinaryReader reader, BinaryWriter writer, byte channel )
        {
            // Consider both client/server ne p2p situation.
            if (ConnectionState == ConnectionState.NotSet)
            {
                ConnectResult = ConnectResult.MaxUsers;
                ConnectedNode.AddMessage( new ConnectMessage( ConnectedNode, this ) );
            }
        }

        internal void ReceiveConnectAcceptedWT( BinaryReader reader, BinaryWriter writer, byte channel )
        {
            // Consider both client/server ne p2p situation.
            if (ConnectionState == ConnectionState.NotSet)
            {
                Debug.Assert( ConnectionState == ConnectionState.Initiating );
                ConnectionState = ConnectionState.Active;
                ConnectResult   = ConnectResult.Succes;
                ConnectedNode.AddMessage( new ConnectMessage( ConnectedNode, this ) );
            }
        }

        internal void SendConnectAccepted( BinaryWriter writer, byte channel )
        {
            ConnectedNode.SendPrivate( (byte)SystemPacketId.ConnectAccepted, null, channel, SendMethod.Reliable, EndPoint );
        }

        internal void ReceiveDisconnectWT( BinaryReader reader, BinaryWriter writer, byte channel )
        {
            // Consider both client/server ne p2p situation.
            if (ConnectionState == ConnectionState.Active)
            {
                ConnectionState  = ConnectionState.Disconnected;
                DisconnectReason = DisconnectReason.Requested;
                ConnectedNode.AddMessage( new DisconnectMessage( ConnectedNode, this ) );
            }
        }

        internal void SendDisconnect( BinaryWriter writer, byte channel )
        {
            lock (m_DisconnectLock)
            {
                if ( ConnectionState != ConnectionState.Active )
                {
                    return;
                }
                ConnectionState  = ConnectionState.Disconnected;
                DisconnectReason = DisconnectReason.Requested;
            }
            ConnectedNode.SendPrivate( (byte)SystemPacketId.Disconnect, null, channel, SendMethod.Reliable, EndPoint );
        }
    }
}
