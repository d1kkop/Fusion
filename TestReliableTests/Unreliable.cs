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
    public class Unreliable
    {
        [TestMethod()]
        public void SendSingleMessageClient2Server()
        {
            int numMessagesReceived = 0;

            Node n1 = new Node();
            Node n2 = new Node();
            ushort boundPort = n1.AddListener(0);
            n1.AddRecipient( boundPort, "localhost", 2005 );
            n2.AddListener( 2005 );
            string myMessage = "A first message";
            byte [] data = Encoding.UTF8.GetBytes(myMessage);
            n1.Send( 20, data, 0, SendMethod.Unreliable );

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

            ushort dstPort = 2006;
            Node n1 = new Node();
            Node n2 = new Node();
            ushort boundPort = n1.AddListener(0);
            n1.AddRecipient( boundPort, "localhost", dstPort );
            n2.AddListener( dstPort );
            n2.AddRecipient( dstPort, "localhost", boundPort );
            string myMessage = "A first message";
            byte [] data = Encoding.UTF8.GetBytes(myMessage);
            n2.Send( 20, data, 0, SendMethod.Unreliable );

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
        public void FloodUnreliableMessages()
        {
            int numMessagesReceived = 0;

            ushort dstPort = 2007;
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
                n2.Send( 20, data, 0, SendMethod.Unreliable );

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
    }
}