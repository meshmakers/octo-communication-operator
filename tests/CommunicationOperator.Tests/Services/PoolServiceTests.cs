using k8s.Models;
using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services;

public class PoolServiceTests
{
    private readonly IOperatorHubInvoker _hub = Substitute.For<IOperatorHubInvoker>();
    private readonly PoolService _service;

    private const string MeshtestPoolRtId = "65d5c447b420da3fb12381a1";
    private const string EnergytestPoolRtId = "65d5c447b420da3fb12381a2";
    private const string AcmePoolRtId = "65d5c447b420da3fb12381a3";

    public PoolServiceTests()
    {
        _hub.IsConnected.Returns(true);
        _service = new PoolService(NullLogger<PoolService>.Instance, _hub);
    }

    private static V1CommunicationPoolEntity Entity(string tenantId, string poolRtId, string poolName) =>
        new()
        {
            Metadata = new V1ObjectMeta { Name = $"{tenantId}-{poolRtId}", NamespaceProperty = "octo" },
            Spec = new V1CommunicationPoolEntity.V1CommunicationPoolEntitySpec
            {
                TenantId = tenantId,
                PoolRtId = poolRtId,
                PoolName = poolName,
            },
            Status = new V1CommunicationPoolEntity.V1CommunicationPoolEntityStatus()
        };

    [Test]
    public async Task GetPools_TwoTenantsSamePoolName_KeepsBoth()
    {
        // Regression: a central operator typically manages multiple
        // CommunicationPool CRs whose poolName collides (every tenant has
        // its own `cloud` pool). The local pool dict used to be keyed by
        // poolName alone, so the second-reconciled CR overwrote the first.
        // After a SignalR reconnect (e.g. controller restart) the
        // operator's replay loop iterated GetPools() and only
        // re-registered one of the N colliding pools — workload deploys
        // for every other tenant then had no operator owning the pool and
        // either went nowhere or got mis-routed to whichever operator
        // happened to claim the bare poolName. Keying by (tenantId,
        // poolRtId) keeps both entries.
        var meshtest = Entity("meshtest", MeshtestPoolRtId, "cloud");
        var energytest = Entity("energytest", EnergytestPoolRtId, "cloud");

        await _service.RegisterPoolAsync(meshtest, CancellationToken.None);
        await _service.RegisterPoolAsync(energytest, CancellationToken.None);

        var pools = _service.GetPools();
        await Assert.That(pools.Count).IsEqualTo(2);
        await Assert.That(pools.Any(p => p.Entity.Spec.TenantId == "meshtest")).IsTrue();
        await Assert.That(pools.Any(p => p.Entity.Spec.TenantId == "energytest")).IsTrue();
        await _hub.Received(1).RegisterPoolAsync("meshtest", MeshtestPoolRtId, "cloud");
        await _hub.Received(1).RegisterPoolAsync("energytest", EnergytestPoolRtId, "cloud");
    }

    [Test]
    public async Task UnregisterPoolAsync_OnlyRemovesTheTargetedTenantPool()
    {
        // A consequence of the keying fix: unregister must be tenant-scoped
        // too, otherwise deleting one tenant's `cloud` CR would silently
        // remove the sibling tenant's still-active pool from the local dict.
        var meshtest = Entity("meshtest", MeshtestPoolRtId, "cloud");
        var energytest = Entity("energytest", EnergytestPoolRtId, "cloud");

        await _service.RegisterPoolAsync(meshtest, CancellationToken.None);
        await _service.RegisterPoolAsync(energytest, CancellationToken.None);

        await _service.UnRegisterPoolAsync(meshtest);

        var pools = _service.GetPools();
        await Assert.That(pools.Count).IsEqualTo(1);
        await Assert.That(pools.Single().Entity.Spec.TenantId).IsEqualTo("energytest");
        await _hub.Received(1).UnregisterPoolAsync("meshtest", MeshtestPoolRtId, "cloud");
    }

    [Test]
    public async Task RegisterPoolAsync_SameEntityTwice_DoesNotDuplicate()
    {
        var entity = Entity("acme", AcmePoolRtId, "default");

        await _service.RegisterPoolAsync(entity, CancellationToken.None);
        await _service.RegisterPoolAsync(entity, CancellationToken.None);

        await Assert.That(_service.GetPools().Count).IsEqualTo(1);
    }
}
