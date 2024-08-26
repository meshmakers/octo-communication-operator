using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Models;

namespace Meshmakers.Octo.Communication.Operator.Reconcilers;

public interface IAdapterReconciler
{
    /// <summary>
    /// Reconciles the communication adapter for the pool resource.
    /// </summary>
    /// <param name="pool">Pool management object</param>
    /// <param name="poolAdapter">The pool communication adapter to reconcile</param>
    /// <param name="entity">Communication pool entity for reconcile</param>
    Task ReconcileAsync(Pool pool, PoolCommunicationAdapterDto poolAdapter, V1CommunicationPoolEntity entity);

    /// <summary>
    /// Deletes the communication adapter for the pool resource.
    /// </summary>
    /// <param name="k8Pool">Pool metadata</param>
    Task DeleteAsync(K8Pool k8Pool);
    
    /// <summary>
    /// Deletes the communication adapter for the pool resource.
    /// </summary>
    /// <param name="pool">Pool management object</param>
    /// <param name="poolAdapter">The communication adapter to reconcile</param>
    Task DeleteAsync(Pool pool, PoolCommunicationAdapterDto poolAdapter);
}