using k8s.Models;
using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Finalizer;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meshmakers.Octo.Communication.Operator.Tests.Finalizer;

public class DeploymentSiteFinalizerTests
{
    [Test]
    public async Task FinalizeAsync_ReturnsSuccessWithSameEntity()
    {
        var finalizer = new DeploymentSiteFinalizer(NullLogger<DeploymentSiteFinalizer>.Instance);
        var entity = new V1DeploymentSiteEntity
        {
            Metadata = new V1ObjectMeta { Name = "test-pool" }
        };

        var result = await finalizer.FinalizeAsync(entity, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Entity).IsEqualTo(entity);
    }
}
