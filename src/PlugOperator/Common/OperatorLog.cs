namespace PlugOperator.Common;

/// <summary>
/// Provides statically typed logging messages for the application
/// </summary>
public static partial class OperatorLog
{
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "Creating deployment {DeploymentName} for pool {PoolName} in namespace {NamespaceName}"
    )]
    public static partial void CreatingDeployment(this ILogger logger, string deploymentName, string poolName,
        string namespaceName);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "Deleting deployment {DeploymentName} for pool {PoolName} in namespace {NamespaceName}"
    )]
    public static partial void DeletingDeployment(this ILogger logger, string deploymentName, string poolName,
        string namespaceName);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Information,
        Message = "Creating service {ServiceName} for pool {PoolName} in namespace {NamespaceName}"
    )]
    public static partial void CreatingService(this ILogger logger, string serviceName, string poolName, string namespaceName);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Information,
        Message = "Deleting service {ServiceName} for pool {PoolName} in namespace {NamespaceName}"
    )]
    public static partial void DeletingService(this ILogger logger, string serviceName, string poolName, string namespaceName);
    
    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Information,
        Message = "Creating stateful set {StatefulSetName} for workspace {WorkspaceName} in namespace {NamespaceName}"
    )]
    public static partial void CreatingStatefulSet(this ILogger logger, string statefulSetName, string workspaceName,
        string namespaceName);

    [LoggerMessage(
        EventId = 1006,
        Level = LogLevel.Information,
        Message =
            "Scaling deployment {DeploymentName} for workspace {WorkspaceName} in namespace {NamespaceName} to {Replicas} replicas"
    )]
    public static partial void ScalingDeployment(this ILogger logger, string deploymentName, string workspaceName, string namespaceName,
        int replicas);
}