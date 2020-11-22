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
        UnreliableStream m_UnreliableStream;
        Dictionary<byte, ReliableStream> m_ReliableStreams;

        internal Node Node { get; }
        internal IPEndPoint EndPoint { get; }
        internal UdpClient UDPClient { get; }

        internal Recipient( Node node, IPEndPoint endpoint, UdpClient recipient )
        {
            Node      = node;
            EndPoint  = endpoint;
            UDPClient = recipient;
            m_UnreliableStream = new UnreliableStream( this );
            m_ReliableStreams  = new Dictionary<byte, ReliableStream>();
        }

        public void Dispose()
        {
            Dispose( true );
            GC.SuppressFinalize( this );
        }

        protected virtual void Dispose(bool disposing)
        {
            if (m_Disposed)
                return;
            if ( disposing )
            {
                // Do NOT dispose udp client as this is owned by the listener. Recipients only borrow it.
            }
            m_Disposed = true;
        }

        internal void Sync()
        {
            lock(m_ReliableStreams)
            {
                foreach( var kvp in m_ReliableStreams)
                {
                    ReliableStream stream = kvp.Value;
                    stream.Sync();
                }
            }
            m_UnreliableStream.Sync();
        }

        internal virtual void PrepareSend( BinaryWriter writer, StreamId streamId )
        {
            writer.BaseStream.Position = 0;
            writer.Write( (byte)streamId );
        }

        internal virtual void Send( byte id, byte[] data, byte channel, SendMethod sendMethod, DeliveryTrace trace )
        {
            switch ( sendMethod )
            {
                case SendMethod.Reliable:
                {
                    ReliableStream stream;
                    lock (m_ReliableStreams)
                    {
                        if (!m_ReliableStreams.TryGetValue( channel, out stream ))
                        {
                            stream = new ReliableStream( this, channel );
                            m_ReliableStreams.Add( channel, stream );
                        }
                    }
                    stream.AddMessage( id, data, trace );
                }
                break;

                case SendMethod.Unreliable:
                {
                    m_UnreliableStream.AddMessage( id, data );
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
                    lock (m_ReliableStreams)
                    {
                        if (!m_ReliableStreams.TryGetValue( channel, out stream ))
                        {
                            stream = new ReliableStream( this, channel );
                            m_ReliableStreams.Add( channel, stream );
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
                    m_UnreliableStream.ReceiveDataWT( reader, writer );
                }
                break;

                default:
                Debug.WriteLine( "Unknown stream id detected, data ignored." );
                break;
            }
        }

        internal virtual void FlushDataST( BinaryWriter writer )
        {
            lock(m_ReliableStreams)
            {
                foreach( var kvp in m_ReliableStreams)
                {
                    ReliableStream stream = kvp.Value;
                    stream.FlushST( writer );
                }
            }
            m_UnreliableStream.FlushST( writer );
        }

        virtual internal void ReceiveSystemMessageWT( BinaryReader reader, BinaryWriter writer, byte id, IPEndPoint endpoint, byte channel )
        {
            // Silently, ignore irrelevant packages
            Debug.Assert( id < (byte)SystemPacketId.Count );
        }
    }
}
