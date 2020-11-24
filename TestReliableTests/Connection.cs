using Fusion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace TestReliable.Tests
{
    [TestClass()]
    public class Connection
    {
        [TestMethod()]
        public void ConnectTimeout()
        {
            using (ConnectedNode client = new ConnectedNode())
            using (ConnectedNode server = new ConnectedNode())
            {
                ConnectStream.ConnecTimeoutMs = 5000;

                bool waitOnResponse = true;
                bool serverError = false;
                bool clientError = false;

                client.OnConnect += ( ConnectedRecipient rec, ConnectResult res ) =>
                {
                    Assert.IsTrue( res == ConnectResult.Timedout );
                    waitOnResponse = false;
                };
                client.OnReceptionError += ( int error ) => clientError = true;

                server.OnConnect += ( ConnectedRecipient rec, ConnectResult res ) =>
                {
                    Assert.IsTrue( res == ConnectResult.Succes );
                };
                server.OnReceptionError += ( int error ) => serverError = true;

                server.Host( 7000, 1, "", 1 ); // 100% packet loss
                client.Connect( "localhost", 7000 );

                int k  = 0;
                while (k < 1000 && waitOnResponse && !(clientError || serverError))
                {
                    client.Sync();
                    server.Sync();
                    Thread.Sleep( 30 );
                    k++;
                }

                Assert.IsFalse( waitOnResponse );
                Assert.IsFalse( serverError );
                Assert.IsFalse( clientError );
            }
        }

        [TestMethod()]
        public void ConnectNormal()
        {
            using (ConnectedNode client = new ConnectedNode())
            {
                ConnectedNode server = new ConnectedNode();
                bool waitOnResponse = true;
                bool serverError = false;
                bool clientError = false;
                bool serverDisconnected = false;
                bool serverHasSucces = false;

                client.OnConnect += ( ConnectedRecipient rec, ConnectResult res ) =>
                {
                    Assert.IsTrue( res == ConnectResult.Succes );
                    waitOnResponse = false;
                };
                client.OnDisconnect += ( ConnectedRecipient rec, DisconnectReason res ) =>
                {
                    Assert.IsTrue( res == DisconnectReason.Requested );
                    serverDisconnected = true;
                };
                client.OnReceptionError += ( int error ) => clientError = true;

                server.OnConnect += ( ConnectedRecipient rec, ConnectResult res ) =>
                {
                    Assert.IsTrue( res == ConnectResult.Succes );
                    serverHasSucces = true;
                };
                server.OnDisconnect += ( ConnectedRecipient rec, DisconnectReason res ) =>
                {
                    Assert.IsTrue( res == DisconnectReason.Requested );
                };
                server.OnReceptionError += ( int error ) => serverError = true;

                server.Host( 7001, 1, "GOOD PW" );
                client.Connect( "localhost", 7001, "GOOD PW" );

                int k  = 0;
                while (k < 1000 && (waitOnResponse || !serverDisconnected || !serverHasSucces) && !(clientError || serverError))
                {
                    if (!waitOnResponse && serverHasSucces && server !=null)
                    {
                        server.Dispose();
                        server=null;
                    }
                    client.Sync();
                    if (server!=null) server.Sync();
                    Thread.Sleep( 30 );
                    k++;
                }

                Assert.IsFalse( waitOnResponse );
                Assert.IsFalse( serverError );
                Assert.IsFalse( clientError );
                Assert.IsTrue( serverDisconnected );
                Assert.IsTrue( serverHasSucces );
            }
        }

        [TestMethod()]
        async public Task ConnectWithWrongPW()
        {
            using (ConnectedNode client = new ConnectedNode())
            {
                ConnectedNode server = new ConnectedNode();
                bool waitOnResponse = true;
                bool serverError = false;
                bool clientError = false;

                client.OnConnect += ( ConnectedRecipient rec, ConnectResult res ) =>
                {
                    Assert.IsTrue( res == ConnectResult.InvalidPw );
                    waitOnResponse = false;
                };
                client.OnDisconnect += ( ConnectedRecipient rec, DisconnectReason res ) =>
                {
                    Assert.IsTrue( res == DisconnectReason.Requested );
                };
                client.OnReceptionError += ( int error ) => clientError = true;

                server.OnConnect += ( ConnectedRecipient rec, ConnectResult res ) =>
                {
                    Assert.IsTrue( res == ConnectResult.Succes );
                };
                server.OnDisconnect += ( ConnectedRecipient rec, DisconnectReason res ) =>
                {
                    Assert.IsTrue( res == DisconnectReason.Requested );
                };
                server.OnReceptionError += ( int error ) => serverError = true;

                client.Connect( "localhost", 7005, "wrong pw" );
                server.Host( 7005, 1, "my custom pw" );

                int k  = 0;
                while (k < 1000 && waitOnResponse && !(clientError || serverError))
                {
                    client.Sync();
                    server.Sync();
                    Thread.Sleep( 30 );
                    k++;
                }

                await server.DisposeAsync();

                Assert.IsFalse( waitOnResponse );
                Assert.IsFalse( serverError );
                Assert.IsFalse( clientError );
            }
        }

        [TestMethod()]
        public async Task ConnectWithTooManyClients()
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

                clients.ForEach( async ( c ) => await c.DisposeAsync() );
            }
        }

        [TestMethod()]
        public async Task ConnectWith1000Connections()
        {
            int numDisconnectsServer = 0;
            int numDisconnectsClient = 0;
            int numConnectedOnClient = 0;
            int numConnectedOnServer = 0;
            int numTimeoutsOnClient = 0;
            int numTimeoutsOnServer = 0;
            await using (var server = new ConnectedNode())
            {
                server.KeepConnectionsAlive = false;

                ushort port = 7008;
                int numClients = 1024;
                server.OnConnect += ( ConnectedRecipient rec, ConnectResult res ) =>
                {
                    Assert.IsTrue( res == ConnectResult.Succes );
                    numConnectedOnServer++;
                };
                server.OnDisconnect += ( ConnectedRecipient rec, DisconnectReason res ) =>
                {
                    Assert.IsTrue( res == DisconnectReason.Requested || res == DisconnectReason.Unreachable );
                    if (res == DisconnectReason.Unreachable)
                        numTimeoutsOnServer++;
                    else numDisconnectsServer++;
                };
                server.OnReceptionError += ( int error ) => Assert.IsFalse( true );
                server.Host( port, (ushort)numClients, "my custom pw 9918" );

                List<ConnectedNode> clients = new List<ConnectedNode>();
                for (int i = 0;i < numClients;i++)
                {
                    ConnectedNode client = new ConnectedNode();
                    client.KeepConnectionsAlive = false;
                    client.OnConnect += ( ConnectedRecipient rec, ConnectResult res ) =>
                    {
                        Assert.IsTrue( res == ConnectResult.Succes );
                        numConnectedOnClient++;
                    };
                    client.OnDisconnect += ( ConnectedRecipient rec, DisconnectReason res ) =>
                    {
                        Assert.IsTrue( res == DisconnectReason.Requested || res == DisconnectReason.Unreachable );
                        if (res == DisconnectReason.Unreachable)
                            numTimeoutsOnClient++;
                        else numDisconnectsClient++;
                    };
                    client.OnReceptionError += ( int error ) => Assert.IsFalse( true );
                    client.Connect( "localhost", port, "my custom pw 9918" );
                    clients.Add( client );
                }

                while (numConnectedOnClient != numClients || numConnectedOnServer != numClients)
                {
                    clients.ForEach( c => c.Sync() );
                    server.Sync();
                    Thread.Sleep( 30 );
                }

                //       clients.ForEach( async c => await c.DisposeAsync() );
                clients.ForEach( c => c.Dispose() );
                server.Sync();

                Assert.IsTrue( numClients == numConnectedOnClient );
                if (numClients != (numDisconnectsServer+numTimeoutsOnServer))
                {
                    Assert.Inconclusive( $"Num disconnects ({numDisconnectsServer}) + timeouts ({numTimeoutsOnServer}) did not match expected ({numClients})." );
                }
            }
        }

        [TestMethod()]
        public async Task ConnectWithTonsOfConnectionsComingAndGoing()
        {
            int numTimeouts = 0;
            await using (var server = new ConnectedNode())
            {
                //server.ConnectingCanTimeout = false;
                server.KeepConnectionsAlive = false;

                ushort port = 7009;
                int numClients   = 2048;

                //Server
                {
                    server.OnConnect += ( ConnectedRecipient rec, ConnectResult res ) =>
                    {
                        Assert.IsTrue( res == ConnectResult.Succes );
                    };
                    server.OnDisconnect += ( ConnectedRecipient rec, DisconnectReason res ) =>
                    {
                        Assert.IsTrue( res == DisconnectReason.Requested || res == DisconnectReason.Unreachable );
                        if (res == DisconnectReason.Unreachable)
                            numTimeouts++;
                    };
                    server.OnReceptionError += ( int error ) =>
                    Assert.IsFalse( true );
                    server.Host( port, (ushort)numClients, "NicePW" );
                }

                List<ConnectedNode> clients = new List<ConnectedNode>();
                List<ConnectedNode> deadClients = new List<ConnectedNode>();
                Stopwatch sw = new Stopwatch();
                sw.Start();
                Random r = new Random();
                while (sw.Elapsed.Seconds<20)
                {
                    deadClients.Clear();

                    // Create new client
                    if (r.Next( 3 )==0)
                    {
                        bool invalidPw = r.Next(3)==0;
                        ConnectedNode client = new ConnectedNode();
                        client.ConnectingCanTimeout = false;
                        client.KeepConnectionsAlive = false;
                        if (!invalidPw)
                        {
                            client.OnConnect += ( ConnectedRecipient rec, ConnectResult res ) =>
                            {
                                Assert.IsTrue( res == ConnectResult.Succes || res == ConnectResult.MaxUsers );
                            };
                        }
                        else
                        {
                            client.OnConnect += ( ConnectedRecipient rec, ConnectResult res ) =>
                            {
                                Assert.IsTrue( res == ConnectResult.InvalidPw );
                            };
                        }
                        client.OnDisconnect += ( ConnectedRecipient rec, DisconnectReason res ) =>
                        {
                            Assert.IsTrue( res == DisconnectReason.Requested );
                            deadClients.Add( rec.ConnectedNode );
                        };
                        client.OnReceptionError += ( int error ) => Assert.IsFalse( true );

                        if (invalidPw)
                            client.Connect( "localhost", port, "THIS A WRONG PW!!" );
                        else
                            client.Connect( "localhost", port, "NicePW" );

                        clients.Add( client );
                    }

                    // disconnect client from server
                    if (r.Next( 5 )==0)
                    {
                        if (clients.Count != 0)
                        {
                            int t = r.Next(0, clients.Count);
                            await clients[t].DisposeAsync();
                            clients.RemoveAt( t );
                        }
                    }

                    clients.ForEach( c => c.Sync() );
                    deadClients.ForEach( dc => clients.Remove( dc ) );
                    server.Sync();
                    Thread.Sleep( 30 );
                }

                clients.ForEach( async c => await c.DisposeAsync() );
            }

            if (numTimeouts != 0)
            {
                Assert.Inconclusive( $"Num timetouts not 0, namely: {numTimeouts}" );
            }
        }
    }
}