using System;
using Xunit;

namespace Microsoft.ManagedZLib.Tests
{
    public class MyClassTests
    {
        [Fact]
        public void Test1()
        {
            Assert.True(Microsoft.ManagedZLib.ManagedZLib.ReturnTrue);
        }
    }
}
