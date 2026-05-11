using k8s.Autorest;
using k8s.Models;
using KubeOps.KubernetesClient;
using Meshmakers.Octo.Communication.Operator.Controller;
using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Communication.Operator.Tests.Controller;

public class CommunicationPoolControllerTests
{
    private const string TenantId = "acme";
    private const string PoolName = "default";
    private const string CrName = "acme-default";

    private readonly IKubernetesClient _client = Substitute.For<IKubernetesClient>();
    private readonly IPoolService _poolService = Substitute.For<IPoolService>();
    private readonly CommunicationPoolController _controller;

    public CommunicationPoolControllerTests()
    {
        _controller = new CommunicationPoolController(
            NullLogger<CommunicationPoolController>.Instance,
            _client,
            _poolService);
    }

    private static V1CommunicationPoolEntity CreateEntity() =>
        new()
        {
            Metadata = new V1ObjectMeta { Name = CrName, NamespaceProperty = "octo" },
            Spec = new V1CommunicationPoolEntity.V1CommunicationPoolEntitySpec
            {
                TenantId = TenantId,
                PoolName = PoolName
            },
            Status = new V1CommunicationPoolEntity.V1CommunicationPoolEntityStatus()
        };

    [Test]
    public async Task ReconcileAsync_HappyPath_UpdatesStatusAndRegistersPool()
    {
        var entity = CreateEntity();
        _client.UpdateStatusAsync(entity, Arg.Any<CancellationToken>()).Returns(entity);

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await _poolService.Received(1).RegisterPoolAsync(entity, Arg.Any<CancellationToken>());
        await _client.Received(2).UpdateStatusAsync(entity, Arg.Any<CancellationToken>());
        await Assert.That(entity.Status.CommunicationStatus).IsEqualTo("Registered");
    }

    [Test]
    public async Task ReconcileAsync_RegisterThrows_StatusUpdatedToFailureAndFailureReturned()
    {
        var entity = CreateEntity();
        _client.UpdateStatusAsync(entity, Arg.Any<CancellationToken>()).Returns(entity);
        _poolService
            .RegisterPoolAsync(entity, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await _controller.ReconcileAsync(entity, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(entity.Status.CommunicationStatus).IsEqualTo("Failed: boom");
    }

    [Test]
    public async Task DeletedAsync_HappyPath_UnregistersAndDoesNotTouchStatus()
    {
        var entity = CreateEntity();

        var result = await _controller.DeletedAsync(entity, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await _poolService.Received(1).UnRegisterPoolAsync(entity);
        await _client.DidNotReceive().UpdateStatusAsync(
            Arg.Any<V1CommunicationPoolEntity>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeletedAsync_UnregisterFails_DoesNotTouchStatusAndReturnsFailure()
    {
        var entity = CreateEntity();
        _poolService
            .UnRegisterPoolAsync(entity)
            .ThrowsAsync(new InvalidOperationException("hub gone"));

        var result = await _controller.DeletedAsync(entity, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsFalse();
        await _client.DidNotReceive().UpdateStatusAsync(
            Arg.Any<V1CommunicationPoolEntity>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeletedAsync_CrAlreadyGone_StillSucceedsWithoutCrashing()
    {
        // Regression: previously the controller called UpdateStatusAsync from DeletedAsync,
        // which returned 404 once the CR was already gone and made KubeOps retry the
        // delete reconcile forever. The fix removes that call entirely, so even if a
        // status-update path were to be reintroduced, this test pins the no-status-update
        // contract for the delete callback.
        var entity = CreateEntity();
        _client
            .UpdateStatusAsync(Arg.Any<V1CommunicationPoolEntity>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpOperationException("404 Not Found"));

        var result = await _controller.DeletedAsync(entity, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await _client.DidNotReceive().UpdateStatusAsync(
            Arg.Any<V1CommunicationPoolEntity>(), Arg.Any<CancellationToken>());
    }
}
