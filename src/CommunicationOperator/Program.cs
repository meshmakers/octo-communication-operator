using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using k8s;
using KubeOps.KubernetesClient;
using KubeOps.Operator;
using KubeOps.Operator.Web.Builder;
using KubeOps.Operator.Web.Certificates;
using Meshmakers.Octo.Communication.Operator.Options;
using Meshmakers.Octo.Communication.Operator.Reconcilers;
using Meshmakers.Octo.Communication.Operator.Services;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using NLog;
using NLog.Web;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

// Needed if config directory needs to be created newly
//Environment.SetEnvironmentVariable("CFSSL_EXECUTABLES_PATH", "/Users/gerald/RiderProjects/meshmakers/octo-communication-operator/tools");

// NLog: set up the logger first to catch all errors
var nLogFactory = LogManager.Setup().RegisterNLogWeb().LoadConfigurationFromFile("nlog.config").LogFactory;
var logger = nLogFactory.GetCurrentClassLogger();

#if DEBUG || DEBUGL
// Bootstrap configuration just to read OperatorOptions before the DI graph
// exists — Kestrel and KubeOps need the dev webhook host/port up front so
// they can bind and so KubeOps can register the MutatingWebhookConfiguration
// with the right URL.
var bootstrapConfig = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true)
    .AddEnvironmentVariables(prefix: "OCTO_")
    .Build();
var bootstrapOperatorOptions = bootstrapConfig.GetSection("Operator").Get<OperatorOptions>() ?? new OperatorOptions();

string ip = !string.IsNullOrWhiteSpace(bootstrapOperatorOptions.DevWebhookHost)
    ? bootstrapOperatorOptions.DevWebhookHost
    : GetFirstNonLoopbackIPv4()
        ?? throw new InvalidOperationException(
            "Could not auto-detect a non-loopback IPv4 address. Set Operator:DevWebhookHost in appsettings.Development.json or via OCTO_OPERATOR__DEVWEBHOOKHOST.");
ushort port = bootstrapOperatorOptions.DevWebhookPort;
logger.Info("Dev webhook endpoint: https://{Ip}:{Port}", ip, port);

using CertificateGenerator generator = new CertificateGenerator(ip);
using X509Certificate2 cert = generator.Server.CopyServerCertWithPrivateKey();

static string? GetFirstNonLoopbackIPv4()
{
    return NetworkInterface.GetAllNetworkInterfaces()
        .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                      && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
        .Select(ua => ua.Address)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(a)
                    && !a.ToString().StartsWith("169.254.")) // skip APIPA / link-local
        .Select(a => a.ToString())
        .FirstOrDefault();
}
#endif

try
{
    logger.Debug("init main");

    var builder = WebApplication.CreateBuilder(args);

#if DEBUG || DEBUGL
    builder.WebHost.ConfigureKestrel(serverOptions =>
    {
        serverOptions.Listen(System.Net.IPAddress.Any, port,
             listenOptions => { listenOptions.UseHttps(cert); });
    });
#endif

    // NLog: Setup NLog for Dependency injection
    builder.Logging.ClearProviders();
    builder.Logging.SetMinimumLevel(LogLevel.Trace);
    builder.Host.UseNLog();
    
    builder.Services
        .AddKubernetesOperator()
        .RegisterComponents()
#if DEBUG || DEBUGL
        .UseCertificateProvider(port, ip, generator)
#endif
        ;

    builder.Services.AddHealthChecks();

    builder.Services
        .AddControllers();

    builder.Services.Configure<OperatorOptions>(builder.Configuration.GetSection("Operator"));
    builder.Services.AddSingleton<IPoolService, PoolService>();
    builder.Services.AddSingleton<IAdapterReconciler, AdapterReconciler>();
    builder.Services.AddSingleton<IKubernetesClient, KubernetesClient>();
    builder.Services.AddSingleton<IKubernetes>(_ =>
    {
        KubernetesClientConfiguration config;
        try
        {
            config = KubernetesClientConfiguration.InClusterConfig();
        }
        catch
        {
            // Fall back to kubeconfig for local development
            config = KubernetesClientConfiguration.BuildConfigFromConfigFile();
        }
        return new Kubernetes(config);
    });
    builder.Services.AddSingleton<ICommunicationPoolKubernetesGateway, CommunicationPoolKubernetesGateway>();
    builder.Services.AddSingleton<ICommunicationPoolManager, CommunicationPoolManager>();
    builder.Services.AddSingleton<IOperatorHubClientFactory, OperatorHubClientFactory>();
    builder.Services.AddHostedService<OperatorHubService>();
    builder.Services.AddScoped<IDiagnosticsService, DiagnosticsService>();

    var app = builder.Build();

    app.UseRouting();
    app.UseDeveloperExceptionPage();
    app.MapControllers();


    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready")
    });

    app.MapHealthChecks("/health/live", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("live")
    });

    await app.RunAsync();
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