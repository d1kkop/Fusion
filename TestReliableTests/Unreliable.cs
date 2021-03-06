﻿using Fusion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
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

            using (Node n1 = new Node())
            using (Node n2 = new Node())
            {
                ushort boundPort = n1.AddListener(0);
                n1.AddRecipient( boundPort, "localhost", 2005 );
                n2.AddListener( 2005 );
                string myMessage = "A first message";
                byte [] data = Encoding.UTF8.GetBytes(myMessage);
                n1.SendUnreliable( 20, data );

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
            }
        }

        [TestMethod()]
        public void SendSingleMessageServerToClient()
        {
            int numMessagesReceived = 0;

            ushort dstPort = 2006;
            using (Node n1 = new Node())
            using (Node n2 = new Node())
            {
                ushort boundPort = n1.AddListener(0);
                n1.AddRecipient( boundPort, "localhost", dstPort );
                n2.AddListener( dstPort );
                n2.AddRecipient( dstPort, "localhost", boundPort );
                string myMessage = "A first message";
                byte [] data = Encoding.UTF8.GetBytes(myMessage);
                n2.SendUnreliable( 20, data );

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
            }
        }

        [TestMethod()]
        public void FloodUnreliableMessages()
        {
            int numMessagesReceived = 0;

            ushort dstPort = 2007;
            using (Node n1 = new Node())
            using (Node n2 = new Node())
            {
                ushort boundPort = n1.AddListener(0);
                n1.AddRecipient( boundPort, "localhost", dstPort );
                n2.AddListener( dstPort );
                n2.AddRecipient( dstPort, "localhost", boundPort );
                string myMessage = "A first message";
                byte [] data = Encoding.UTF8.GetBytes(myMessage);
                int numMsgsSent = 20000;
                for (int i = 0;i < numMsgsSent;i++)
                    n2.SendUnreliable( 20, data );

                n1.OnMessage += ( byte id, byte[] message, IPEndPoint recipient, byte channel ) =>
                {
                    string recvMessage = Encoding.UTF8.GetString(message);
                    Assert.AreEqual( myMessage, recvMessage );
                    numMessagesReceived++;
                };

                int notProgressingTime = 0;
                int prevNumMessagesReceived = 0;
                while (numMessagesReceived!=numMsgsSent)
                {
                    n1.Sync();
                    if (numMessagesReceived != prevNumMessagesReceived)
                    {
                        prevNumMessagesReceived = numMessagesReceived;
                        notProgressingTime = 0;
                    }
                    else
                    {
                        if (notProgressingTime > 1000)
                            break;
                        notProgressingTime += 30;
                    }
                    //   n2.Sync();
                    Thread.Sleep( 30 );
                }

                if ( numMessagesReceived != numMsgsSent )
                {
                    Assert.Inconclusive( $"Num messages received {numMessagesReceived}, while sent {numMsgsSent}" );
                }
            }
        }
    }
}