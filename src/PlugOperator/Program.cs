using k8s;
using KubeOps.Operator;
using PlugOperator.Reconcilers;
using PlugOperator.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IKubernetes>(sp =>
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
});
builder.Services.AddSingleton<IPlugPoolService, PlugPoolService>();
builder.Services.AddSingleton<IPlugReconciler, PlugReconciler>();

var app = builder.Build();
app.UseKubernetesOperator();
await app.RunOperatorAsync(args);
