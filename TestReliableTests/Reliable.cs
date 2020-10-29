using Fusion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
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
            n1.Send( 5, data );

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
            n2.Send( 5, data );

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
                n2.Send( 5, data );

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

        [TestMethod()]
        public void CheckReliabilityWithPacketMassiveLoss()
        {
            int numMessagesReceivedN1 = 0;
            int numMessagesReceivedN2 = 0;
            int packetLoss = 5;
            ushort dstPort = 3008;
            Node n1 = new Node();
            Node n2 = new Node();
            ushort boundPort = n1.AddListener(0, packetLoss);
            n1.AddRecipient( boundPort, "localhost", dstPort );
            n2.AddListener( dstPort, packetLoss);
            n2.AddRecipient( dstPort, "localhost", boundPort );
            int numMsgsSent = 10000;
            Random rand = new Random();

            List<byte> channelId = new List<byte>();
            List<byte> packetId = new List<byte>();
            List<string> randomMessages = new List<string>();
            for ( int i = 0; i<numMsgsSent; i++ )
            {
                int a = rand.Next(0, 999);
                int b = rand.Next(0, 999);
                byte packId = (byte)rand.Next(0, 256);
                byte chanId = (byte)rand.Next(0, 256);
                packetId.Add( packId );
                channelId.Add( 9 );
                randomMessages.Add( $"this is {a} a random message {b}" );
            }

            for (int i = 0; i < numMsgsSent;i++)
            {
                n1.Send( packetId[numMsgsSent-1-i], Encoding.UTF8.GetBytes( randomMessages[numMsgsSent-1-i] ) , channelId[numMsgsSent-1-i]);
                n2.Send( packetId[i], Encoding.UTF8.GetBytes( randomMessages[i] ), channelId[i] );
            }

            n1.OnMessage += ( byte id, byte[] message, IPEndPoint recipient, byte channel ) =>
            {
                string recvMessage = Encoding.UTF8.GetString( message );
                Assert.AreEqual( recvMessage, randomMessages[numMessagesReceivedN1] );
                Assert.AreEqual( id, packetId[numMessagesReceivedN1] );
                Assert.AreEqual( channel, channelId[numMessagesReceivedN1] );
                numMessagesReceivedN1++;
            };

            n2.OnMessage += ( byte id, byte[] message, IPEndPoint recipient, byte channel ) =>
            {
                string recvMessage = Encoding.UTF8.GetString( message );
                Assert.AreEqual( recvMessage, randomMessages[numMsgsSent- numMessagesReceivedN2-1] );
                Assert.AreEqual( id, packetId[numMsgsSent-numMessagesReceivedN2-1] );
                Assert.AreEqual( channel, channelId[numMsgsSent - numMessagesReceivedN2 -1] );
                numMessagesReceivedN2++;
            };

            while (numMessagesReceivedN1!=numMsgsSent || numMessagesReceivedN2!=numMsgsSent)
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