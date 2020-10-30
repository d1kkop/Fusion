using Fusion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace TestReliable.Tests
{
    [TestClass()]
    public class Reliable
    {
        [TestMethod()]
        public void SendSingleMessageClient2Server()
        {
            int numMessagesReceived = 0;

            Node n1 = new Node();
            Node n2 = new Node();
            ushort boundPort = n1.AddListener(0);
            n1.AddRecipient( boundPort, "localhost", 3005 );
            n2.AddListener( 3005 );
            string myMessage = "A first message";
            byte [] data = Encoding.UTF8.GetBytes(myMessage);
            n1.Send( 20, data );

            n2.OnMessage += ( byte id, byte[] message, IPEndPoint recipient, byte channel ) =>
            {
                string recvMessage = Encoding.UTF8.GetString(message);
                Assert.AreEqual( myMessage, recvMessage );
                Assert.IsTrue( numMessagesReceived==0 );
                numMessagesReceived++;
            };

            while (numMessagesReceived==0)
            {
                n1.Sync();
                n2.Sync();
                Thread.Sleep( 30 );
            }

            Console.WriteLine( "Stopping" );
            n1.Dispose();
            n2.Dispose();
        }

        [TestMethod()]
        public void SendSingleMessageServerToClient()
        {
            int numMessagesReceived = 0;

            ushort dstPort = 3006;
            Node n1 = new Node();
            Node n2 = new Node();
            ushort boundPort = n1.AddListener(0);
            n1.AddRecipient( boundPort, "localhost", dstPort );
            n2.AddListener( dstPort );
            n2.AddRecipient( dstPort, "localhost", boundPort );
            string myMessage = "A first message";
            byte [] data = Encoding.UTF8.GetBytes(myMessage);
            n2.Send( 255, data );

            n1.OnMessage += ( byte id, byte[] message, IPEndPoint recipient, byte channel ) =>
            {
                string recvMessage = Encoding.UTF8.GetString(message);
                Assert.AreEqual( myMessage, recvMessage );
                Assert.IsTrue( numMessagesReceived==0 );
                numMessagesReceived++;
            };

            while (numMessagesReceived==0)
            {
                n1.Sync();
             //   n2.Sync();
                Thread.Sleep( 30 );
            }

            Console.WriteLine( "Stopping" );
            n1.Dispose();
            n2.Dispose();
        }

        [TestMethod()]
        public void FloodReliableMessages()
        {
            int numMessagesReceived = 0;

            ushort dstPort = 3007;
            Node n1 = new Node();
            Node n2 = new Node();
            ushort boundPort = n1.AddListener(0);
            n1.AddRecipient( boundPort, "localhost", dstPort );
            n2.AddListener( dstPort );
            n2.AddRecipient( dstPort, "localhost", boundPort );
            string myMessage = "A first message";
            byte [] data = Encoding.UTF8.GetBytes(myMessage);
            int numMsgsSent = 10000;
            for ( int i = 0; i < numMsgsSent; i++)
                n2.Send( 128, data );

            n1.OnMessage += ( byte id, byte[] message, IPEndPoint recipient, byte channel ) =>
            {
                string recvMessage = Encoding.UTF8.GetString(message);
                Assert.AreEqual( myMessage, recvMessage );
                numMessagesReceived++;
            };

            while (numMessagesReceived!=numMsgsSent)
            {
                n1.Sync();
                //   n2.Sync();
                Thread.Sleep( 30 );
            }

            Console.WriteLine( "Stopping" );
            n1.Dispose();
            n2.Dispose();
        }

        class ChannelData
        {
            public int numReceived;
            public List<string> messages;
            public List<byte> packIds;
        }

        [TestMethod()]
        public void CheckReliabilityWithPacketMassiveLoss()
        {
            int packetLoss = 2; // 50% packet loss
            ushort dstPort = 3008;
            Node n1 = new Node();
            Node n2 = new Node();
            ushort boundPort = n1.AddListener(0, packetLoss);
            n1.AddRecipient( boundPort, "localhost", dstPort );
            n2.AddListener( dstPort, packetLoss);
            n2.AddRecipient( dstPort, "localhost", boundPort );
            int numMsgsSent = 10000;
            Random rand = new Random();

            Dictionary<byte, ChannelData> channelDataN1 = new Dictionary<byte, ChannelData>();
            Dictionary<byte, ChannelData> channelDataN2 = new Dictionary<byte, ChannelData>();
            for (int j = 0;j < 2;j++)
            {
                for (int i = 0;i<numMsgsSent;i++)
                {
                    int a = rand.Next(0, 999);
                    int b = rand.Next(0, 999);
                    byte packId = (byte)rand.Next(20, 256);
                    byte chanId = (byte)rand.Next(0, 256);
                    var dic = channelDataN1;
                    if (j==1) dic = channelDataN2;
                    if (!dic.TryGetValue( chanId, out ChannelData chanData ))
                    {
                        chanData = new ChannelData();
                        chanData.messages = new List<string>();
                        chanData.packIds = new List<byte>();
                        chanData.numReceived = 0;
                        dic.Add( chanId, chanData );
                    }
                    string msg = $"this is {a} a random message {b}";
                    chanData.messages.Add( msg );
                    chanData.packIds.Add( packId );
                }
            }

            foreach ( var kvp in channelDataN1)
            {
                ChannelData cd = kvp.Value;
                for ( int i = 0; i < cd.messages.Count; i++ )
                {
                    n1.Send( cd.packIds[i], Encoding.UTF8.GetBytes( cd.messages[i] ), kvp.Key );
                }
            }
            foreach (var kvp in channelDataN2)
            {
                ChannelData cd = kvp.Value;
                for (int i = 0;i < cd.messages.Count;i++)
                {
                    n2.Send( cd.packIds[i], Encoding.UTF8.GetBytes( cd.messages[i] ), kvp.Key );
                }
            }

            n1.OnMessage += ( byte id, byte[] message, IPEndPoint recipient, byte channel ) =>
            {
                string recvMessage = Encoding.UTF8.GetString( message );
                ChannelData cd;
                channelDataN2.TryGetValue( channel, out cd );
                Assert.AreEqual( recvMessage, cd.messages[cd.numReceived] );
                Assert.AreEqual( id, cd.packIds[cd.numReceived] );
                cd.numReceived++;
            };

            n2.OnMessage += ( byte id, byte[] message, IPEndPoint recipient, byte channel ) =>
            {
                string recvMessage = Encoding.UTF8.GetString( message );
                ChannelData cd;
                channelDataN1.TryGetValue( channel, out cd );
                Assert.AreEqual( recvMessage, cd.messages[cd.numReceived] );
                Assert.AreEqual( id, cd.packIds[cd.numReceived] );
                cd.numReceived++;
            };

            while (!channelDataN1.All( kvp => kvp.Value.messages.Count == kvp.Value.numReceived ) ||
                   !channelDataN2.All( kvp => kvp.Value.messages.Count == kvp.Value.numReceived ) )
            {
                n1.Sync();
                n2.Sync();
                Thread.Sleep( 30 );
            }

            Console.WriteLine( "Stopping" );
            n1.Dispose();
            n2.Dispose();
        }
    }
}