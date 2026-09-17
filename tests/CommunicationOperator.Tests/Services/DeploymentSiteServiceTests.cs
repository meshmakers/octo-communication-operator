using k8s.Models;
using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services;

public class DeploymentSiteServiceTests
{
    private readonly IOperatorHubInvoker _hub = Substitute.For<IOperatorHubInvoker>();
    private readonly DeploymentSiteService _service;

    private const string MeshtestDeploymentSiteRtId = "65d5c447b420da3fb12381a1";
    private const string EnergytestDeploymentSiteRtId = "65d5c447b420da3fb12381a2";
    private const string AcmeDeploymentSiteRtId = "65d5c447b420da3fb12381a3";

    public DeploymentSiteServiceTests()
    {
        _hub.IsConnected.Returns(true);
        _service = new DeploymentSiteService(NullLogger<DeploymentSiteService>.Instance, _hub);
    }

    private static V1DeploymentSiteEntity Entity(string tenantId, string deploymentSiteRtId, string deploymentSiteName) =>
        new()
        {
            Metadata = new V1ObjectMeta { Name = $"{tenantId}-{deploymentSiteRtId}", NamespaceProperty = "octo" },
            Spec = new V1DeploymentSiteEntity.V1DeploymentSiteEntitySpec
            {
                TenantId = tenantId,
                DeploymentSiteRtId = deploymentSiteRtId,
            },
            Status = new V1DeploymentSiteEntity.V1DeploymentSiteEntityStatus()
        };

    [Test]
    public async Task GetDeploymentSites_TwoTenantsSameDeploymentSiteName_KeepsBoth()
    {
        // Regression: a central operator typically manages multiple
        // DeploymentSite CRs whose deploymentSiteName collides (every tenant has
        // its own `cloud` deployment site). The local deployment site dict used to be keyed by
        // deploymentSiteName alone, so the second-reconciled CR overwrote the first.
        // After a SignalR reconnect (e.g. controller restart) the
        // operator's replay loop iterated GetDeploymentSites() and only
        // re-registered one of the N colliding deployment sites — workload deploys
        // for every other tenant then had no operator owning the deployment site and
        // either went nowhere or got mis-routed to whichever operator
        // happened to claim the bare deploymentSiteName. Keying by (tenantId,
        // deploymentSiteRtId) keeps both entries.
        var meshtest = Entity("meshtest", MeshtestDeploymentSiteRtId, "cloud");
        var energytest = Entity("energytest", EnergytestDeploymentSiteRtId, "cloud");

        await _service.RegisterDeploymentSiteAsync(meshtest, CancellationToken.None);
        await _service.RegisterDeploymentSiteAsync(energytest, CancellationToken.None);

        var deploymentSites = _service.GetDeploymentSites();
        await Assert.That(deploymentSites.Count).IsEqualTo(2);
        await Assert.That(deploymentSites.Any(p => p.Entity.Spec.TenantId == "meshtest")).IsTrue();
        await Assert.That(deploymentSites.Any(p => p.Entity.Spec.TenantId == "energytest")).IsTrue();
        await _hub.Received(1).RegisterDeploymentSiteAsync("meshtest", MeshtestDeploymentSiteRtId);
        await _hub.Received(1).RegisterDeploymentSiteAsync("energytest", EnergytestDeploymentSiteRtId);
    }

    [Test]
    public async Task UnregisterDeploymentSiteAsync_OnlyRemovesTheTargetedTenantDeploymentSite()
    {
        // A consequence of the keying fix: unregister must be tenant-scoped
        // too, otherwise deleting one tenant's `cloud` CR would silently
        // remove the sibling tenant's still-active deployment site from the local dict.
        var meshtest = Entity("meshtest", MeshtestDeploymentSiteRtId, "cloud");
        var energytest = Entity("energytest", EnergytestDeploymentSiteRtId, "cloud");

        await _service.RegisterDeploymentSiteAsync(meshtest, CancellationToken.None);
        await _service.RegisterDeploymentSiteAsync(energytest, CancellationToken.None);

        await _service.UnRegisterDeploymentSiteAsync(meshtest);

        var deploymentSites = _service.GetDeploymentSites();
        await Assert.That(deploymentSites.Count).IsEqualTo(1);
        await Assert.That(deploymentSites.Single().Entity.Spec.TenantId).IsEqualTo("energytest");
        await _hub.Received(1).UnregisterDeploymentSiteAsync("meshtest", MeshtestDeploymentSiteRtId);
    }

    [Test]
    public async Task RegisterDeploymentSiteAsync_SameEntityTwice_DoesNotDuplicate()
    {
        var entity = Entity("acme", AcmeDeploymentSiteRtId, "default");

        await _service.RegisterDeploymentSiteAsync(entity, CancellationToken.None);
        await _service.RegisterDeploymentSiteAsync(entity, CancellationToken.None);

        await Assert.That(_service.GetDeploymentSites().Count).IsEqualTo(1);
    }

    [Test]
    public async Task RegisterDeploymentSiteAsync_HubConnected_FiresPerDeploymentSiteReverseSync()
    {
        // Smoking-gun fix: late-arriving CR (KubeOps discovered it AFTER the
        // bulk reverse-sync in OperatorHubService.onReconnect already ran)
        // must trigger its own reverse-sync so the controller can lift any
        // DeploymentState drift for THIS deployment site. Without this call the late
        // CRs were silently stuck at whatever DeploymentState the previous
        // operator restart cycle had left them in.
        var entity = Entity("energytest", EnergytestDeploymentSiteRtId, "cloud");

        await _service.RegisterDeploymentSiteAsync(entity, CancellationToken.None);

        await _hub.Received(1).RegisterDeploymentSiteAsync("energytest", EnergytestDeploymentSiteRtId);
        await _hub.Received(1).ReportDeployedDeploymentSiteAsync("energytest", EnergytestDeploymentSiteRtId);
    }

    [Test]
    public async Task RegisterDeploymentSiteAsync_HubDisconnected_SkipsPerDeploymentSiteReverseSync()
    {
        // When the hub is down the RegisterDeploymentSiteAsync call to the invoker is a
        // no-op anyway, and DeploymentSiteService stores the CR locally so the next
        // reconnect's bulk reverse-sync (in OperatorHubService.onReconnect)
        // picks it up. Calling ReportDeployedDeploymentSiteAsync now would be wasted
        // work and produce a confusing "skipping" log entry per CR.
        _hub.IsConnected.Returns(false);
        var entity = Entity("acme", AcmeDeploymentSiteRtId, "default");

        await _service.RegisterDeploymentSiteAsync(entity, CancellationToken.None);

        await _hub.DidNotReceiveWithAnyArgs().ReportDeployedDeploymentSiteAsync(default!, default!);
    }
}
