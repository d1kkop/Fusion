using Fusion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Threading;

namespace TestReliable.Tests
{
    [TestClass()]
    public class Connection
    {
        [TestMethod()]
        public void ConnectWithWrongPW()
        {
            using (ConnectedNode client = new ConnectedNode())
            using (ConnectedNode server = new ConnectedNode())
            {
                bool waitOnResponse = true;
                bool serverError = false;
                bool clientError = false;

                client.OnConnect += ( ConnectedRecipient rec, ConnectResult res ) =>
                {
                    Assert.IsTrue( res == ConnectResult.InvalidPw );
                    waitOnResponse = false;
                };
                client.OnReceptionError += ( int error ) => clientError = true;

                server.OnConnect += ( ConnectedRecipient rec, ConnectResult res ) =>
                {
                    Assert.IsTrue( res == ConnectResult.Succes );
                };
                server.OnReceptionError += ( int error ) => serverError = true;


                client.Connect( "localhost", 7005, "wrong pw" );
                server.Host( 7005, 1, "my custom pw" );

                while (waitOnResponse && !(clientError || serverError))
                {
                    client.Sync();
                    server.Sync();
                    Thread.Sleep( 30 );
                }

                Assert.IsFalse( serverError );
                Assert.IsFalse( clientError );
            }
        }

        [TestMethod()]
        public async void ConnectWithTooManyClients()
        {
            await using (ConnectedNode server = new ConnectedNode())
            {
                ushort port = 7006;
                int exceedCount = 10;
                int numClients  = 100;
                int numTooManyClients = 0;

                server.OnConnect += ( ConnectedRecipient rec, ConnectResult res ) =>
                {
                    Assert.IsTrue( res == ConnectResult.Succes );
                };
                server.OnReceptionError += ( int error ) => Assert.IsFalse( true );
                server.Host( port, (ushort)numClients, "my custom pw" );

                List<ConnectedNode> clients = new List<ConnectedNode>();
                for (int i = 0;i < numClients+exceedCount;i++)
                {
                    ConnectedNode client = new ConnectedNode();
                    client.OnConnect += ( ConnectedRecipient rec, ConnectResult res ) =>
                    {
                        if (res == ConnectResult.MaxUsers)
                        {
                            numTooManyClients++;
                            Assert.IsTrue( numTooManyClients <= exceedCount );
                        }
                        else { Assert.IsTrue( res == ConnectResult.Succes ); }
                    };

                    client.OnReceptionError += ( int error ) => Assert.IsFalse( true );
                    client.Connect( "localhost", port, "my custom pw" );
                    clients.Add( client );
                }

                while (numTooManyClients!=exceedCount)
                {
                    clients.ForEach( c => c.Sync() );
                    server.Sync();
                    Thread.Sleep( 30 );
                }

                clients.ForEach( async (c) => await c.DisposeAsync() );
            }
        }

        [TestMethod()]
        public async void ConnectWithEverythingFine()
        {
            await using (var server = new ConnectedNode())
            {
                ushort port = 7008;
                int numClients   = 100;
                int numConnected = 0;

                server.OnConnect += ( ConnectedRecipient rec, ConnectResult res ) =>
                {
                    Assert.IsTrue( res == ConnectResult.Succes );
                };
                server.OnReceptionError += ( int error ) => Assert.IsFalse( true );
                server.Host( port, (ushort)numClients, "my custom pw 9918" );

                List<ConnectedNode> clients = new List<ConnectedNode>();
                for (int i = 0;i < numClients;i++)
                {
                    ConnectedNode client = new ConnectedNode();
                    client.OnConnect += ( ConnectedRecipient rec, ConnectResult res ) =>
                    {
                        Assert.IsTrue( res == ConnectResult.Succes );
                        numConnected++;
                    };

                    client.OnReceptionError += ( int error ) => Assert.IsFalse( true );
                    client.Connect( "localhost", port, "my custom pw 9918" );
                    clients.Add( client );
                }

                while (numConnected != numClients)
                {
                    clients.ForEach( c => c.Sync() );
                    server.Sync();
                    Thread.Sleep( 30 );
                } 

                clients.ForEach( async c => await c.DisposeAsync() );
                Assert.IsTrue( numClients == numConnected );
            }
        }
    }
}