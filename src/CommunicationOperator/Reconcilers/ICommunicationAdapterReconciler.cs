using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Models;

namespace Meshmakers.Octo.Communication.Operator.Reconcilers;

public interface ICommunicationAdapterReconciler
{
    /// <summary>
    /// Reconciles the communication adapter for the pool resource.
    /// </summary>
    /// <param name="poolDescriptor">Meta data about the pool</param>
    /// <param name="poolAdapter">The pool communication adapter to reconcile</param>
    /// <param name="entity">Communication pool entity for reconcile</param>
    Task ReconcileAsync(PoolDescriptor poolDescriptor, PoolCommunicationAdapterDto poolAdapter, V1CommunicationPoolEntity entity);

    /// <summary>
    /// Deletes the communication adapter for the pool resource.
    /// </summary>
    /// <param name="k8Pool">Meta data about the pool</param>
    Task DeleteAsync(K8Pool k8Pool);
    
    /// <summary>
    /// Deletes the communication adapter for the pool resource.
    /// </summary>
    /// <param name="k8Pool">Meta data about the pool</param>
    /// <param name="poolAdapter">The communication adapter to reconcile</param>
    Task DeleteAsync(K8Pool k8Pool, PoolCommunicationAdapterDto poolAdapter);
}