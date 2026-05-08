using Meshmakers.Octo.Communication.Operator.Options;
using Meshmakers.Octo.Communication.Operator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services;

public class OperatorHubServiceTests : IDisposable
{
    private const string TenantId = "acme";

    private readonly ICommunicationPoolManager _poolManager;
    private readonly OperatorHubService _service;

    public OperatorHubServiceTests()
    {
        _poolManager = Substitute.For<ICommunicationPoolManager>();
        _service = new OperatorHubService(
            NullLogger<OperatorHubService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new OperatorOptions()),
            NullLoggerFactory.Instance,
            _poolManager);
    }

    public void Dispose()
    {
        _service.Dispose();
        GC.SuppressFinalize(this);
    }

    [Test]
    public async Task TenantCreatedAsync_DelegatesToPoolManager()
    {
        await _service.TenantCreatedAsync(TenantId);

        await _poolManager.Received(1).CreateCommunicationPoolAsync(TenantId);
    }

    [Test]
    public async Task TenantCreatedAsync_PoolManagerThrows_ExceptionIsSwallowed()
    {
        _poolManager.CreateCommunicationPoolAsync(TenantId)
            .ThrowsAsync(new InvalidOperationException("boom"));

        await _service.TenantCreatedAsync(TenantId);

        await _poolManager.Received(1).CreateCommunicationPoolAsync(TenantId);
    }

    [Test]
    public async Task TenantDeletedAsync_DelegatesToPoolManager()
    {
        await _service.TenantDeletedAsync(TenantId);

        await _poolManager.Received(1).DeleteCommunicationPoolAsync(TenantId);
    }

    [Test]
    public async Task TenantDeletedAsync_PoolManagerThrows_ExceptionIsSwallowed()
    {
        _poolManager.DeleteCommunicationPoolAsync(TenantId)
            .ThrowsAsync(new InvalidOperationException("boom"));

        await _service.TenantDeletedAsync(TenantId);

        await _poolManager.Received(1).DeleteCommunicationPoolAsync(TenantId);
    }
}
