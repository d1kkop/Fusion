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
    public class VarGroups
    {
        [TestMethod()]
        public void CreateSingleVariableGroup()
        {
            Node n1 = new Node();
            Node n2 = new Node();
            ushort boundPort = n1.AddListener(0);
            n1.AddRecipient( boundPort, "localhost", 4005 );
            n2.AddListener( 4005 );

            n1.GroupManager.CreateGroup( 55,
                UpdatableType.Int,
                UpdatableType.Double,
                UpdatableType.Int );


            while (true)
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
        public void NumUpdatableTypes()
        {
            Assert.IsTrue( (uint)UpdatableType.Count < 256 ); // Otherwise cannot send type as a byte.
        }
    }
}