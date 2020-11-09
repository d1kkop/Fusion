using Fusion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading;

namespace TestReliable.Tests
{
    [TestClass()]
    public class VarGroups
    {
        [TestMethod()]
        public void CreateSingleVariableGroup()
        {
            return;
            //ConnectedNode client = new ConnectedNode();
            //ConnectedNode server = new ConnectedNode();

            //client.Connect( "localhost", 7005 );
            //server.Host( 7005, 20, "my custom pw" );

            ////n1.GroupManager.CreateGroup( 55,
            ////    UpdatableType.Int,
            ////    UpdatableType.Double,
            ////    UpdatableType.Int );

            //while (true)
            //{
            //    client.Sync();
            //    server.Sync();
            //    Thread.Sleep( 30 );
            //}

            //Console.WriteLine( "Stopping" );
            //client.Dispose();
            //server.Dispose();
        }

        [TestMethod()]
        public void NumUpdatableTypes()
        {
            Assert.IsTrue( (uint)UpdatableType.Count < 256 ); // Otherwise cannot send type as a byte.
        }
    }
}