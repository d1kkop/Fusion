using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Fusion
{
    public class ConnectedNode : Node, IAsyncDisposable
    {
        internal bool m_IsServer  = false;
        internal bool m_IsClient  = false;
        internal bool m_IsDisposed = false;

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
            GroupManager = new GroupManager( this );
        }

        public void Connect( string host, ushort port, string pw = "" )
        {
            if (m_IsClient || m_IsServer)
            {
                throw new InvalidOperationException( "First disconnect." );
            }
            m_IsClient = true;
            (AddRecipient( host, port ) as ConnectedRecipient).SendConnect( BinWriter, pw );
        }

        public void Host( ushort port, ushort maxUsers = 10, string password = "" )
        {
            if (m_IsClient || m_IsServer)
            {
                throw new InvalidOperationException( "First disconnect." );
            }
            m_IsServer = true;
            MaxUsers   = maxUsers;
            Password   = password;
            AddListener( port );
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
                lock (m_Recipients)
                {
                    foreach (var kvp in m_Recipients)
                    {
                        ConnectedRecipient recipient = kvp.Value as ConnectedRecipient;
                        recipient.SendDisconnect( BinWriter, ReliableStream.SystemChannel );
                    }
                }
                Thread.Sleep( 1000 ); // Await disconnect messages to be transmitted.
            }
            base.Dispose( disposing );
        }

        protected virtual async ValueTask DisposeAsyncCore()
        {
            await Task.Run( () =>
            {
                Dispose();
            });
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
