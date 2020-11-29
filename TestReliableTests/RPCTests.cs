using Fusion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading;

namespace TestReliable.Tests
{
    [TestClass()]
    public class RPCTests
    {
        static bool receivedRpc;
        static bool receivedRpcU;

        [RPC]
        public static void SendMessage(int a, byte b, char c, double d, string name, ConnectedRecipient recipient, byte channel )
        {
            Assert.IsTrue( a==1 );
            Assert.IsTrue( b==2 );
            Assert.IsTrue( c==3 );
            Assert.IsTrue( d==5.123 );
            Assert.IsTrue( name=="bartje" );

            if (recipient != null)
            {
                receivedRpc = true;
            }
        }

        [RPC]
        public static void SendMessage2( int a, byte b, char c, double d, string name, ConnectedRecipient recipient, byte channel )
        {
            Assert.IsTrue( a==5 );
            Assert.IsTrue( b==88 );
            Assert.IsTrue( c=='Z' );
            Assert.IsTrue( d==8812.7123712e53d );
            Assert.IsTrue( name=="jahoo" );

            if (recipient != null)
            {
                receivedRpcU = true;
            }
        }

        [TestMethod()]
        public void RPCSend()
        {
            using (ConnectedNode client = new ConnectedNode())
            using (ConnectedNode server = new ConnectedNode())
            {
                server.KeepConnectionsAlive = false;
                client.KeepConnectionsAlive = false;

                server.Host( 3100, 1, "my pw" );
                client.Connect( "127.0.0.1", 3100, "my pw" );
                
                bool serverAlive = true;

                client.OnConnect += ( ConnectedRecipient recipient, ConnectResult result ) =>
                {
                    client.DoReliableRPC( "SendMessage", 0, null, true, 1, (byte)2, (char)3, 5.123, "bartje" );
                    client.DoUnreliableRPC( "SendMessage2", null, true, 5, (byte)88, 'Z', 8812.7123712e53d, "jahoo" );
                };

                server.OnDisconnect += ( ConnectedRecipient recipient, DisconnectReason reason ) =>
                {
                    serverAlive = false;
                };

                while ((!receivedRpc || !receivedRpcU) && serverAlive)
                {
                    client.Sync();
                    server.Sync();
                    Thread.Sleep( 30 );
                }

                Assert.IsTrue( receivedRpc );
            }
        }
    }
}