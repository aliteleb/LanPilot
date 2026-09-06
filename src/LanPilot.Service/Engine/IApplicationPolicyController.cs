using LanPilot.Contracts;

namespace LanPilot.Service.Engine;

// OS mutation boundary: tests can inject failures without altering the host firewall.
public interface IApplicationPolicyController
{
    Task<IReadOnlyList<LocalApplicationSnapshot>> DiscoverAsync(IReadOnlyDictionary<string, LocalApplicationPolicy> policies, CancellationToken token);
    Task ApplyAsync(LocalApplicationPolicy policy, CancellationToken token);
    Task RemoveAsync(LocalApplicationPolicy policy, CancellationToken token);
    Task SuspendAllAsync(CancellationToken token);
}
