using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;

namespace Fusion
{
    public class UnreliableStream
    {
        // NOTE: Actual max send size is a little above 1400 due to overhead in unreliable message.
        const int MaxFrameSize = 1400; // Ethernet frame is max 1500. Reduce 100 for overhead in other layers.

        protected class SendMessage
        {
            internal uint m_Sequence;
            internal byte m_Id;
            internal bool m_IsSystem;
            internal byte [] m_Payload;
        }

        protected class RecvMessage
        {
            internal byte m_Id;
            internal byte [] m_Payload;
            internal IPEndPoint m_Recipient;
        }

        protected class DataMT // MainThread data
        {
            internal uint m_Newest;
            internal Queue<SendMessage> m_Messages = new Queue<SendMessage>();
        }

        protected class DataRT // ReceiveThread data
        {
            internal uint m_Expected;
            internal Queue<RecvMessage> m_Messages = new Queue<RecvMessage>();
        }

        protected DataMT m_UnreliableDataMT;
        protected DataRT m_UnreliableDataRT;

        internal Recipient Recipient { get; }

        internal UnreliableStream( Recipient recipient )
        {
            Recipient = recipient;
            m_UnreliableDataMT = new DataMT();
            m_UnreliableDataRT = new DataRT();
        }

        internal void AddMessage( byte packetId, bool isSystem, byte [] payload )
        {
            if (payload != null && payload.Length > MaxFrameSize)
                throw new ArgumentException( $"Payload too big ( >{MaxFrameSize} ) for unreliable data. Fragmentation required. Use reliable." );

            // Construct message
            SendMessage rm = new SendMessage();
            rm.m_Id       = packetId;
            rm.m_IsSystem = isSystem;
            rm.m_Payload  = payload;        // It is ok to copy a reference. The data is pre-copied and shared between multiple streams.
            rm.m_Sequence = m_UnreliableDataMT.m_Newest++;

            // Add to list of messages thread safely
            lock (m_UnreliableDataMT.m_Messages)
            {
                m_UnreliableDataMT.m_Messages.Enqueue( rm );
            }
        }

        internal void Sync()
        {
            lock (m_UnreliableDataRT.m_Messages)
            {
                while (m_UnreliableDataRT.m_Messages.Count !=0)
                {
                    var msg = m_UnreliableDataRT.m_Messages.Dequeue();
                    Recipient.Node.RaiseOnMessage( msg.m_Id, msg.m_Payload, msg.m_Recipient, 0 );
                }
            }
        }

        internal virtual StreamId GetStreamId()
        {
            return StreamId.UID;
        }

        internal virtual void FlushST( BinaryWriter binWriter )
        {
            // URID(1) | Sequence(4) | MsgLen(2) | Msg(variable)

            int numMessagesAdded = 0;
            lock (m_UnreliableDataMT.m_Messages)
            {
                if (m_UnreliableDataMT.m_Messages.Count == 0)
                    return;

                Recipient.PrepareSend( binWriter, GetStreamId() );

                // Only send sequence of first message, other sequences are consequative.
                binWriter.Write( m_UnreliableDataMT.m_Messages.Peek().m_Sequence );

                // Write each message with Length, ID & payload. The sequence is always the first +1 for each message.
                while (m_UnreliableDataMT.m_Messages.Count != 0)
                {
                    byte [] payload = m_UnreliableDataMT.m_Messages.Peek().m_Payload;

                    // Avoid fragmentation and exceeding max recvBuffer size (65536).
                    if (binWriter.BaseStream.Position + (payload!=null ? payload.Length : 0) > MaxFrameSize )
                    {
                        break;
                    }

                    var msg = m_UnreliableDataMT.m_Messages.Dequeue();
                    Debug.Assert( msg.m_Payload.Length <= MaxFrameSize );
                    binWriter.Write( msg.m_Id );
                    binWriter.Write( msg.m_IsSystem );
                    if (msg.m_Payload != null)
                    {
                        binWriter.Write( (ushort)msg.m_Payload.Length );
                        binWriter.Write( msg.m_Payload );
                    }
                    else
                    {
                        binWriter.Write( (ushort)0 );
                    }

                    ++numMessagesAdded;
                }
            }

            // If Payload is too big, we cannot send it. We ensure however at the point where a message is inserted,
            // that no such payload can be added. Assert this.
            Debug.Assert( numMessagesAdded > 0 );

            Debug.Assert( binWriter.BaseStream.Position < MaxFrameSize + 50 );

            // Eventhough this is already in a send thread, do the actual send async to avoid keeping the lock longer than necessary.
            Recipient.UDPClient.SendSafe( binWriter.GetData(), Recipient.EndPoint );
        }

        internal virtual void ReceiveDataWT( BinaryReader reader, BinaryWriter writer )
        {
            uint sequence = reader.ReadUInt32();
            if (IsSequenceNewer( sequence, m_UnreliableDataRT.m_Expected ))
            {
                while (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    SystemPacketId id = (SystemPacketId)reader.ReadByte();
                    bool isSystem     = reader.ReadBoolean();
                    ushort messageLen = reader.ReadUInt16();
                    long preMessagePosition = reader.BaseStream.Position;

                    if ( isSystem )
                    {
                        Recipient.ReceiveSystemMessageWT( false, reader, writer, id, Recipient.EndPoint, ReliableStream.SystemChannel );
                    }
                    else
                    {
                        // Make reliable received message
                        RecvMessage rm = new RecvMessage();
                        rm.m_Id        = (byte)id;
                        rm.m_Payload   = messageLen != 0 ? reader.ReadBytes( messageLen ) : null;
                        rm.m_Recipient = Recipient.EndPoint;

                        // Add it thread safely
                        lock (m_UnreliableDataRT.m_Messages)
                        {
                            m_UnreliableDataRT.m_Messages.Enqueue( rm );
                        }
                    }

                    // Move sequence up one, discarding old data
                    sequence += 1;

                    // Regardless of what has been read, move to next message (if any).
                    reader.BaseStream.Position = preMessagePosition + messageLen;
                }
                m_UnreliableDataRT.m_Expected = sequence;
            }
        }

        public static bool IsSequenceNewer( uint incoming, uint having )
        {
            return (incoming - having) < (uint.MaxValue>>1);
        }
    }
}
