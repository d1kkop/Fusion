using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;

namespace Fusion
{
    public class ReliableStream
    {
        public const byte RID   = 10;
        public const byte RACK  = 11;
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
            internal uint m_AckExpected;
            internal uint m_AckReceived;
            internal Queue<RecvMessage> m_Messages = new Queue<RecvMessage>();
        }

        byte m_Channel;
        Recipient    m_Recipient;
        MemoryStream m_MemWriteStream = new MemoryStream();
        BinaryWriter m_BinWriter;
        DataMT m_ReliableDataMT = new DataMT();
        DataRT m_ReliableDataRT = new DataRT();

        public ReliableStream( Recipient recipient, byte channel )
        {
            m_Recipient = recipient;
            m_Channel   = channel;
            m_BinWriter = new BinaryWriter( m_MemWriteStream );
        }

        internal void AddMessage( byte packetId, byte[] payload )
        {
            if ( payload != null && payload.Length > MaxPayloadSize)
                throw new ArgumentException( "Payload null or exceeding max size of " + MaxPayloadSize );

            // Construct message
            SendMessage rm = new SendMessage();
            rm.m_Id       = packetId;
            rm.m_Payload  = payload;
            rm.m_Sequence = m_ReliableDataMT.m_Newest++;

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
                    m_Recipient.Node.RaiseOnMessage( msg.m_Id, msg.m_Payload, msg.m_Recipient, m_Channel );
                }
            }
        }

        internal void FlushST()
        {
            // RID(1) | ChannelID(1) | Sequence(4) | MsgLen(2) | Msg(variable)

            int numMessagesAdded = 0;
            lock (m_ReliableDataMT.m_Messages)
            {
                if (m_ReliableDataMT.m_Messages.Count == 0)
                    return;

                m_BinWriter.BaseStream.Position = 0;
                m_BinWriter.Write( RID );
                m_BinWriter.Write( m_Channel );

                // Only send sequence of first message, other sequences are consequative.
                m_BinWriter.Write( m_ReliableDataMT.m_Messages.Peek().m_Sequence );

                // Write each message with Length, ID & payload. The sequence is always the first +1 for each message.
                foreach (var msg in m_ReliableDataMT.m_Messages)
                {
                    Debug.Assert( msg.m_Payload.Length <= MaxPayloadSize );
                    m_BinWriter.Write( (ushort)msg.m_Payload.Length );
                    m_BinWriter.Write( msg.m_Id );
                    m_BinWriter.Write( msg.m_Payload );
                    // Avoid fragmentation and exceeding max recvBuffer size (65536).
                    if (m_BinWriter.BaseStream.Position > MaxFrameSize)
                        break;
                    ++numMessagesAdded;
                }
            }

            // If Payload is too big, we cannot send it. We ensure however at the point where a message is inserted,
            // that no such payload can be added. Assert this.
            Debug.Assert( numMessagesAdded!=0 );

            // Eventhough this is already in a send thread, do the actual send async to avoid keeping the lock longer than necessary.
            m_Recipient.UDPClient.SendAsync( m_MemWriteStream.GetBuffer(), (int)m_BinWriter.BaseStream.Position, m_Recipient.EndPoint );
        }

        internal void ReceiveDataWT( BinaryReader reader )
        {
            uint sequence = reader.ReadUInt32();
            if (sequence == m_ReliableDataRT.m_AckExpected) // Unexpected
            {
                while (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    ushort messageLen = reader.ReadUInt16();

                    // Make reliable received message
                    RecvMessage rm = new RecvMessage();
                    rm.m_Channel = m_Channel;
                    rm.m_Id      = reader.ReadByte();
                    rm.m_Payload = reader.ReadBytes( messageLen );
                    rm.m_Recipient = m_Recipient.EndPoint;

                    // Add it thread safely
                    lock (m_ReliableDataRT.m_Messages)
                    {
                        m_ReliableDataRT.m_Messages.Enqueue( rm );
                    }

                    sequence += 1;
                }
                m_ReliableDataRT.m_AckExpected = sequence;
            }
            // Always send ack. Ack may have been lost previously. Keep sending this until transmitter knows it was delivered.
            FlushAckWT();
        }

        void FlushAckWT()
        {
            m_BinWriter.BaseStream.Position = 0;
            m_BinWriter.Write( RACK );
            m_BinWriter.Write( m_Channel );
            m_BinWriter.Write( m_ReliableDataRT.m_AckExpected-1 ); // Ack yields the new value to expect, so Ack-1 is the last one received.
            m_Recipient.UDPClient.SendAsync( m_MemWriteStream.GetBuffer(), (int)m_BinWriter.BaseStream.Position, m_Recipient.EndPoint );
        }

        internal void ReceiveAckWT( BinaryReader reader )
        {
            uint ack = reader.ReadUInt32();
            if (IsSequenceNewer( ack, m_ReliableDataRT.m_AckReceived ))
            {
                int numPacketsToDrop = (int)(ack - m_ReliableDataRT.m_AckReceived) + 1;
                lock (m_ReliableDataMT.m_Messages)
                {
                    Debug.Assert( m_ReliableDataMT.m_Messages.Count >= numPacketsToDrop );
                    while (numPacketsToDrop!=0)
                    {
                        m_ReliableDataMT.m_Messages.Dequeue();
                        numPacketsToDrop--;
                    }
                }
                m_ReliableDataRT.m_AckReceived = ack+1;
            }
        }

        static bool IsSequenceNewer( uint incoming, uint having )
        {
            return (incoming - having) < (uint.MaxValue>>1);
        }
    }
}
