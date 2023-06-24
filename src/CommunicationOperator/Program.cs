using k8s;
using KubeOps.KubernetesClient;
using KubeOps.Operator;
using Meshmakers.Octo.Communication.Operator.Controller;
using Meshmakers.Octo.Communication.Operator.Entities;
using Meshmakers.Octo.Communication.Operator.Finalizer;
using Meshmakers.Octo.Communication.Operator.Reconcilers;
using Meshmakers.Octo.Communication.Operator.Services;
using Meshmakers.Octo.Communication.Operator.Webhooks;
using NLog;
using NLog.Web;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

// Needed if config directory needs to be created newly
//Environment.SetEnvironmentVariable("CFSSL_EXECUTABLES_PATH", "/Users/gerald/RiderProjects/meshmakers/octo-communication-operator/tools");

// NLog: setup the logger first to catch all errors
var nLogFactory = LogManager.Setup().RegisterNLogWeb().LoadConfigurationFromFile("nlog.config").LogFactory;
var logger = nLogFactory.GetCurrentClassLogger();

try
{
    logger.Debug("init main");

    var builder = WebApplication.CreateBuilder(args);

    // NLog: Setup NLog for Dependency injection
    builder.Logging.ClearProviders();
    builder.Logging.SetMinimumLevel(LogLevel.Trace);
    builder.Host.UseNLog();

    builder.Services.AddSingleton<IKubernetes>(_ =>
    {
        // Since we can run inside or outside the cluster,
        // we need to set up a different configuration for each of the cases.
        var config = KubernetesClientConfiguration.IsInCluster() switch
        {
            true => KubernetesClientConfiguration.InClusterConfig(),
            false => KubernetesClientConfiguration.BuildConfigFromConfigFile()
        };

        return new Kubernetes(config);
    });

    builder.Services.AddKubernetesOperator((x) =>
        {
            x.HttpPort = 6000;
            x.HttpsPort = 6001;
            x.EnableAssemblyScanning = false;
        })
        .AddEntity<V1CommunicationPoolEntity>()
        .AddController<CommunicationPoolController>()
        .AddFinalizer<CommunicationPoolFinalizer>()
        .AddMutationWebhook<CommunicationPoolMutator>();
    
    builder.Services.AddSingleton<IPoolService, PoolService>();
    builder.Services.AddSingleton<ICommunicationAdapterReconciler, CommunicationAdapterReconciler>();
    builder.Services.AddSingleton<IKubernetesClient, KubernetesClient>();

    var app = builder.Build();
    app.UseKubernetesOperator();
    await app.RunOperatorAsync(args);
}
catch (Exception ex)
{
    //NLog: catch setup errors
    logger.Error(ex, "Stopped program because of exception");
    throw;
}
finally
{
    // Ensure to flush and stop internal timers/threads before application-exit (Avoid segmentation fault on Linux)
    LogManager.Shutdown();
}