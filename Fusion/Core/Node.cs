using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Fusion
{
    public enum SendMethod
    {
        Reliable,
        Unreliable
    }

    public sealed class Node : Messagable, IDisposable
    {
        Thread m_SendThread;
        volatile bool m_IsClosing;
        Dictionary<uint, Listener> m_Listeners;
        Dictionary<IPEndPoint, Recipient> m_Recipients;

        public GroupManager GroupManager { get; }
        internal Recipient Server { get; private set; }
        internal BinaryWriter BinWriter { get; private set; }

        public event Action<byte, byte [], IPEndPoint, byte> OnMessage;
        public event Action<VariableGroup> OnGroupCreated;
        public event Action<VariableGroup> OnGroupDestroyed;

        public Node()
        {
            m_Recipients = new Dictionary<IPEndPoint, Recipient>();
            m_Listeners  = new Dictionary<uint, Listener>();
            GroupManager = new GroupManager( this );
            BinWriter    = new BinaryWriter( new MemoryStream() );
            m_SendThread = new Thread( SyncLoopST );
            m_SendThread.Start();
        }

        public void Sync()
        {
            GroupManager.Sync( BinWriter );
            lock (m_Recipients)
            {
                foreach (var kvp in m_Recipients)
                {
                    Recipient recipient = kvp.Value;
                    recipient.Sync();
                }
            }
            ProcessMessages();
        }

        public void Dispose()
        {
            // TODO
            GC.SuppressFinalize( this );
            m_IsClosing = true;
            if (m_SendThread != null)
            {
                m_SendThread.Join();
                m_SendThread = null;
            }
            if (m_Listeners != null)
            {
                foreach (var kvp in m_Listeners)
                {
                    kvp.Value.Dispose();
                }
                m_Listeners = null;
            }
            BinWriter.Dispose();
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

        public void Send( byte id, byte[] data, byte channel = 0, SendMethod sendMethod = SendMethod.Reliable, IPEndPoint target = null, IPEndPoint except = null )
        {
            if (id < (byte) SystemPacketId.Count)
            {
                throw new Exception( $"Id's 0 to {(byte)SystemPacketId.Count} are reserved." );
            }

            lock (m_Recipients)
            {
                if (target != null)
                {
                    Recipient recipient;
                    if (m_Recipients.TryGetValue( target, out recipient ))
                    {
                        recipient.Send( id, data, channel, sendMethod );
                    }
                }
                else
                {
                    foreach (var kvp in m_Recipients)
                    {
                        Recipient recipient = kvp.Value;
                        if (recipient.EndPoint == except)
                            continue;
                        recipient.Send( id, data, channel, sendMethod );
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
            using (BinaryWriter writer = new BinaryWriter( new MemoryStream() ))
            {
                recipient.ReceiveDataWT( reader, writer );
            }
            //   } catch (Exception e)
            {
                //        Debug.WriteLine( e );
            }
        }

        void SyncLoopST()
        {
            using (BinaryWriter binWriter = new BinaryWriter( new MemoryStream() ))
            {
                while (!m_IsClosing)
                {
                    lock (m_Recipients)
                    {
                        foreach (var kvp in m_Recipients)
                        {
                            Recipient recipient = kvp.Value;
                            recipient.FlushDataST( binWriter );
                        }
                    }
                    GroupManager.Sync( binWriter );
                    Thread.Sleep( 30 );
                }
            }
        }

        internal bool IsSendThread()
        {
            return Thread.CurrentThread.ManagedThreadId == m_SendThread.ManagedThreadId;
        }

        // ---- Events ---------------------------------------------------------------------------------------

        internal void RaiseOnMessage( byte id, byte[] data, IPEndPoint endpoint, byte channel )
        {
            OnMessage?.Invoke( id, data, endpoint, channel );
        }

        internal void RaiseOnGroupCreated( VariableGroup group )
        {
            OnGroupCreated?.Invoke( group );
        }

        internal void RaiseOnGroupDestroyed( VariableGroup group )
        {
            OnGroupDestroyed?.Invoke( group );
        }
    }
}
