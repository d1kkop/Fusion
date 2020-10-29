using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Fusion
{
    internal class Listener
    {
        public IPEndPoint LocalEndPoint { get; }
        public UdpClient UDPClient { get; }
        public int SimulatePacketLoss { get; set; }

        Node          m_Node;
        Random        m_Random = new Random();

        internal Listener( Node node, UdpClient listener )
        {
            m_Node = node;
            UDPClient = listener;
            LocalEndPoint = (IPEndPoint)listener.Client.LocalEndPoint;
            UDPClient.BeginReceive( ReceiveCallbackWT, null );
        }

        void ReceiveCallbackWT( IAsyncResult result )
        {
            bool skipPacket = SimulatePacketLoss > 0 && m_Random.Next( 0, SimulatePacketLoss )==0;
            IPEndPoint endpoint = new IPEndPoint(IPAddress.Any, 0);
            byte [] data = UDPClient.EndReceive( result, ref endpoint );
            if (!skipPacket)
            {
                m_Node.ReceiveDataWT( data, endpoint, UDPClient );
            }
            UDPClient.BeginReceive( ReceiveCallbackWT, null );
        }
    }
}
