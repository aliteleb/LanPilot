using LanPilot.Service;
using Xunit;

namespace LanPilot.Tests;

public sealed class ServiceInstanceTests
{
    [Fact]
    public void LeaseRejectsSecondOwnerAndCanBeReacquiredAfterRelease()
    {
        string directory = Path.Combine(Path.GetTempPath(), "LanPilot.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            using (var owner = new ServiceInstanceLease(directory))
                Assert.Throws<IOException>(() => new ServiceInstanceLease(directory));
            using var restartedOwner = new ServiceInstanceLease(directory);
        }
        finally { Directory.Delete(directory, true); }
    }
}
