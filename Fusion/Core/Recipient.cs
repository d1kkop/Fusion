using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace Fusion
{
    public class Recipient
    {
        UnreliableStream m_UnreliableStream;
        Dictionary<byte, ReliableStream> m_ReliableStreams = new Dictionary<byte, ReliableStream>();

        internal Node Node { get; }
        internal IPEndPoint EndPoint { get; }
        internal UdpClient UDPClient { get; }

        public Recipient( Node node, IPEndPoint endpoint, UdpClient recipient )
        {
            Node      = node;
            EndPoint  = endpoint;
            UDPClient = recipient;
            m_UnreliableStream = new UnreliableStream( this );
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

        internal void Send( byte id, byte[] data, byte channel, SendMethod sendMethod )
        {
            switch ( sendMethod)
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
                    stream.AddMessage( id, data );
                }
                break;

                case SendMethod.Unreliable:
                {
                    m_UnreliableStream.AddMessage( id, data );
                }
                break;
            }

        }

        internal void ReceiveDataWT( BinaryReader reader )
        {
            byte streamId = reader.ReadByte();
            switch (streamId)
            {
                case ReliableStream.RID:
                case ReliableStream.RACK:
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
                    if (streamId == ReliableStream.RID)
                        stream.ReceiveDataWT( reader );
                    else
                        stream.ReceiveAckWT( reader );
                }
                break;

                case UnreliableStream.URID:
                {
                    m_UnreliableStream.ReceiveDataWT( reader );
                }
                break;

                default:
                Debug.WriteLine( "Unknown stream id detected, data ignored." );
                break;
            }
        }

        internal void FlushDataST()
        {
            lock(m_ReliableStreams)
            {
                foreach( var kvp in m_ReliableStreams)
                {
                    ReliableStream stream = kvp.Value;
                    stream.FlushST();
                }
            }
            m_UnreliableStream.FlushST();
        }
    }
}
