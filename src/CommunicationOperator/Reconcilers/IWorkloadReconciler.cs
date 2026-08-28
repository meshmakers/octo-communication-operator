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

    /// <summary>
    /// Scales the release's Deployments to the requested replica count via a plain
    /// Kubernetes patch — no helm involved, so the release history is untouched and the
    /// operation completes in seconds (AB#4917, on-demand lifecycle AB#4914).
    /// Returns the number of Deployments patched; 0 means the release has no
    /// Deployments (not deployed, or already uninstalled) and the caller should
    /// report a failure.
    /// </summary>
    Task<int> ScaleAsync(ScaleWorkloadDto workload, CancellationToken cancellationToken);
}
