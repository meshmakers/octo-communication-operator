using Meshmakers.Octo.Communication.Operator.Entities;

namespace Meshmakers.Octo.Communication.Operator.Models;

/// <summary>
/// In-memory record of a <c>CommunicationPool</c> CR the operator currently
/// owns. Used to remember which pools to (re-)register on the operator hub
/// after a reconnect, and which to release when the CR is deleted.
/// </summary>
public class Pool(K8Pool descriptor, V1CommunicationPoolEntity entity)
{
    public K8Pool Descriptor { get; } = descriptor;
    public V1CommunicationPoolEntity Entity { get; } = entity;

    /// <summary>
    /// True once the operator has invoked
    /// <see cref="Meshmakers.Octo.Communication.Contracts.Hubs.IOperatorHub.RegisterPoolAsync"/>
    /// on the controller for this pool. Reset to <c>false</c> on connection
    /// drops so the reconnect handler knows to re-register.
    /// </summary>
    public bool IsRegistered { get; set; }
}
