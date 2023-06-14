using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;
using PlugOperator.Entities;
using PlugOperator.Models;

namespace PlugOperator.Reconcilers;

public interface IPlugReconciler
{
    /// <summary>
    /// Reconciles the plugs for the plug pool resource.
    /// </summary>
    /// <param name="poolDescriptor">Meta data about the pool</param>
    /// <param name="plugPoolPlug">The pool plug to reconcile</param>
    /// <param name="entity">Plug pool entity for reconcile</param>
    Task ReconcileAsync(PoolDescriptor poolDescriptor, PlugPoolPlugDto plugPoolPlug, V1PlugPoolEntity entity);

    /// <summary>
    /// Deletes the plugs for the plug pool resource.
    /// </summary>
    /// <param name="k8Pool">Meta data about the pool</param>
    Task DeleteAsync(K8Pool k8Pool);
    
    /// <summary>
    /// Deletes the plugs for the plug pool resource.
    /// </summary>
    /// <param name="k8Pool">Meta data about the pool</param>
    /// <param name="plugPoolPlug">The pool plug to reconcile</param>
    Task DeleteAsync(K8Pool k8Pool, PlugPoolPlugDto plugPoolPlug);
}