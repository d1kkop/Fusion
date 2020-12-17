using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Fusion
{
    internal enum SendMethod
    {
        Reliable,
        Unreliable,
        Connect
    }

    internal enum StreamId
    {
        CID,
        RID,
        RACK,
        UID
    }

    internal enum SystemPacketId
    {
        IdPackRequest,
        IdPackProvide,
        CreateGroup,
        DestroyGroup,
        DestroyAllGroups,
        Connect,
        ConnectInvalidPw,
        ConnectMaxUsers,
        ConnectAccepted,
        Disconnect,
        KeepAlive,
        RPC,
        Count
    }

    public class Node : Messagable, IDisposable
    {
        Thread m_SendThread;
        bool   m_IsDisposed;
        volatile bool  m_IsClosing;
        protected Dictionary<uint, Listener> m_Listeners;
        protected Dictionary<IPEndPoint, Recipient> m_Recipients;

        internal int NumRecipients => m_Recipients.Count;
        internal BinaryWriter BinWriter { get; private set; }

        public event Action<byte, byte [], IPEndPoint, byte> OnMessage;
        public event Action<int> OnReceptionError;

        public Node()
        {
            m_Recipients = new Dictionary<IPEndPoint, Recipient>();
            m_Listeners  = new Dictionary<uint, Listener>();
            BinWriter    = new BinaryWriter( new MemoryStream() );
            m_SendThread = new Thread( FlushST );
            m_SendThread.Start();
        }

        public virtual void Sync()
        {
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
            Dispose( true );
            GC.SuppressFinalize( this );
        }

        protected virtual void Dispose( bool disposing )
        {
            if (m_IsDisposed)
                return;
            m_IsDisposed = true;
            if (disposing)
            {
                m_IsClosing  = true;
                m_SendThread.Join();
                foreach (var kvp in m_Listeners)
                {
                    kvp.Value.Dispose();
                }
                foreach (var kvp in m_Recipients)
                {
                    kvp.Value.Dispose();
                }
                BinWriter.Dispose();
            }
        }

        public ushort AddListener( ushort port, int simulatePacketLoss = 0 )
        {
            Listener listener;
            if (port != 0)
            {
                if (!m_Listeners.TryGetValue( port, out listener ))
                {
                    listener = CreateListener( this, new UdpClient( port ) );
                    m_Listeners.Add( port, listener );
                }
            }
            else
            {
                listener = CreateListener( this, new UdpClient( 0 ) );
                port     = (ushort)listener.LocalEndPoint.Port;
                m_Listeners.Add( port, listener );
            }
            listener.SimulatePacketLoss = simulatePacketLoss;
            return port;
        }

        public void RemoveListener( ushort port )
        {
            m_Listeners.Remove( port );
        }

        public Recipient AddRecipient( string host, ushort port )
        {
            ushort listenPort = AddListener(0);
            return AddRecipient( listenPort, host, port );
        }

        public Recipient AddRecipient( ushort listenerPort, string host, ushort port )
        {
            Listener listener = m_Listeners[listenerPort];

            // Apparently, localhost:port is not recognized as valid IPEndPoint.
            if (host=="localhost")
                host ="127.0.0.1";
            string hostAndPort  = host+":"+port;  // Parse wants host with port added.
            IPEndPoint endpoint = IPEndPoint.Parse(hostAndPort);

            lock (m_Recipients)
            {
                if (!m_Recipients.TryGetValue( endpoint, out Recipient recipient ))
                {
                    recipient = CreateRecipient( this, endpoint, listener.UDPClient );
                    m_Recipients.Add( endpoint, recipient );
                    return recipient;
                }
                else throw new InvalidOperationException( "Recipient already exists" );
            }
        }

        public void RemoveRecipient( IPEndPoint endpoint )
        {
            lock (m_Recipients)
            {
                m_Recipients.Remove( endpoint );
            }
        }

        public void SendUnreliable( byte id, ArraySegment<byte> data, IPEndPoint target = null, IPEndPoint except = null )
        {
            SendPrivate( id, data, 0, SendMethod.Unreliable, target, except, false );
        }

        public void SendReliable( byte id, ArraySegment<byte> data, byte channel = 0, IPEndPoint target = null, IPEndPoint except = null )
        {
            if (channel == ReliableStream.SystemChannel)
            {
                throw new InvalidOperationException( "Channel " + channel + " is reserved, use different." );
            }
            SendPrivate( id, data, channel, SendMethod.Reliable, target, except, false );
        }

        public DeliveryTrace SendReliableWithTrace( byte id, ArraySegment<byte> data, byte channel = 0, IPEndPoint target = null, IPEndPoint except = null )
        {
            if (channel == ReliableStream.SystemChannel)
            {
                throw new InvalidOperationException( "Channel " + channel + " is reserved, use different." );
            }
            return SendPrivate( id, data, channel, SendMethod.Reliable, target, except, true );
        }

        internal DeliveryTrace SendPrivate( byte id, ArraySegment<byte> data, byte channel = 0, SendMethod sendMethod = SendMethod.Reliable, IPEndPoint target = null, IPEndPoint except = null, bool traceDelivery = false )
        {
            // NOTE: The original data must be copied because the caller may change it after the call.
            // We also may deal with multiple recipients. So they can all share the same duplicated data.
            byte [] dataCpy = data.GetCopy();
            DeliveryTrace dt = traceDelivery ? new DeliveryTrace() : null;
            lock (m_Recipients)
            {
                if (target != null)
                {
                    Recipient recipient;
                    if (m_Recipients.TryGetValue( target, out recipient ))
                    {
                        recipient.Send( id, dataCpy, channel, false, sendMethod, dt );
                    }
                }
                else
                {
                    foreach (var kvp in m_Recipients)
                    {
                        Recipient recipient = kvp.Value;
                        if (recipient.EndPoint == except)
                            continue;
                        recipient.Send( id, dataCpy, channel, false, sendMethod, dt );
                    }
                }
            }
            return dt;
        }

        internal void SendUnreliablePrivate( byte id, bool isSystem, ArraySegment<byte> data, IPEndPoint target = null, IPEndPoint except = null )
        {
            // NOTE: The original data must be copied because the caller may change it after the call.
            // We also may deal with multiple recipients. So they can all share the same duplicated data.
            byte [] dataCpy = data.GetCopy();
            lock (m_Recipients)
            {
                if (target != null)
                {
                    Recipient recipient;
                    if (m_Recipients.TryGetValue( target, out recipient ))
                    {
                        recipient.Send( id, dataCpy, 0, false, SendMethod.Unreliable, null );
                    }
                }
                else
                {
                    foreach (var kvp in m_Recipients)
                    {
                        Recipient recipient = kvp.Value;
                        if (recipient.EndPoint == except)
                            continue;
                        recipient.Send( id, dataCpy, 0, false, SendMethod.Unreliable, null );
                    }
                }
            }
        }

        internal virtual void ReceiveDataWT( byte[] data, IPEndPoint endpoint, UdpClient client )
        {
            if (endpoint == null || endpoint == null || client == null)
                return;

            // Implicit get or add recipient from remote endpoint.
            Recipient recipient;
            lock (m_Recipients)
            {
                if (!m_Recipients.TryGetValue( endpoint, out recipient ))
                {
                    recipient = CreateRecipient( this, endpoint, client );
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

        void FlushST()
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
                    Thread.Sleep( 30 );
                }
            }
        }

        internal bool IsSendThread()
        {
            return Thread.CurrentThread.ManagedThreadId == m_SendThread.ManagedThreadId;
        }

        internal virtual Recipient CreateRecipient( Node node, IPEndPoint endpoint, UdpClient udpClient )
        {
            return new Recipient( node, endpoint, udpClient );
        }

        internal virtual Listener CreateListener( Node node, UdpClient udpClient )
        {
            return new Listener( node, udpClient );
        }

        // ---- Events ---------------------------------------------------------------------------------------

        internal void RaiseOnMessage( byte id, byte[] data, IPEndPoint endpoint, byte channel )
        {
            OnMessage?.Invoke( id, data, endpoint, channel );
        }

        internal void RaiseOnReceptionError( int error )
        {
            OnReceptionError?.Invoke( error );
        }
    }
}
