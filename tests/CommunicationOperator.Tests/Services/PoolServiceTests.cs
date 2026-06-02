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
        await _hub.Received(1).RegisterPoolAsync("meshtest", MeshtestPoolRtId);
        await _hub.Received(1).RegisterPoolAsync("energytest", EnergytestPoolRtId);
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
        await _hub.Received(1).UnregisterPoolAsync("meshtest", MeshtestPoolRtId);
    }

    [Test]
    public async Task RegisterPoolAsync_SameEntityTwice_DoesNotDuplicate()
    {
        var entity = Entity("acme", AcmePoolRtId, "default");

        await _service.RegisterPoolAsync(entity, CancellationToken.None);
        await _service.RegisterPoolAsync(entity, CancellationToken.None);

        await Assert.That(_service.GetPools().Count).IsEqualTo(1);
    }

    [Test]
    public async Task RegisterPoolAsync_HubConnected_FiresPerPoolReverseSync()
    {
        // Smoking-gun fix: late-arriving CR (KubeOps discovered it AFTER the
        // bulk reverse-sync in OperatorHubService.onReconnect already ran)
        // must trigger its own reverse-sync so the controller can lift any
        // DeploymentState drift for THIS pool. Without this call the late
        // CRs were silently stuck at whatever DeploymentState the previous
        // operator restart cycle had left them in.
        var entity = Entity("energytest", EnergytestPoolRtId, "cloud");

        await _service.RegisterPoolAsync(entity, CancellationToken.None);

        await _hub.Received(1).RegisterPoolAsync("energytest", EnergytestPoolRtId);
        await _hub.Received(1).ReportDeployedPoolAsync("energytest", EnergytestPoolRtId);
    }

    [Test]
    public async Task RegisterPoolAsync_HubDisconnected_SkipsPerPoolReverseSync()
    {
        // When the hub is down the RegisterPoolAsync call to the invoker is a
        // no-op anyway, and PoolService stores the CR locally so the next
        // reconnect's bulk reverse-sync (in OperatorHubService.onReconnect)
        // picks it up. Calling ReportDeployedPoolAsync now would be wasted
        // work and produce a confusing "skipping" log entry per CR.
        _hub.IsConnected.Returns(false);
        var entity = Entity("acme", AcmePoolRtId, "default");

        await _service.RegisterPoolAsync(entity, CancellationToken.None);

        await _hub.DidNotReceiveWithAnyArgs().ReportDeployedPoolAsync(default!, default!);
    }
}
