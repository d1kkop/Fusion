using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;

namespace Fusion
{
    public class DeliveryTrace
    {
        internal List<Recipient> m_Targets   = new List<Recipient>();
        internal List<Recipient> m_Delivered = new List<Recipient>();

        public bool PeekSpecific( Recipient recipient )
        {
            lock (this)
            {
                return m_Delivered.Contains( recipient );
            }
        }

        public bool PeekAll()
        {
            bool result = m_Targets.Count == m_Delivered.Count;
#if DEBUG
            if (result)
            {
                Debug.Assert( m_Targets.All( t => m_Delivered.Contains( t ) ) );
            }
#endif
            return result;
        }

        public void WaitSpecific( Recipient recipient )
        {
            bool result = WaitSpecific( recipient, 0 );
            Debug.Assert( result );
        }

        public bool WaitSpecific( Recipient recipient, int timeout )
        {
            lock (this)
            {
                if (!m_Targets.Contains( recipient ))
                    throw new InvalidOperationException( "Recipient is not in list of targets." );
                Stopwatch sw = new Stopwatch();
                while (!m_Delivered.Contains( recipient ))
                {
                    if (timeout <= 0)
                    {
                        Monitor.Wait( this );
                    }
                    else
                    {
                        if (!Monitor.Wait( this, timeout-(int)sw.ElapsedMilliseconds ))
                            return false;
                    }
                }
                return true;
            }
        }

        public void WaitAll()
        {
            bool result = WaitAll(0);
            Debug.Assert( result );
        }

        public bool WaitAll( int timeout )
        {
            if (PeekAll())
                return true;
            lock (this)
            {
                Stopwatch sw = new Stopwatch();
                while (!PeekAll())
                {
                    if (timeout <= 0)
                    {
                        Monitor.Wait( this );
                    }
                    else
                    {
                        if (!Monitor.Wait( this, timeout-(int)sw.ElapsedMilliseconds ))
                            return false;
                    }
                }
                return true;
            }
        }
    }

    public class ReliableStream
    {
        public const byte SystemChannel = 255;
        const int MaxFrameSize   = 1400; // Ethernet frame is max 1500. Reduce 100 for overhead in other layers.
        const int MaxPayloadSize = MaxFrameSize-10; // Reduce 10 from overhead of header.

        struct SendMessage
        {
            internal uint m_Sequence;
            internal byte m_Id;
            internal byte [] m_Payload;
            internal DeliveryTrace m_Trace;
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
        internal byte Channel { get; }

        internal ReliableStream( Recipient recipient, byte channel )
        {
            Recipient = recipient;
            Channel   = channel;
            m_ReliableDataMT = new DataMT();
            m_ReliableDataRT = new DataRT();
        }

        internal void AddMessage( byte packetId, byte[] payload, DeliveryTrace trace )
        {
            if (payload != null && payload.Length > MaxPayloadSize)
                throw new ArgumentException( "Payload null or exceeding max size of " + MaxPayloadSize );

            // Construct message
            SendMessage rm = new SendMessage();
            rm.m_Id        = packetId;
            rm.m_Payload   = payload;
            rm.m_Trace     = trace;
            rm.m_Sequence  = m_ReliableDataMT.m_Newest++;

            if (trace != null)
            {
                lock (trace) // Need lock because on local machine, we may receive in a different thread on a recipient in this trace already.
                {
                    trace.m_Targets.Add( Recipient );
                }
            }

            // Add to list of messages thread safely
            lock (m_ReliableDataMT.m_Messages)
            {
                m_ReliableDataMT.m_Messages.Enqueue( rm );
            }
        }

        internal void Sync()
        {
            lock (m_ReliableDataRT.m_Messages)
            {
                while (m_ReliableDataRT.m_Messages.Count !=0)
                {
                    var msg = m_ReliableDataRT.m_Messages.Dequeue();
                    Recipient.Node.RaiseOnMessage( msg.m_Id, msg.m_Payload, msg.m_Recipient, Channel );
                }
            }
        }

        internal void FlushST( BinaryWriter binWriter )
        {
            // RID(1) | ChannelID(1) | Sequence(4) | MsgLen(2) | Msg(variable)

            int numMessagesAdded = 0;
            lock (m_ReliableDataMT.m_Messages)
            {
                if (m_ReliableDataMT.m_Messages.Count == 0)
                    return;

                Recipient.PrepareSend( binWriter, StreamId.RID );
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
            Recipient.UDPClient.SendSafe( binWriter.GetData(), (int)binWriter.BaseStream.Position, Recipient.EndPoint );
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
                    if (Channel == ReliableStream.SystemChannel || (SystemPacketId)id == SystemPacketId.RPC)
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

                    // Move sequence up one, discarding old data.
                    sequence += 1;

                    // Move reader position to next valid read position in case the readback function did not read all data.
                    reader.BaseStream.Position = oldPosition + 1 + 2 + messageLen;
                }
                m_ReliableDataRT.m_Expected = sequence;
                FlushAckWT( writer );
            }
            else if (!IsSequenceNewer( sequence, m_ReliableDataRT.m_Expected ))
            {
                // Only retransmit ack here if incoming sequence is older. This helps prevent sending unsequenced acks back to the recipient.
                FlushAckWT( writer );
            }
        }

        void FlushAckWT( BinaryWriter binWriter )
        {
            Recipient.PrepareSend( binWriter, StreamId.RACK );
            binWriter.Write( Channel );
            binWriter.Write( m_ReliableDataRT.m_Expected-1 ); // Ack yields the new value to expect, so Ack-1 is the last one received.
            Recipient.UDPClient.SendSafe( binWriter.GetData(), (int)binWriter.BaseStream.Position, Recipient.EndPoint );
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
                    while (numPacketsToDrop!=0 &&
                           m_ReliableDataMT.m_Messages.Count!=0 /*<- In case data that is received after endpoint was removed does get through to this point, this fixes it.*/ )
                    {
                        SendMessage sm = m_ReliableDataMT.m_Messages.Dequeue();
                        if (sm.m_Trace != null)
                        {
                            lock (sm.m_Trace)
                            {
                                sm.m_Trace.m_Delivered.Add( Recipient );
                                Monitor.PulseAll( sm.m_Trace );
                            }
                        }
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
