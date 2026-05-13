using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Communication.Operator.Reconcilers;

/// <summary>
/// Drives Helm-based workload deployments for Adapters and Applications.
/// </summary>
public interface IWorkloadReconciler
{
    /// <summary>
    /// Materializes any secret-flagged value overrides into a Kubernetes
    /// <c>Secret</c>, registers the chart repository if needed, assembles
    /// the effective Helm values and runs <c>helm upgrade --install</c>.
    /// </summary>
    Task DeployAsync(WorkloadDeployedDto workload, CancellationToken cancellationToken);

    /// <summary>
    /// Runs <c>helm uninstall</c> for the matching release and removes the
    /// operator-owned Kubernetes <c>Secret</c> created at deploy time (if any).
    /// </summary>
    Task UndeployAsync(WorkloadUndeployedDto workload, CancellationToken cancellationToken);
}
