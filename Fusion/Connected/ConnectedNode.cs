using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Fusion
{
    public class ConnectedNode : Node, IAsyncDisposable
    {
        internal const int  m_ConnectAttemptIntervalMs  = 300;
        internal const int  m_ConnecTimeoutMs           = 20000;
        internal const int  m_KeepAliveMs               = 5000;
        internal const int  m_LostTimeoutMs             = 12000;
        internal const int  m_MaintenanceIntvervalMs    = 2000;
        internal const int  m_DisconnectLingerTimeMs    = 1000;

        internal bool m_IsServer   = false;
        internal bool m_IsClient   = false;
        internal bool m_IsDisposed = false;

        internal long m_LastCheckLostConnectionsMs = 0;
        internal Stopwatch Stopwatch { get; private set; }
        internal bool RemoveLostConnections { get; set; } = true;

        public string Password { get; set; }
        public ushort MaxUsers { get; set; }
        public GroupManager GroupManager { get; }
        public ConnectedRecipient Server { get; private set; }

        public event Action<ConnectedRecipient, ConnectResult> OnConnect;
        public event Action<ConnectedRecipient, DisconnectReason> OnDisconnect;
        public event Action<VariableGroup> OnGroupCreated;
        public event Action<VariableGroup> OnGroupDestroyed;

        public ConnectedNode()
        {
            Stopwatch    = new Stopwatch();
            GroupManager = new GroupManager( this );
            Stopwatch.Start();
        }

        public void Connect( string host, ushort port, string pw = "" )
        {
            if (m_IsClient || m_IsServer)
            {
                throw new InvalidOperationException( "Cannot reuse a node. Dispose and create a new one." );
            }
            m_IsClient = true;
            (AddRecipient( host, port ) as ConnectedRecipient).SendConnect( BinWriter, pw );
        }

        public void Host( ushort port, ushort maxUsers = 10, string password = "" )
        {
            if (m_IsClient || m_IsServer)
            {
                throw new InvalidOperationException( "Cannot reuse a node. Dispose and create a new one." );
            }
            m_IsServer = true;
            MaxUsers   = maxUsers;
            Password   = password;
            AddListener( port );
        }

        public void Migrate()
        {
            // TODO
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeAsyncCore();
        }

        protected override void Dispose( bool disposing )
        {
            if (m_IsDisposed)
                return;
            m_IsDisposed = true;
            if (disposing)
            {
                Disconnect( m_DisconnectLingerTimeMs );
            }
            base.Dispose( disposing );
        }

        protected virtual async ValueTask DisposeAsyncCore()
        {
            await Task.Run( () =>
            {
                Dispose();
            } );
        }

        internal void Disconnect( int timeout )
        {
            List<DeliveryTrace> disconnectDeliveries = new List<DeliveryTrace>();
            lock (m_Recipients)
            {
                foreach (var kvp in m_Recipients)
                {
                    ConnectedRecipient recipient = kvp.Value as ConnectedRecipient;
                    DeliveryTrace dt = recipient.SendDisconnect( BinWriter, ReliableStream.SystemChannel, true );
                    if (dt != null)
                    {
                        disconnectDeliveries.Add( dt );
                    }
                }
            }
            // Wait until all disconnects have been delivered or timeout was reached.
            disconnectDeliveries.ForEach( dt => dt.WaitAll( timeout ) );
        }

        public override void Sync()
        {
            base.Sync();
            GroupManager.Sync( BinWriter );
        }

        internal override Recipient CreateRecipient( Node node, IPEndPoint endpoint, UdpClient udpClient )
        {
            return new ConnectedRecipient( node as ConnectedNode, endpoint, udpClient );
        }

        internal override void ReceiveDataWT( byte[] data, IPEndPoint endpoint, UdpClient client )
        {
            base.ReceiveDataWT( data, endpoint, client );
            if (RemoveLostConnections) CheckLostConnectionsWT();
        }

        void CheckLostConnectionsWT()
        {
            long timeNowMs = Stopwatch.ElapsedMilliseconds;
            if (timeNowMs - m_LastCheckLostConnectionsMs < m_MaintenanceIntvervalMs)
            {
                return;
            }
            m_LastCheckLostConnectionsMs = timeNowMs;
            List<ConnectedRecipient> deadRecipients = null;
            lock (m_Recipients)
            {
                foreach (var kvp in m_Recipients)
                {
                    ConnectedRecipient recipient = kvp.Value as ConnectedRecipient;
                    if (timeNowMs - recipient.LastReceivedPacketMs > m_LostTimeoutMs)
                    {
                        if (deadRecipients == null) deadRecipients = new List<ConnectedRecipient>();
                        deadRecipients.Add( recipient );
                    }
                }
                if (deadRecipients != null)
                {
                    deadRecipients.ForEach( recipient =>
                    {
                        m_Recipients.Remove( recipient.EndPoint );
                        recipient.MarkAsLostConnectionWT();
                    } );
                }
            }
        }

        // --- Events ------------------------------------------------------------------------------------------------------

        internal void RaiseOnConnect( ConnectedRecipient recipient )
        {
            OnConnect?.Invoke( recipient, recipient.ConnectResult );
        }

        internal void RaiseOnDisconnect( ConnectedRecipient recipient )
        {
            OnDisconnect?.Invoke( recipient, recipient.DisconnectReason );
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
