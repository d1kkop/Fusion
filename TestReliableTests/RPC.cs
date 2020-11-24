using Fusion;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace TestReliable.Tests
{
    [TestClass()]
    public class RPC
    {
        void SendMessage(string msg)
        {

        }

        [TestMethod()]
        public void RPCSend()
        {
            using (ConnectedNode client = new ConnectedNode())
            using (ConnectedNode server = new ConnectedNode())
            {

            }
        }
    }
}