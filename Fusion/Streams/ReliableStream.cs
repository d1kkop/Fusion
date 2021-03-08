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
        internal DeliveryTrace m_NextTrace; // In case of fragmented packet, this points to the next fragment.
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
        const int MaxFrameSize = 1400; // Ethernet frame is max 1500. Reduce 100 for overhead in other layers. Actual max is slightly higher due to overhead.

        enum FragmentState
        {
            NoData = 0, /*This indicates that data was already sent and received (acked). In which case sequential transmits write a 0 byte. */
            NotFragmented,
            Begin,
            Intermediate,
            End
        }

        class SendMessage
        {
            internal FragmentState m_FragmentState;
            internal uint m_Sequence;
            internal byte m_Id;
            internal ArraySegment<byte> m_Payload;
            internal DeliveryTrace m_Trace;
            internal bool m_IsAcked;
        }

        class RecvMessage
        {
            internal FragmentState m_FragmentState;
            internal IMessage m_ExternalMsg; // In case this msg is not handled in ReliableStream but externally.
            internal byte m_Channel;
            internal byte m_Id;
            internal byte [] m_Payload;
            internal IPEndPoint m_Recipient;
        }

        class SendData // MainThread data
        {
            internal uint m_Newest;
            internal List<SendMessage> m_Messages = new List<SendMessage>();
        }

        class RecvData // ReceiveThread data
        {
            internal uint m_Expected;
            internal Dictionary<uint, RecvMessage> m_PendingMessages = new Dictionary<uint, RecvMessage>();
            internal List<RecvMessage> m_FinalMessages = new List<RecvMessage>();
        }


        SendData m_ReliableDataSend;
        RecvData m_ReliableDataRecv;
        byte [] m_EmptyByteArray = new byte[0];

        internal Recipient Recipient { get; }
        internal byte Channel { get; }

        internal ReliableStream( Recipient recipient, byte channel )
        {
            Recipient = recipient;
            Channel   = channel;
            m_ReliableDataSend = new SendData();
            m_ReliableDataRecv = new RecvData();
        }

        internal void AddMessage( byte packetId, byte [] payload, DeliveryTrace trace )
        {
            if (payload != null && payload.Length > MaxFrameSize)
                throw new ArgumentException( "Payload null or exceeding max size of " + MaxFrameSize );

            if (payload != null && payload.Length > MaxFrameSize)
            {
                // This packet must be fragmented.
                int sizeLeft = payload.Length;
                int offset   = 0;
                bool isFirst = true;
                DeliveryTrace fragmentTrace = trace;
                while( sizeLeft != 0 )
                {
                    SendMessage rm = new SendMessage();
                    ArraySegment<byte> fragment;

                    if ( sizeLeft > MaxFrameSize )
                    {
                        fragment = new ArraySegment<byte>(payload, offset, MaxFrameSize);
                        sizeLeft -= MaxFrameSize;
                        rm.m_FragmentState = (isFirst ? FragmentState.Begin : FragmentState.Intermediate);
                        isFirst=false;
                    }
                    else
                    {
                        fragment = new ArraySegment<byte>( payload, offset, sizeLeft );
                        sizeLeft -= sizeLeft;
                        rm.m_FragmentState = FragmentState.End;
                    }
 
                    rm.m_Id        = packetId;
                    rm.m_Payload   = fragment;
                    rm.m_Trace     = trace;
                    rm.m_Sequence  = m_ReliableDataSend.m_Newest++;

                    if ( trace != null )
                    {
                        lock( trace ) // Lock parent trace (provided by user).
                        {
                            fragmentTrace.m_Targets.Add( Recipient ); // Add ourself to be waited for completion.
                            // If not is last fragment, create new fragmentTrace or go to next (if was already created by other ReliableStream).
                            if ( rm.m_FragmentState != FragmentState.End && fragmentTrace.m_NextTrace == null )
                            {
                                fragmentTrace.m_NextTrace = new DeliveryTrace();
                            }
                            fragmentTrace = fragmentTrace.m_NextTrace;
                        }
                    }

                    // Add to list of messages thread safely
                    lock (m_ReliableDataSend.m_Messages)
                    {
                        m_ReliableDataSend.m_Messages.Add( rm );
                    }
                }
            }
            else
            {
                // Construct message
                SendMessage rm = new SendMessage();
                rm.m_FragmentState  = FragmentState.NotFragmented;
                rm.m_Id             = packetId;
                rm.m_Payload        = payload;
                rm.m_Trace          = trace;
                rm.m_Sequence       = m_ReliableDataSend.m_Newest++;

                if (trace != null)
                {
                    lock (trace) // Need lock because on local machine, we may receive in a different thread on a recipient in this trace already.
                    {
                        trace.m_Targets.Add( Recipient );
                    }
                }

                // Add to list of messages thread safely
                lock (m_ReliableDataSend.m_Messages)
                {
                    m_ReliableDataSend.m_Messages.Add( rm );
                }
            }
        }

        internal void Sync()
        {
            lock (m_ReliableDataRecv.m_FinalMessages)
            {
                while ( m_ReliableDataRecv.m_FinalMessages.Count !=0 )
                {
                    var msg = m_ReliableDataRecv.m_FinalMessages.Dequeue();
                    if (msg.m_ExternalMsg != null)
                    {
                        msg.m_ExternalMsg.Process();
                    }
                    else
                    {
                        Recipient.Node.RaiseOnMessage( msg.m_Id, msg.m_Payload, msg.m_Recipient, Channel );
                    }
                }
            }
        }

        internal void FlushST( BinaryWriter binWriter )
        {
            // RID(1) | ChannelID(1) | Sequence(4) | MsgLen(2) | Msg(variable)

            int numMessagesAdded = 0;
            lock (m_ReliableDataSend.m_Messages)
            {
                if (m_ReliableDataSend.m_Messages.Count == 0)
                    return;

                Recipient.PrepareSend( binWriter, StreamId.RID );
                binWriter.Write( Channel );

                // Only send sequence of first message, other sequences are consequative.
                // If in between messages are already acked, the msg.IsAcked is set true and as such the data can be skipped.
                binWriter.Write( m_ReliableDataSend.m_Messages[0].m_Sequence );

                // Write each message with Length, ID & payload. The sequence is always the first +1 for each message.
                foreach (var msg in m_ReliableDataSend.m_Messages)
                {
                    Debug.Assert( msg.m_Sequence - numMessagesAdded == m_ReliableDataSend.m_Messages[0].m_Sequence );

                    // Avoid UDP-builtin-fragmentation and exceeding max recvBuffer size (default=65536).
                    ushort msgLen = msg.m_Payload != null ? (ushort)msg.m_Payload.Count : (ushort)0;
                    Debug.Assert( msgLen <= MaxFrameSize );

                    // Discarded added bytes for overhead in this check. We have already reserved 100 bytes for additional overhead in other layers.
                    if (binWriter.BaseStream.Position+msgLen > MaxFrameSize ||
                        numMessagesAdded > byte.MaxValue /* Max num messages is 255, as we can only sent back that num of acks. */
                        )
                    {
                        break;
                    }

                    if (!msg.m_IsAcked)
                    {
                        binWriter.Write( (byte)msg.m_FragmentState );   // Is Data appended
                        binWriter.Write( msg.m_Id );                    // DataId
                        if (msg.m_Payload != null && msg.m_Payload.Count != 0)
                        {
                            
                            binWriter.Write( msgLen );              // DataLen
                            binWriter.Write( msg.m_Payload );       // Actual Data
                        }
                        else
                        {
                            binWriter.Write( (ushort)0 );           // Data is not appended
                        }
                    }
                    else
                    {
                        // This message was already acked, so write 'true' so that it can be skipped on the recipient side.
                        binWriter.Write( true );
                    }

                    ++numMessagesAdded;
                }
            }

            // If Payload is too big, we cannot send it. We ensure however at the point where a message is inserted,
            // that no such payload can be added. Assert this.
            Debug.Assert( numMessagesAdded > 0 );

            Debug.Assert( binWriter.BaseStream.Position < MaxFrameSize+50 );

            // Eventhough this is already in a send thread, do the actual send async to avoid keeping the lock longer than necessary.
            Recipient.UDPClient.SendSafe( binWriter.GetData(), Recipient.EndPoint );
        }

        // Called optionally from RecvThreat (WT) to insert message in reliable stream to ensure correct ordering.
        internal void AddRecvMessageWT( IMessage msg )
        {
            Debug.Assert( msg!=null );
            // Make reliable received message
            RecvMessage rm = new RecvMessage();
            rm.m_ExternalMsg = msg;
            rm.m_Channel   = Channel;
            // Add it thread safely
            lock (m_ReliableDataRecv.m_FinalMessages)
            {
                m_ReliableDataRecv.m_FinalMessages.Enqueue( rm );
            }
        }

        internal void ReceiveDataWT( BinaryReader reader, BinaryWriter writer )
        {
            uint sequence         = reader.ReadUInt32();
            uint firstSeq         = sequence;
            bool allMessagesOlder = !IsSequenceNewer(sequence, m_ReliableDataRecv.m_Expected);
            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                long oldPosition        = reader.BaseStream.Position;
                FragmentState fragState = (FragmentState) reader.ReadByte();
                uint bytesToNext = 1;

                if (fragState != FragmentState.NoData)
                {
                    byte   id         = reader.ReadByte();
                    ushort messageLen = reader.ReadUInt16();
                    bytesToNext       = messageLen + 4u; // uint16, id & skip bool

                    if (!allMessagesOlder && // All messages are older, only need to know how many messages are appended for acking.
                        IsSequenceNewer(sequence, m_ReliableDataRecv.m_Expected) &&
                        !m_ReliableDataRecv.m_PendingMessages.ContainsKey( sequence )) // Already have it buffered, skip to next.
                    {
                        // Make reliable received message
                        RecvMessage rm = new RecvMessage();
                        rm.m_FragmentState = fragState;
                        rm.m_Channel   = Channel;
                        rm.m_Id        = id;
                        rm.m_Payload   = messageLen != 0 ? reader.ReadBytes( messageLen ) : null;
                        rm.m_Recipient = Recipient.EndPoint;
                        m_ReliableDataRecv.m_PendingMessages.Add( sequence, rm );
                    }
                }

                sequence++;

                // Move reader position to next valid read position.
                reader.BaseStream.Position = oldPosition + bytesToNext;
            }

            // Regardless of whether the data was previously received or was just received as new, ack back the reception of this data.
            uint numAcks = sequence - firstSeq;
            Debug.Assert( numAcks <= byte.MaxValue ); // The sending side much ensure that no more than 255 messages inside a datagram are transmitted.
            FlushAckWT( writer, firstSeq, (byte)numAcks );

            // Try to process as many reliable packets as possible.
            while ( m_ReliableDataRecv.m_PendingMessages.Count != 0 &&
                    m_ReliableDataRecv.m_PendingMessages.TryGetValue( m_ReliableDataRecv.m_Expected, out RecvMessage msg ) )
            {
                Debug.Assert( msg.m_FragmentState != FragmentState.NoData );
                uint nextSequence = m_ReliableDataRecv.m_Expected+1;

                // If packet is part of a fragment.
                if ( msg.m_FragmentState != FragmentState.NotFragmented )
                {
                    msg = ReassembleFragmentedPacketWT( msg.m_Payload.Length, out nextSequence );
                    if (msg == null)
                        return;
                }
                
                // Remove pending (possible reassembled fragments).
                while (m_ReliableDataRecv.m_Expected != nextSequence)
                {
                    m_ReliableDataRecv.m_PendingMessages.Remove( m_ReliableDataRecv.m_Expected );
                    m_ReliableDataRecv.m_Expected++;
                }

                // Peek if message is system message. If so, handle system messages directly in the worker thread.
                // However, do NOT spawn new async task as that could invalidate the reliability order.
                if ( Channel == SystemChannel || msg.m_Id == (byte)SystemPacketId.RPC )
                {
                    Debug.Assert( msg.m_Id < (byte)SystemPacketId.Count || msg.m_Id == (byte)SystemPacketId.RPC );
                    using (MemoryStream ms = new MemoryStream( msg.m_Payload ?? m_EmptyByteArray ))
                    using (reader = new BinaryReader( ms ))
                    {
                        Recipient.ReceiveSystemMessageWT( false, reader, writer, (SystemPacketId)msg.m_Id, Recipient.EndPoint, Channel );
                    }
                }
                else
                {
                    // Add it thread safely
                    lock (m_ReliableDataRecv.m_FinalMessages)
                    {
                        m_ReliableDataRecv.m_FinalMessages.Add( msg );
                    }
                }
            }
        }

        RecvMessage ReassembleFragmentedPacketWT( int firstFragmentSize, out uint nextNewSequence )
        {
            RecvMessage msg;
            nextNewSequence      = 0;
            int totalPayloadSize = firstFragmentSize;
            uint lastSequence    = m_ReliableDataRecv.m_Expected+1; // Can skip first, already know size of that one.
            for (; m_ReliableDataRecv.m_PendingMessages.TryGetValue( lastSequence, out msg ) &&
                   msg.m_FragmentState != FragmentState.End;
                   lastSequence++)
            {
                totalPayloadSize += msg.m_Payload.Length;
            }

            // Missing intermediate fragment.
            if (msg.m_FragmentState != FragmentState.End)
                return null;

            RecvMessage unfragmentedMessage     = new RecvMessage();
            unfragmentedMessage.m_Payload       = new byte[totalPayloadSize];
            unfragmentedMessage.m_FragmentState = FragmentState.NotFragmented;
            unfragmentedMessage.m_Channel       = msg.m_Channel;
            unfragmentedMessage.m_Id            = msg.m_Id;
            unfragmentedMessage.m_Recipient     = msg.m_Recipient;

            int offset = 0;
            for ( uint sequence = m_ReliableDataRecv.m_Expected; sequence != lastSequence+1; sequence++ )
            {
                msg = m_ReliableDataRecv.m_PendingMessages[sequence];
                msg.m_Payload.CopyTo( unfragmentedMessage.m_Payload, offset );
                offset += msg.m_Payload.Length;
#if DEBUG
                if (sequence == m_ReliableDataRecv.m_Expected) 
                    Debug.Assert( msg.m_FragmentState == FragmentState.Begin );
                else if (sequence == lastSequence)
                    Debug.Assert( msg.m_FragmentState == FragmentState.End );
                else
                    Debug.Assert( msg.m_FragmentState == FragmentState.Intermediate );
#endif
            }

            nextNewSequence = lastSequence+1;
            return unfragmentedMessage;
        }

        void FlushAckWT( BinaryWriter binWriter, uint firstSequence, byte numAcks )
        {
            Debug.Assert( numAcks <= byte.MaxValue );
            Recipient.PrepareSend( binWriter, StreamId.RACK );
            binWriter.Write( Channel );
            binWriter.Write( firstSequence );
            binWriter.Write( numAcks );
            Recipient.UDPClient.SendSafe( binWriter.GetData(), Recipient.EndPoint );
        }

        internal void ReceiveAckWT( BinaryReader reader )
        {
            uint ack = reader.ReadUInt32();
            byte num = reader.ReadByte();
            Debug.Assert( num>=1 );

            // NOTE: In between packets may be acked.
            lock (m_ReliableDataSend.m_Messages)
            {
                // Early out
                if (m_ReliableDataSend.m_Messages.Count == 0 ||
                    !IsSequenceNewer( ack, m_ReliableDataSend.m_Messages[0].m_Sequence ))
                {
                    return;
                }

                for (int i = 0; i < m_ReliableDataSend.m_Messages.Count; i++)
                {
                    if (m_ReliableDataSend.m_Messages[i].m_Sequence == ack)
                    {
                        for (; (num != 0) && (i != m_ReliableDataSend.m_Messages.Count); num--)
                        {
                            m_ReliableDataSend.m_Messages[i++].m_IsAcked = true; // No longer transmit this message.
                        }
                        break;
                    }
                }

                // While messages in front are acked, we can remove them. However if a packet is not yet acked, we cannot
                // remove the in between packets because we only send a sequence for the first message and only a 
                // flag for whether the message is actually appended or not. This way we safe on bandwith.
                while (m_ReliableDataSend.m_Messages.Count != 0 && m_ReliableDataSend.m_Messages[0].m_IsAcked)
                {
                    SendMessage sm = m_ReliableDataSend.m_Messages[0];
                    // Notify the trace that this message was delivered, if any.
                    if (sm.m_Trace != null)
                    {
                        lock (sm.m_Trace)
                        {
                            sm.m_Trace.m_Delivered.Add( Recipient );
                            Monitor.PulseAll( sm.m_Trace );
                        }
                    }
                    m_ReliableDataSend.m_Messages.RemoveAt( 0 );
                }
            }
        }

        // Correct with wrap around.
        static bool IsSequenceNewer( uint incoming, uint having )
        {
            return (incoming - having) < (uint.MaxValue>>1);
        }
    }
}
