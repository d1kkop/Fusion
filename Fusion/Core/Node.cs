using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Fusion
{
    public sealed class Node : IDisposable
    {
        Thread m_SendThread;
        volatile bool m_IsClosing;
        Dictionary<IPEndPoint, Recipient> m_Recipients = new Dictionary<IPEndPoint, Recipient>();
        Dictionary<ushort, Listener> m_Listeners = new Dictionary<ushort, Listener>();

        public event Action<byte, byte [], IPEndPoint, byte> OnMessage;

        public Node()
        {
            m_SendThread = new Thread( SendLoop );
            m_SendThread.Start();
        }

        public void Sync()
        {
            lock (m_Recipients)
            {
                foreach (var kvp in m_Recipients)
                {
                    Recipient recipient = kvp.Value;
                    recipient.Sync();
                }
            }
        }

        public void Dispose()
        {
            m_IsClosing = true;
            if (m_SendThread != null)
            {
                m_SendThread.Join();
                m_SendThread = null;
            }
        }

        public ushort AddListener( ushort port, int simulatePacketLoss = 0 )
        {
            Listener listener;
            lock (m_Listeners)
            {
                if (port != 0)
                {
                    if (!m_Listeners.TryGetValue( port, out listener ))
                    {
                        listener = new Listener( this, new UdpClient( port ) );
                        m_Listeners.Add( port, listener );
                    }
                }
                else
                {
                    listener = new Listener( this, new UdpClient( 0 ) );
                    port     = (ushort)listener.LocalEndPoint.Port;
                    m_Listeners.Add( port, listener );
                }
            }
            listener.SimulatePacketLoss = simulatePacketLoss;
            return port;
        }

        public void RemoveListener( ushort port )
        {
            lock(m_Listeners)
            {
                m_Listeners.Remove( port );
            }
        }

        public void AddRecipient( string host, ushort port )
        {
            ushort listenPort = AddListener(0);
            AddRecipient( listenPort, host, port );
        }

        public void AddRecipient( ushort listenerPort, string host, ushort port )
        {
            Listener listener;
            lock (m_Listeners)
            {
                // If does not exist, first add a listener.
                listener = m_Listeners[listenerPort];
            }

            // Apparently, localhost:port is not recognized as valid IPEndPoint.
            if (host=="localhost")
                host ="127.0.0.1";
            string hostAndPort  = host+":"+port;  // Parse wants host with port added.
            IPEndPoint endpoint = IPEndPoint.Parse(hostAndPort);

            lock (m_Recipients)
            {
                if (!m_Recipients.TryGetValue( endpoint, out Recipient recipient ))
                {
                    recipient = new Recipient( this, endpoint, listener.UDPClient );
                    m_Recipients.Add( endpoint, recipient );
                }
                else throw new Exception( "Recipient already exists" );
            }
        }

        public void RemoveRecipient( IPEndPoint endpoint )
        {
            lock(m_Recipients)
            {
                m_Recipients.Remove( endpoint );
            }
        }

        public void Send( byte id, byte[] data, byte channel = 0, IPEndPoint target = null, IPEndPoint except = null )
        {
            lock (m_Recipients)
            {
                if (target != null)
                {
                    Recipient recipient;
                    if (m_Recipients.TryGetValue( target, out recipient ))
                    {
                        recipient.Send( id, data, channel );
                    }
                }
                else
                {
                    foreach (var kvp in m_Recipients)
                    {
                        Recipient recipient = kvp.Value;
                        if (recipient.EndPoint == except)
                            continue;
                        recipient.Send( id, data, channel );
                    }
                }
            }
        }

        internal void ReceiveDataWT( byte[] data, IPEndPoint endpoint, UdpClient client )
        {
            if (endpoint == null || endpoint == null || client == null)
                return;

            // Implicit get or add recipient from remote endpoint.
            Recipient recipient;
            lock (m_Recipients)
            {
                if (!m_Recipients.TryGetValue( endpoint, out recipient ))
                {
                    recipient = new Recipient( this, endpoint, client );
                    m_Recipients.Add( endpoint, recipient );
                }
            }

            //   try
            //   {
            using (BinaryReader reader = new BinaryReader( new MemoryStream( data, false ) ))
            {
                recipient.ReceiveDataWT( reader );
            }
            //   } catch (Exception e)
            {
                //        Debug.WriteLine( e );
            }
        }

        void SendLoop()
        {
            while (!m_IsClosing)
            {
                lock (m_Recipients)
                {
                    foreach (var kvp in m_Recipients)
                    {
                        Recipient recipient = kvp.Value;
                        recipient.FlushDataST();
                    }
                }
                Thread.Sleep( 30 );
            }
        }

        internal void RaiseOnMessage( byte id, byte[] data, IPEndPoint endpoint, byte channel )
        {
            OnMessage?.Invoke( id, data, endpoint, channel );
        }
    }
}
