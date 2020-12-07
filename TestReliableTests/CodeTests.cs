using Fusion;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace TestReliable.Tests
{
    [TestClass()]
    public class CodeTests
    {
        uint FindStorePositionForNewer(uint having, uint incoming)
        {
            uint d = incoming - having;
            return d;
        }

        [TestMethod()]
        public void WrapAround()
        {
            uint s1 = FindStorePositionForNewer( 16, 18 );
            Assert.IsTrue( s1 == 2 );
            uint s2 = FindStorePositionForNewer( 16, 15 );
            Assert.IsTrue( s2 == uint.MaxValue );
            uint s3 = FindStorePositionForNewer( uint.MaxValue -2, 2 );
            Assert.IsTrue( s3 == 5 );
        }

        [TestMethod()]
        public void Kaas()
        {
            byte [] data = new byte[10];
            data[3] = 5;
            

        }
    }
}