using Fusion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Linq;

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

        internal struct RPCData1
        {
            internal int m_a;
            internal byte m_b;
            internal char m_c;
            internal double m_d;
            internal string m_name;
            internal List<string> m_people;
        }

        internal struct RPCData2
        {
            internal Dictionary<string, string > m_phonebook;
            internal List<string> m_people;
        }

        internal struct RPCData3
        {
            internal float m_f;
            internal decimal m_dm;
            internal byte m_b;
            internal sbyte m_sb;
            internal uint m_ui;
            internal ushort m_us;
            internal short m_s;
            internal long m_l;
            internal ulong m_ul;
            internal Dictionary<string, string > m_phonebook;
            internal List<string> m_people;
        }

        static int m_recvNum1, m_recvNum2, m_recvNum3;
        static Dictionary<int, RPCData1> m_rpcData1;
        static Dictionary<int, RPCData2> m_rpcData2;
        static Dictionary<int, RPCData3> m_rpcData3;

        [RPC]
        public static void ManyRPC1( int a, byte b, char c, double d, string name, List<string> people, ConnectedRecipient recipient, byte channel )
        {
            Assert.IsTrue( m_rpcData1[m_recvNum1].m_a == a );
            Assert.IsTrue( m_rpcData1[m_recvNum1].m_b == b );
            Assert.IsTrue( m_rpcData1[m_recvNum1].m_c == c );
            Assert.IsTrue( m_rpcData1[m_recvNum1].m_d == d );
            Assert.IsTrue( m_rpcData1[m_recvNum1].m_name == name );
            List<string> tpeople = m_rpcData1[m_recvNum1].m_people;
            for (int i = 0; i < tpeople.Count; i++)
            {
                Assert.IsTrue( tpeople[i] == people[i] );
            }
            m_recvNum1++;
        }

        [RPC]
        public static void ManyRPC2( Dictionary<string, string> phonebook, List<string> people, ConnectedRecipient recipient, byte channel )
        {
            Assert.IsTrue( m_rpcData2[m_recvNum2].m_phonebook.All( kvp => phonebook.Contains( kvp ) ) );
            Assert.IsTrue( m_rpcData2[m_recvNum2].m_people.All( kvp => people.Contains( kvp ) ) );
            m_recvNum2++;
        }

        [RPC]
        public static void ManyRPC3( float f, decimal dm, byte b, sbyte sb, uint ui, ushort us, short s, long l, ulong ul,
            Dictionary<string, string> phonebook, List<string> people, ConnectedRecipient recipient, byte channel )
        {
            Assert.IsTrue( m_rpcData3[m_recvNum3].m_f == f );
            Assert.IsTrue( m_rpcData3[m_recvNum3].m_dm == dm );
            Assert.IsTrue( m_rpcData3[m_recvNum3].m_b == b );
            Assert.IsTrue( m_rpcData3[m_recvNum3].m_sb == sb );
            Assert.IsTrue( m_rpcData3[m_recvNum3].m_ui == ui );
            Assert.IsTrue( m_rpcData3[m_recvNum3].m_us == us );
            Assert.IsTrue( m_rpcData3[m_recvNum3].m_s == s );
            Assert.IsTrue( m_rpcData3[m_recvNum3].m_l == l );
            Assert.IsTrue( m_rpcData3[m_recvNum3].m_ul == ul );
            Assert.IsTrue( m_rpcData3[m_recvNum3].m_phonebook == phonebook );
            Assert.IsTrue( m_rpcData3[m_recvNum3].m_people == people );
            m_recvNum3++;
        }

        [TestMethod()]
        public void ManyRPCS()
        {
            int numMessages = 1000;
            m_rpcData1 = new Dictionary<int, RPCData1>();
            Random r = new Random();
            for (int i = 0;i < numMessages;i++)
            {
                RPCData1 rpData = new RPCData1();
                rpData.m_a = r.Next();
                rpData.m_b = (byte)r.Next();
                rpData.m_c = (char)r.Next( 0, 127 );
                rpData.m_d = r.NextDouble();
                rpData.m_name = "this a very short message but should actually be a very long string";
                rpData.m_people = new List<string>(
                    new [] { "a", "b", "c", "abcdefghijakladjfklajlaaaaaaaaammmmmmmmmmmm" }  
                    );
                m_rpcData1.Add( i, rpData );
            }

            using (ConnectedNode client = new ConnectedNode())
            using (ConnectedNode server = new ConnectedNode())
            {
                server.KeepConnectionsAlive = false;
                client.KeepConnectionsAlive = false;

                server.Host( 3101, 1, "my pw", 5 );
                client.Connect( "127.0.0.1", 3101, "my pw" );

                bool serverAlive = true;

                client.OnConnect += ( ConnectedRecipient recipient, ConnectResult result ) =>
                {
                    for (int i = 0;i < numMessages;i++)
                    {
                        client.DoReliableRPC( "ManyRPC1", 0, null, false,
                            m_rpcData1[i].m_a, m_rpcData1[i].m_b, m_rpcData1[i].m_c,
                            m_rpcData1[i].m_d, m_rpcData1[i].m_name,
                            m_rpcData1[i].m_people
                            );
                    }
                };

                server.OnDisconnect += ( ConnectedRecipient recipient, DisconnectReason reason ) =>
                {
                    serverAlive = false;
                };

                while (m_recvNum1 != numMessages && serverAlive)
                {
                    client.Sync();
                    server.Sync();
                    Thread.Sleep( 30 );
                }

                Assert.IsTrue( m_recvNum1 == numMessages );
            }
        }

        [TestMethod()]
        public void ManyRPCS2()
        {
            int numMessages = 1000;

            m_rpcData2= new Dictionary<int, RPCData2>();
            for (int i = 0;i < numMessages;i++)
            {
                RPCData2 rpData = new RPCData2();
                rpData.m_people = new List<string>();
                rpData.m_phonebook = new Dictionary<string, string>();
                rpData.m_phonebook.Add( "jaap", "063738881" );
                rpData.m_phonebook.Add( "tim", "123345234" );
                rpData.m_phonebook.Add( "erk jan", "1232131q65634563452345" );
                rpData.m_phonebook.Add( "tralal lalal", "123123123fasdfasd" );
                rpData.m_phonebook.Add( "rafl pafl", "063738asdfasdfasfd6563456356881" );
                rpData.m_phonebook.Add( "what is this", "adsfasfdaaaaaaaaa" );
                rpData.m_phonebook.Add( "a number", "adsfadsfsdfkkkkkkkkkkk" );
                rpData.m_phonebook.Add( "not a name", "asdfasdf55555555%%%%%%%%%%%%%!!!!!!!!!@@@@@@@@@@@@@@##############$$$$$$$$$$$$$" );
                rpData.m_phonebook.Add( "not a1 name", "asdfasdf55555555%%%%%%%%%%%%%!!!!!!!!!@@@@@@@@@@@@@@##############$$$$$$$$$$$$$1" );
                rpData.m_phonebook.Add( "not a2 name", "asdfasdf55555555%%%%%%%%%%%%%!!!!!!!!!@@@@@@@@@@@@@@##############$$$$$$$$$$$$$2" );
                rpData.m_phonebook.Add( "not a3 name", "asdfasdf55555555%%%%%%%%%%%%%!!!!!!!!!@@@@@@@@@@@@@@##############$$$$$$$$$$$$$3" );
                rpData.m_phonebook.Add( "not a4 name", "asdfasdf55555555%%%%%%%%%%%%%!!!!!!!!!@@@@@@@@@@@@@@##############$$$$$$$$$$$$$4" );
                rpData.m_phonebook.Add( "not a5 name", "asdfasdf55555555%%%%%%%%%%%%%!!!!!!!!!@@@@@@@@@@@@@@##############$$$$$$$$$$$$$5" );
                rpData.m_phonebook.Add( "not a6 name", "asdfasdf55555555%%%%%%%%%%%%%!!!!!!!!!@@@@@@@@@@@@@@##############$$$$$$$$$$$$$6" );
                rpData.m_phonebook.Add( "not a7 name", "asdfasdf55555555%%%%%%%%%%%%%!!!!!!!!!@@@@@@@@@@@@@@##############$$$$$$$$$$$$$7" );
                rpData.m_phonebook.Add( "not a8 name", "asdfasdf55555555%%%%%%%%%%%%%!!!!!!!!!@@@@@@@@@@@@@@##############$$$$$$$$$$$$$8" );
                rpData.m_phonebook.Add( "not a9 name", "asdfasdf55555555%%%%%%%%%%%%%!!!!!!!!!@@@@@@@@@@@@@@##############$$$$$$$$$$$$$9" );
                m_rpcData2.Add( i, rpData );
            }

            using (ConnectedNode client = new ConnectedNode())
            using (ConnectedNode server = new ConnectedNode())
            {
                server.KeepConnectionsAlive = false;
                client.KeepConnectionsAlive = false;

                server.Host( 3102, 1, "my pw", 5 );
                client.Connect( "127.0.0.1", 3102, "my pw" );

                bool serverAlive = true;

                client.OnConnect += ( ConnectedRecipient recipient, ConnectResult result ) =>
                {
                    for (int i = 0;i < numMessages;i++)
                    {
                        client.DoReliableRPC( "ManyRPC2", 0, null, false, m_rpcData2[i].m_phonebook, m_rpcData2[i].m_people );

                    }
                };

                server.OnDisconnect += ( ConnectedRecipient recipient, DisconnectReason reason ) =>
                {
                    serverAlive = false;
                };

                while (m_recvNum2 != numMessages && serverAlive)
                {
                    client.Sync();
                    server.Sync();
                    Thread.Sleep( 30 );
                }

                Assert.IsTrue( m_recvNum2 == numMessages );
            }
        }
    }
}