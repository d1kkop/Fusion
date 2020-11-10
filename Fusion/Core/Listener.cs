using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Xml.Serialization;

namespace Fusion
{
    // --- Message Structs ---------------------------------------------------------------------------------------------------

    class ReceptionError : IMessage
    {
        Node m_Node;
        int m_Error;

        public ReceptionError( Node node, int error )
        {
            m_Node  = node;
            m_Error = error;
        }
        public void Process()
        {
            m_Node.RaiseOnReceptionError( m_Error );
        }
    }

    public class Listener : IDisposable
    {
        internal Node Node { get; private set; }
        internal IPEndPoint LocalEndPoint { get; }
        internal UdpClient UDPClient { get; }
        internal int SimulatePacketLoss { get; set; }
        internal int CurrentWorkingThreadId { get; set; }
        internal BinaryWriter BinWriter { get; private set; }

        bool   m_Disposed;
        volatile bool m_IsClosing;
        Thread m_ListenThread;
        Random m_Random = new Random();

        internal Listener( Node node, UdpClient listener )
        {
            Node = node;
            UDPClient = listener;
            BinWriter = new BinaryWriter( new MemoryStream() );
            LocalEndPoint = (IPEndPoint)listener.Client.LocalEndPoint;
            m_ListenThread = new Thread( ThreadCallbackWT );
            m_ListenThread.Start();
        }

        void ThreadCallbackWT()
        {
            // Ensure working threadId is updated before anything else is executed from working thread.
            CurrentWorkingThreadId = Thread.CurrentThread.ManagedThreadId;

            while (!m_IsClosing)
            {
                byte [] data;
                IPEndPoint endpoint = new IPEndPoint(IPAddress.Any, 0);
                try
                { 
                    data = UDPClient.Receive( ref endpoint );

                } catch (SocketException e)
                {
                    if (e.SocketErrorCode != SocketError.Interrupted)
                    {
                        // Some error or force closed.
                        Node.AddMessage( new ReceptionError( Node, e.ErrorCode ) );
                    }
                    break;
                }
                catch (ObjectDisposedException)
                {
                    // May occur if closing while receiving was in started.
                    break;
                }

                // Check if we want to simulate packet loss.
                bool skipPacket = SimulatePacketLoss > 0 && m_Random.Next( 0, SimulatePacketLoss )==0;

                // If not skip packet, handle the packet from worker thread.
                if (!skipPacket)
                {
                    Node.ReceiveDataWT( data, endpoint, UDPClient );
                }
            }
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

            if (disposing)
            {
                m_IsClosing = true;
                UDPClient.Close();
                m_ListenThread.Join();
                UDPClient.Dispose();
                BinWriter.Dispose();
            }

            m_Disposed = true;
        }
    }
}
