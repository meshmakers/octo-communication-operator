using k8s.Models;
using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Finalizer;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meshmakers.Octo.Communication.Operator.Tests.Finalizer;

public class CommunicationPoolFinalizerTests
{
    [Test]
    public async Task FinalizeAsync_ReturnsSuccessWithSameEntity()
    {
        var finalizer = new CommunicationPoolFinalizer(NullLogger<CommunicationPoolFinalizer>.Instance);
        var entity = new V1CommunicationPoolEntity
        {
            Metadata = new V1ObjectMeta { Name = "test-pool" }
        };

        var result = await finalizer.FinalizeAsync(entity, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Entity).IsEqualTo(entity);
    }
}
