using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;

namespace Fusion
{
    internal enum SystemPacketId
    {
        IdPackRequest,
        IdPackProvide,
        CreateGroup,
        DestroyGroup,
        DestroyAllGroups,
        Connect,
        ConnectInvalidPw,
        ConnectMaxUsers,
        ConnectAccepted,
        Disconnect,
        Count
    }

    public class ReliableStream
    {
        public const byte SystemChannel = 255;
        public const byte RID    = 0;
        public const byte RACK   = 1;
        const int MaxFrameSize   = 1400; // Ethernet frame is max 1500. Reduce 100 for overhead in other layers.
        const int MaxPayloadSize = MaxFrameSize-10; // Reduce 10 from overhead of header.

        struct SendMessage
        {
            internal uint m_Sequence;
            internal byte m_Id;
            internal byte [] m_Payload;
        }

        struct RecvMessage
        {
            internal byte m_Channel;
            internal byte m_Id;
            internal byte [] m_Payload;
            internal IPEndPoint m_Recipient;
        }

        class DataMT // MainThread data
        {
            internal uint m_Newest;
            internal Queue<SendMessage> m_Messages = new Queue<SendMessage>();
        }

        class DataRT // ReceiveThread data
        {
            internal uint m_Expected;
            internal uint m_AckExpected;
            internal Queue<RecvMessage> m_Messages = new Queue<RecvMessage>();
        }


        DataMT m_ReliableDataMT;
        DataRT m_ReliableDataRT;

        internal Recipient Recipient { get; }
        internal byte Channel { get;  }

        internal ReliableStream( Recipient recipient, byte channel )
        {
            Recipient = recipient;
            Channel   = channel;
            m_ReliableDataMT = new DataMT();
            m_ReliableDataRT = new DataRT();
        }

        internal void AddMessage( byte packetId, byte[] payload )
        {
            if ( payload != null && payload.Length > MaxPayloadSize)
                throw new ArgumentException( "Payload null or exceeding max size of " + MaxPayloadSize );

            // Construct message
            SendMessage rm = new SendMessage();
            rm.m_Id        = packetId;
            rm.m_Payload   = payload;
            rm.m_Sequence  = m_ReliableDataMT.m_Newest++;

            // Add to list of messages thread safely
            lock (m_ReliableDataMT.m_Messages)
            {
                m_ReliableDataMT.m_Messages.Enqueue( rm );
            }
        }

        internal void Sync()
        {
            lock(m_ReliableDataRT.m_Messages)
            {
                while (m_ReliableDataRT.m_Messages.Count !=0)
                {
                    var msg = m_ReliableDataRT.m_Messages.Dequeue();
                    Recipient.Node.RaiseOnMessage( msg.m_Id, msg.m_Payload, msg.m_Recipient, Channel );
                }
            }
        }

        internal void FlushST(BinaryWriter binWriter)
        {
            // RID(1) | ChannelID(1) | Sequence(4) | MsgLen(2) | Msg(variable)

            int numMessagesAdded = 0;
            lock (m_ReliableDataMT.m_Messages)
            {
                if (m_ReliableDataMT.m_Messages.Count == 0)
                    return;

                binWriter.BaseStream.Position = 0;
                binWriter.Write( RID );
                binWriter.Write( Channel );

                // Only send sequence of first message, other sequences are consequative.
                binWriter.Write( m_ReliableDataMT.m_Messages.Peek().m_Sequence );

                // Write each message with Length, ID & payload. The sequence is always the first +1 for each message.
                foreach (var msg in m_ReliableDataMT.m_Messages)
                {
                    if (msg.m_Payload != null)
                    {
                        Debug.Assert( msg.m_Payload.Length <= MaxPayloadSize );
                        binWriter.Write( (ushort)msg.m_Payload.Length );
                        binWriter.Write( msg.m_Id );
                        binWriter.Write( msg.m_Payload );
                    }
                    else
                    {
                        binWriter.Write( (ushort)0 );
                        binWriter.Write( msg.m_Id );
                    }
                    // Avoid fragmentation and exceeding max recvBuffer size (default=65536).
                    if (binWriter.BaseStream.Position > MaxFrameSize)
                        break;
                    ++numMessagesAdded;
                }
            }

            // If Payload is too big, we cannot send it. We ensure however at the point where a message is inserted,
            // that no such payload can be added. Assert this.
            Debug.Assert( numMessagesAdded!=0 );

            // Eventhough this is already in a send thread, do the actual send async to avoid keeping the lock longer than necessary.
            Recipient.UDPClient.SendAsync( binWriter.GetData(), (int)binWriter.BaseStream.Position, Recipient.EndPoint );
        }

        internal void ReceiveDataWT( BinaryReader reader, BinaryWriter writer )
        {
            uint sequence = reader.ReadUInt32();
            if (sequence == m_ReliableDataRT.m_Expected) // Unexpected
            {
                while (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    long oldPosition  = reader.BaseStream.Position;
                    ushort messageLen = reader.ReadUInt16();
                    byte   id         = reader.ReadByte();

                    // Peek if message is system message. If so, handle system messages directly in the worker thread.
                    // However, do NOT spawn new worker thread as that could invalidate the reliability order.
                    if ( Channel == ReliableStream.SystemChannel )
                    {
                        Debug.Assert( id < (byte)SystemPacketId.Count );
                        Recipient.ReceiveSystemMessageWT( reader, writer, id, Recipient.EndPoint, Channel );
                    }
                    else
                    {
                        // Make reliable received message
                        RecvMessage rm = new RecvMessage();
                        rm.m_Channel   = Channel;
                        rm.m_Id        = id;
                        rm.m_Payload   = messageLen != 0 ? reader.ReadBytes( messageLen ) : null;
                        rm.m_Recipient = Recipient.EndPoint;

                        // Add it thread safely
                        lock (m_ReliableDataRT.m_Messages)
                        {
                            m_ReliableDataRT.m_Messages.Enqueue( rm );
                        }
                    }

                    // Eventhough we should be exactly at the next package, if serialization goes wrong step to correct position
                    // as otherwise we might risk sending false positive received packages and on the recipient. This will
                    // then result in a sliding window error but do not know from which packet. So better to figure errors out here.
                    reader.BaseStream.Position = oldPosition + 1 + 2 + messageLen;
                    sequence += 1;
                }
                m_ReliableDataRT.m_Expected = sequence;
            }
            // Always send ack. Ack may have been lost previously. Keep sending this until transmitter knows it was delivered.
            FlushAckWT( writer );
        }

        void FlushAckWT(BinaryWriter binWriter)
        {
            binWriter.BaseStream.Position = 0;
            binWriter.Write( RACK );
            binWriter.Write( Channel );
            binWriter.Write( m_ReliableDataRT.m_Expected-1 ); // Ack yields the new value to expect, so Ack-1 is the last one received.
            Recipient.UDPClient.SendAsync( binWriter.GetData(), (int)binWriter.BaseStream.Position, Recipient.EndPoint );
        }

        internal void ReceiveAckWT( BinaryReader reader )
        {
            uint ack = reader.ReadUInt32();
            if (IsSequenceNewer( ack, m_ReliableDataRT.m_AckExpected ))
            {
                int numPacketsToDrop = (int)(ack - m_ReliableDataRT.m_AckExpected) + 1;
                lock (m_ReliableDataMT.m_Messages)
                {
                    Debug.Assert( m_ReliableDataMT.m_Messages.Count >= numPacketsToDrop );
                    while (numPacketsToDrop!=0)
                    {
                        m_ReliableDataMT.m_Messages.Dequeue();
                        numPacketsToDrop--;
                    }
                }
                m_ReliableDataRT.m_AckExpected = ack+1;
            }
        }

        static bool IsSequenceNewer( uint incoming, uint having )
        {
            return (incoming - having) < (uint.MaxValue>>1);
        }
    }
}
