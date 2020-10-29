using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace Fusion
{
    public class Recipient
    {
        Dictionary<byte, ReliableStream> m_ReliableStreams = new Dictionary<byte, ReliableStream>();

        internal Node Node { get; }
        internal IPEndPoint EndPoint { get; }
        internal UdpClient UDPClient { get; }

        public Recipient( Node node, IPEndPoint endpoint, UdpClient recipient )
        {
            Node      = node;
            EndPoint  = endpoint;
            UDPClient = recipient;
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
        }

        internal void Send( byte id, byte[] data, byte channel )
        {
            ReliableStream stream;
            lock(m_ReliableStreams)
            {
                if (!m_ReliableStreams.TryGetValue( channel, out stream ))
                {
                    stream = new ReliableStream( this, channel );
                    m_ReliableStreams.Add( channel, stream );
                }
            }
            stream.AddMessage( id, data );
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
        }
    }
}
