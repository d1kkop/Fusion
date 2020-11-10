using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;

namespace Fusion
{
    public class UnreliableStream
    {
        public const byte URID   = 2;
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
            internal Queue<RecvMessage> m_Messages = new Queue<RecvMessage>();
        }

        DataMT m_UnreliableDataMT;
        DataRT m_UnreliableDataRT;

        internal Recipient Recipient { get; }

        internal UnreliableStream( Recipient recipient )
        {
            Recipient = recipient;
            m_UnreliableDataMT = new DataMT();
            m_UnreliableDataRT = new DataRT();
        }

        internal void AddMessage( byte packetId, byte[] payload )
        {
            if ( payload != null && payload.Length > MaxPayloadSize)
                throw new ArgumentException( "Payload null or exceeding max size of " + MaxPayloadSize );

            // Construct message
            SendMessage rm = new SendMessage();
            rm.m_Id       = packetId;
            rm.m_Payload  = payload;
            rm.m_Sequence = m_UnreliableDataMT.m_Newest++;

            // Add to list of messages thread safely
            lock (m_UnreliableDataMT.m_Messages)
            {
                m_UnreliableDataMT.m_Messages.Enqueue( rm );
            }
        }

        internal void Sync()
        {
            lock(m_UnreliableDataRT.m_Messages)
            {
                while (m_UnreliableDataRT.m_Messages.Count !=0)
                {
                    var msg = m_UnreliableDataRT.m_Messages.Dequeue();
                    Recipient.Node.RaiseOnMessage( msg.m_Id, msg.m_Payload, msg.m_Recipient, 0 );
                }
            }
        }

        internal void FlushST( BinaryWriter binWriter )
        {
            // URID(1) | Sequence(4) | MsgLen(2) | Msg(variable)

            int numMessagesAdded = 0;
            lock (m_UnreliableDataMT.m_Messages)
            {
                if (m_UnreliableDataMT.m_Messages.Count == 0)
                    return;

                binWriter.BaseStream.Position = 0;
                binWriter.Write( URID );

                // Only send sequence of first message, other sequences are consequative.
                binWriter.Write( m_UnreliableDataMT.m_Messages.Peek().m_Sequence );

                // Write each message with Length, ID & payload. The sequence is always the first +1 for each message.
                while ( m_UnreliableDataMT.m_Messages.Count != 0 )
                {
                    var msg = m_UnreliableDataMT.m_Messages.Dequeue();
                    Debug.Assert( msg.m_Payload.Length <= MaxPayloadSize );
                    binWriter.Write( (ushort)msg.m_Payload.Length );
                    binWriter.Write( msg.m_Id );
                    binWriter.Write( msg.m_Payload );
                    // Avoid fragmentation and exceeding max recvBuffer size (65536).
                    if (binWriter.BaseStream.Position > MaxFrameSize)
                        break;
                    ++numMessagesAdded;
                }
            }

            // If Payload is too big, we cannot send it. We ensure however at the point where a message is inserted,
            // that no such payload can be added. Assert this.
            Debug.Assert( numMessagesAdded!=0 );

            // Eventhough this is already in a send thread, do the actual send async to avoid keeping the lock longer than necessary.
            Recipient.UDPClient.SendSafe( binWriter.GetData(), (int)binWriter.BaseStream.Position, Recipient.EndPoint );
        }

        internal void ReceiveDataWT( BinaryReader reader )
        {
            uint sequence = reader.ReadUInt32();
            if ( IsSequenceNewer(sequence, m_UnreliableDataRT.m_Expected) ) 
            {
                while (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    ushort messageLen = reader.ReadUInt16();

                    // Make reliable received message
                    RecvMessage rm = new RecvMessage();
                    rm.m_Id        = reader.ReadByte();
                    rm.m_Payload   = reader.ReadBytes( messageLen );
                    rm.m_Recipient = Recipient.EndPoint;

                    // Add it thread safely
                    lock (m_UnreliableDataRT.m_Messages)
                    {
                        m_UnreliableDataRT.m_Messages.Enqueue( rm );
                    }

                    sequence += 1;
                }
                m_UnreliableDataRT.m_Expected = sequence;
            }
        }

        static bool IsSequenceNewer( uint incoming, uint having )
        {
            return (incoming - having) < (uint.MaxValue>>1);
        }
    }
}
