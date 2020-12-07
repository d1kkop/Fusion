using System;
using System.Diagnostics;
using System.IO;
using System.Net;

namespace Fusion
{
    public enum ConnectionState
    {
        NotSet,
        Initiating,
        Active,
        Closed
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

    /* ConnectStream intenteion is to put the recipient in a 'Active' state.
     * We dont use the reliable stream for this because the reliablie stream does not know anything about a connection and as such
     * we might receive invalid acks or other data from recipients.
     * So, before any streams become active (Reliable, Unreliable, VariableGroup, etc) we first try to put
     * a recipient in an 'Active' state by handshaking. Once done we can 'easily' throw out data that is out of bounds or invalid not otherwise specified.
     */
    public class ConnectStream : UnreliableStream
    {
        public static int  ConnectIntervalMs         = 300;
        public static int  ConnecTimeoutMs           = 20000;
        public static int  KeepAliveMs               = 5000;
        public static int  DisconnectLingerTimeMs    = 1000;

        string m_ConnectPw;
        long   m_StartConnectTime;
        long   m_LastConnectAttempt;
        long   m_LastKeepAliveAttempt;
        DeliveryTrace m_AliveDelivery;

        internal uint LocalId { get; private set; }
        internal uint RemoteId { get; private set; }
        internal ConnectionState ConnectionState { get; private set; }
        internal bool IsConnected => ConnectResult == ConnectResult.Succes;
        internal ConnectResult ConnectResult { get; private set; }
        internal DisconnectReason DisconnectReason { get; private set; }
        internal ConnectedRecipient ConnectedRecipient => Recipient as ConnectedRecipient;
        internal ConnectedNode ConnectedNode => ConnectedRecipient.ConnectedNode;
        internal IPEndPoint EndPoint => ConnectedRecipient.EndPoint;
        internal bool DisconnectRequestWasRemote { get; private set; }

        internal ConnectStream( ConnectedRecipient recipient ) :
            base( recipient )
        {
            LocalId     = (uint)(new Random()).Next();
            RemoteId    = uint.MaxValue;
        }

        internal override StreamId GetStreamId()
        {
            return StreamId.CID;
        }

        internal void StartConnecting( string pw )
        {
            Debug.Assert( ConnectionState == ConnectionState.NotSet );
            Debug.Assert( ConnectResult == ConnectResult.NotSet );
            Debug.Assert( DisconnectReason == DisconnectReason.NotSet );
            m_ConnectPw = pw;
            m_StartConnectTime = ConnectedNode.TimeNow;
            ConnectionState = ConnectionState.Initiating;
            SendConnect( ConnectedNode.BinWriter );
        }

        internal void HandleConnectST( BinaryWriter writer )
        {
            if (!ConnectedNode.ConnectingCanTimeout)
                return;
            if (ConnectionState != ConnectionState.Initiating)
                return;

            if (ConnectedNode.TimeNow - m_LastConnectAttempt > ConnectIntervalMs)
            {
                m_LastConnectAttempt = ConnectedNode.TimeNow;
                SendConnect( writer );
            }
            else if (ConnectedNode.TimeNow - m_StartConnectTime > ConnecTimeoutMs &&
                     CheckConnectionState( ConnectionState.Initiating, ConnectionState.Closed ))
            {
                ConnectResult = ConnectResult.Timedout;
                ConnectedNode.AddMessage( new ConnectMessage( ConnectedNode, ConnectedRecipient ) );
            }
        }

        internal void HandleKeepAliveST( BinaryWriter writer )
        {
            if (!ConnectedNode.KeepConnectionsAlive)
                return;
            if (ConnectionState != ConnectionState.Active)
                return;

            if (m_AliveDelivery == null)
            {
                if (ConnectedNode.TimeNow - m_LastKeepAliveAttempt > KeepAliveMs)
                {
                    m_LastKeepAliveAttempt = ConnectedNode.TimeNow;
                    m_AliveDelivery = SendKeepAlive( writer, ReliableStream.SystemChannel );
                }
            }
            else if (m_AliveDelivery.PeekAll())
            {
                m_AliveDelivery = null;
                m_LastKeepAliveAttempt = ConnectedNode.TimeNow;
            }
            else if (ConnectedNode.TimeNow - m_LastKeepAliveAttempt > KeepAliveMs &&
                     CheckConnectionState( ConnectionState.Active, ConnectionState.Closed ))
            {
                DisconnectReason = DisconnectReason.Unreachable;
                ConnectedNode.AddMessage( new DisconnectMessage( ConnectedNode, ConnectedRecipient ) );
            }
        }

        internal override void FlushST( BinaryWriter writer )
        {
            HandleConnectST( writer );
            HandleKeepAliveST( writer );
            base.FlushST( writer );
        }

        internal void ReceiveDataNewConnectionWT( BinaryReader reader, BinaryWriter writer )
        {
            byte streamId = reader.ReadByte();
            if ((StreamId)streamId != StreamId.CID)
            {
                return;
            }
            uint sequence = reader.ReadUInt32();
            if (IsSequenceNewer( sequence, m_UnreliableDataRT.m_Expected ))
            {
                while (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    byte dataId       = reader.ReadByte();
                    bool isSystem     = reader.ReadBoolean();
                    ushort messageLen = reader.ReadUInt16();
                    long oldPosition  = reader.BaseStream.Position;
                    Debug.Assert( isSystem );
                    Recipient.ReceiveSystemMessageWT( reader, writer, dataId, Recipient.EndPoint, ReliableStream.SystemChannel );
                    sequence += 1;
                    // Move to next read position. This is better as the receive function may not have read all data incase of irrelevant.
                    reader.BaseStream.Position = oldPosition + messageLen;
                }
                m_UnreliableDataRT.m_Expected = sequence;
            }
        }

        // --- Messages ---------------------------------------------------------------------------------------------

        internal void SendConnect( BinaryWriter writer )
        {
            writer.ResetPosition();
            writer.Write( LocalId );
            writer.Write( m_ConnectPw );
            ConnectedRecipient.ConnectedNode.SendPrivate( (byte)SystemPacketId.Connect, writer.GetData(), ReliableStream.SystemChannel, SendMethod.Connect, Recipient.EndPoint );
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
            if (ConnectedRecipient.ConnectedNode.NumRecipients >= ConnectedRecipient.ConnectedNode.MaxUsers+1/*Add one because num is already incremented when coming here*/ )
            {
                SendConnectMaxUsers( writer, remoteId, channel );
                return;
            }

            // Check valid Pw.
            string pw = reader.ReadString();
            if (ConnectedRecipient.ConnectedNode.Password != pw) // Note strings are immutable, so always thread safe.
            {
                SendConnectInvalidPw( writer, remoteId, channel );
                return;
            }

            // In client/server the state on receive side is NotConnected.
            // In p2p, the state might be either in NotConnected or Initiating becuase both parties try to connect to eachother.
            if (CheckConnectionState( ConnectionState.NotSet, ConnectionState.Initiating, ConnectionState.Active ))
            {
                ConnectResult = ConnectResult.Succes;
                Debug.Assert( RemoteId == uint.MaxValue );
                Debug.Assert( LocalId != uint.MaxValue );
                RemoteId = remoteId;
                ConnectedNode.AddMessage( new ConnectMessage( ConnectedNode, ConnectedRecipient ) );
                SendConnectAccepted( writer, remoteId, channel );
            }
        }

        internal void ReceiveConnectInvalidPwWT( BinaryReader reader, BinaryWriter writer, byte channel )
        {
            uint localId = reader.ReadUInt32();
            if (LocalId == localId && CheckConnectionState( ConnectionState.Initiating, ConnectionState.Closed ))
            {
                ConnectResult = ConnectResult.InvalidPw;
                ConnectedNode.AddMessage( new ConnectMessage( ConnectedNode, ConnectedRecipient ) );
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
            uint localId = reader.ReadUInt32();
            if (LocalId == localId && CheckConnectionState( ConnectionState.Initiating, ConnectionState.Closed ))
            {
                ConnectResult = ConnectResult.MaxUsers;
                ConnectedNode.AddMessage( new ConnectMessage( ConnectedNode, ConnectedRecipient ) );
            }
        }

        internal void SendConnectMaxUsers( BinaryWriter writer, uint remoteId, byte channel )
        {
            writer.ResetPosition();
            writer.Write( remoteId );
            ConnectedNode.SendPrivate( (byte)SystemPacketId.ConnectMaxUsers, writer.GetData(), channel, SendMethod.Connect, EndPoint );
        }

        internal void ReceiveConnectAcceptedWT( BinaryReader reader, BinaryWriter writer, byte channel )
        {
            uint localId  = reader.ReadUInt32();
            uint remoteId = reader.ReadUInt32();
            if (LocalId == localId && CheckConnectionState( ConnectionState.Initiating, ConnectionState.Active ))
            {
                Debug.Assert( RemoteId == uint.MaxValue );
                RemoteId = remoteId;
                ConnectionState = ConnectionState.Active;
                ConnectResult   = ConnectResult.Succes;
                ConnectedNode.AddMessage( new ConnectMessage( ConnectedNode, ConnectedRecipient ) );
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
            if (CheckConnectionState( ConnectionState.Active, ConnectionState.Closed ))
            {
                DisconnectReason = DisconnectReason.Requested;
                DisconnectRequestWasRemote = true;
                ConnectedNode.AddMessage( new DisconnectMessage( ConnectedNode, ConnectedRecipient ) );
            }
        }

        internal DeliveryTrace SendDisconnect( BinaryWriter writer, byte channel, bool traceDelivery )
        {
            if (CheckConnectionState( ConnectionState.Active, ConnectionState.Closed ))
            {
                // Note: Sending disconnect is done in reliable fashion, other than the connect sequence.
                DisconnectReason = DisconnectReason.Requested;
                return ConnectedNode.SendPrivate( (byte)SystemPacketId.Disconnect, null, channel, SendMethod.Reliable, EndPoint, null, traceDelivery );
            }
            return null;
        }

        internal void ReceiveKeepAliveWT( BinaryReader reader, byte channel )
        {
            // Nothing to do with this message
        }

        internal DeliveryTrace SendKeepAlive( BinaryWriter writer, byte channel )
        {
            return ConnectedNode.SendPrivate( (byte)SystemPacketId.KeepAlive, null, channel, SendMethod.Reliable, EndPoint, null, true );
        }


        // --- Thred safe check and change connection state. ----------------------------------------------------------------------------------
        bool CheckConnectionState( ConnectionState mustBe, ConnectionState setTo )
        {
            lock (this)
            {
                if (ConnectionState == mustBe)
                {
                    ConnectionState = setTo;
                    return true;
                }
            }
            return false;
        }

        bool CheckConnectionState( ConnectionState mustBe, ConnectionState orMustBe, ConnectionState setTo )
        {
            lock (this)
            {
                if (ConnectionState == mustBe || ConnectionState == orMustBe)
                {
                    ConnectionState = setTo;
                    return true;
                }
            }
            return false;
        }
    }
}
