using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Fusion
{
    internal sealed class Listener : IDisposable
    {
        internal IPEndPoint LocalEndPoint { get; }
        internal UdpClient UDPClient { get; }
        internal int SimulatePacketLoss { get; set; }
        internal int CurrentWorkingThreadId { get; set; }
        internal BinaryWriter BinWriter { get; private set; }

        Node   m_Node;
        Random m_Random = new Random();

        internal Listener( Node node, UdpClient listener )
        {
            BinWriter = new BinaryWriter( new MemoryStream() );
            m_Node = node;
            UDPClient = listener;
            LocalEndPoint = (IPEndPoint)listener.Client.LocalEndPoint;
            UDPClient.BeginReceive( ReceiveCallbackWT, null );
        }

        void ReceiveCallbackWT( IAsyncResult result )
        {
            // Ensure working threadId is updated before anything else is executed from working thread.
            CurrentWorkingThreadId = Thread.CurrentThread.ManagedThreadId;

            // Check if we want to simulate packet loss.
            bool skipPacket = SimulatePacketLoss > 0 && m_Random.Next( 0, SimulatePacketLoss )==0;

            // Receive data.
            IPEndPoint endpoint = new IPEndPoint(IPAddress.Any, 0);
            byte [] data = UDPClient.EndReceive( result, ref endpoint );

            // If not skip packet, handle the packet from worker thread.
            if (!skipPacket)
            {
                m_Node.ReceiveDataWT( data, endpoint, UDPClient );
            }

            // Restart receiving.
            UDPClient.BeginReceive( ReceiveCallbackWT, null );
        }

        public void Dispose()
        {
            GC.SuppressFinalize( this );
            UDPClient.Dispose();
            BinWriter.Dispose();
        }
    }
}
