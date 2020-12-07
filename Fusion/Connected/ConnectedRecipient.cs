using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace Fusion
{
    public class ConnectedRecipient : Recipient
    {
        internal ConnectStream ConnectStream { get; private set; }

        public bool IsServer { get; private set; }
        public ConnectedNode ConnectedNode => Node as ConnectedNode;

        internal ConnectedRecipient( ConnectedNode node, IPEndPoint endpoint, UdpClient udpClient ) :
            base( node, endpoint, udpClient )
        {
            IsServer  = false;
            ConnectStream = new ConnectStream( this );
        }

        internal override void PrepareSend( BinaryWriter writer, StreamId streamId )
        {
            writer.BaseStream.Position = 0;
            writer.Write( ConnectStream.LocalId );
            writer.Write( (byte)streamId );
        }

        internal override void Send( byte id, byte [] data, byte channel, SendMethod sendMethod, DeliveryTrace trace )
        {
            switch (sendMethod)
            {
                case SendMethod.Connect:
                ConnectStream.AddMessage( id, true, data );
                break;

                default:
                base.Send( id, data, channel, sendMethod, trace );
                break;
            }
        }

        internal override void FlushDataST( BinaryWriter writer )
        {
            base.FlushDataST( writer );
            ConnectStream.FlushST( writer );
        }

        internal override void ReceiveDataWT( BinaryReader reader, BinaryWriter writer )
        {
            // First data piece always an ID which identifies the connection. This is necessary to distinquish
            // between data from the same endpoint that is received after the endpoint has been removed and also
            // to avoid handling data with invalid message format to avoid raising an exception.
            uint remoteId = reader.ReadUInt32();
            StreamId streamId = (StreamId) reader.PeekChar();
            ConnectionState connState = ConnectStream.ConnectionState;
            if (streamId != StreamId.CID &&
               (connState == ConnectionState.Active ||
               (connState == ConnectionState.Closed && !ConnectStream.DisconnectRequestWasRemote)) /* To leave acks come through */ )
            {
                if (remoteId == ConnectStream.RemoteId)
                {
                    base.ReceiveDataWT( reader, writer );
                }
                else if (ConnectStream.RemoteId != uint.MaxValue)
                {
                    Debug.WriteLine( "Received invalid data from recipient when connect state was set to active." );
                    Debug.Assert( false );
                }
            }
            else if (streamId == StreamId.CID && (connState == ConnectionState.Initiating || connState == ConnectionState.NotSet))
            {
                ConnectStream.ReceiveDataNewConnectionWT( reader, writer );
            }
        }

        internal override void ReceiveSystemMessageWT( BinaryReader reader, BinaryWriter writer, byte id, IPEndPoint endpoint, byte channel )
        {
            Debug.Assert( channel == ReliableStream.SystemChannel || id == (byte)SystemPacketId.RPC );
            Debug.Assert( id < (byte)SystemPacketId.Count );

            SystemPacketId enumId = (SystemPacketId)id;
            ConnectionState connState = ConnectStream.ConnectionState;

            if (connState == ConnectionState.NotSet)
            {
                if (enumId == SystemPacketId.Connect)
                {
                    ConnectStream.ReceiveConnectWT( reader, writer, channel );
                    return;
                }
            }
            else if (connState == ConnectionState.Initiating)
            {
                switch (enumId)
                {
                    case SystemPacketId.ConnectInvalidPw:
                    ConnectStream.ReceiveConnectInvalidPwWT( reader, writer, channel );
                    return;

                    case SystemPacketId.ConnectMaxUsers:
                    ConnectStream.ReceiveConnectMaxUsersWT( reader, writer, channel );
                    return;

                    case SystemPacketId.ConnectAccepted:
                    ConnectStream.ReceiveConnectAcceptedWT( reader, writer, channel );
                    return;
                }
            }
            if (connState == ConnectionState.Active)
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
                    ConnectStream.ReceiveDisconnectWT( reader, writer, channel );
                    return;

                    case SystemPacketId.KeepAlive:
                    ConnectStream.ReceiveKeepAliveWT( reader, channel );
                    break;

                    case SystemPacketId.RPC:
                    ConnectedNode.ReceiveRPCWT( reader, channel, this );
                    break;
                }
            }

            // Packet not handled yet.
            base.ReceiveSystemMessageWT( reader, writer, id, endpoint, channel );
        }
    }
}
