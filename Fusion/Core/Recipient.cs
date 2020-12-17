using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace Fusion
{
    public class Recipient : IDisposable
    {
        bool m_Disposed;

        internal Node Node { get; }
        internal IPEndPoint EndPoint { get; }
        internal UdpClient UDPClient { get; }
        internal UnreliableStream UnreliableStream { get; }
        internal Dictionary<byte, ReliableStream> ReliableStreams { get; }

        internal Recipient( Node node, IPEndPoint endpoint, UdpClient recipient )
        {
            Node      = node;
            EndPoint  = endpoint;
            UDPClient = recipient;
            UnreliableStream = new UnreliableStream( this );
            ReliableStreams  = new Dictionary<byte, ReliableStream>();
        }

        public void Dispose()
        {
            Dispose( true );
            GC.SuppressFinalize( this );
        }

        protected virtual void Dispose( bool disposing )
        {
            if (m_Disposed)
                return;
            if (disposing)
            {
                // Do NOT dispose udp client as this is owned by the listener. Recipients only borrow it.
            }
            m_Disposed = true;
        }

        internal void Sync()
        {
            lock (ReliableStreams)
            {
                foreach (var kvp in ReliableStreams)
                {
                    ReliableStream stream = kvp.Value;
                    stream.Sync();
                }
            }
            UnreliableStream.Sync();
        }

        internal virtual void PrepareSend( BinaryWriter writer, StreamId streamId )
        {
            writer.BaseStream.Position = 0;
            writer.Write( (byte)streamId );
        }

        internal virtual void Send( byte id, byte [] data, byte channel, bool isSystem, SendMethod sendMethod, DeliveryTrace trace )
        {
            switch (sendMethod)
            {
                case SendMethod.Reliable:
                {
                    ReliableStream stream;
                    lock (ReliableStreams)
                    {
                        if (!ReliableStreams.TryGetValue( channel, out stream ))
                        {
                            stream = new ReliableStream( this, channel );
                            ReliableStreams.Add( channel, stream );
                        }
                    }
                    stream.AddMessage( id, data, trace );
                }
                break;

                case SendMethod.Unreliable:
                {
                    UnreliableStream.AddMessage( id, isSystem, data );
                }
                break;

                default:
                Debug.Assert( false, "Invalid send method." );
                break;
            }

        }

        internal virtual void ReceiveDataWT( BinaryReader reader, BinaryWriter writer )
        {
            StreamId streamId = (StreamId)reader.ReadByte();
            switch (streamId)
            {
                case StreamId.RID:
                case StreamId.RACK:
                {
                    byte channel = reader.ReadByte();
                    ReliableStream stream;
                    lock (ReliableStreams)
                    {
                        if (!ReliableStreams.TryGetValue( channel, out stream ))
                        {
                            stream = new ReliableStream( this, channel );
                            ReliableStreams.Add( channel, stream );
                        }
                    }
                    if (streamId == StreamId.RID)
                        stream.ReceiveDataWT( reader, writer );
                    else
                        stream.ReceiveAckWT( reader );
                }
                break;

                case StreamId.UID:
                {
                    UnreliableStream.ReceiveDataWT( reader, writer );
                }
                break;

                default:
                Debug.WriteLine( "Unknown stream id detected, data ignored." );
                break;
            }
        }

        internal virtual void FlushDataST( BinaryWriter writer )
        {
            lock (ReliableStreams)
            {
                foreach (var kvp in ReliableStreams)
                {
                    ReliableStream stream = kvp.Value;
                    stream.FlushST( writer );
                }
            }
            UnreliableStream.FlushST( writer );
        }

        virtual internal void ReceiveSystemMessageWT( bool isReliableMsg, BinaryReader reader, BinaryWriter writer, SystemPacketId id, IPEndPoint endpoint, byte channel )
        {
        }
    }
}
