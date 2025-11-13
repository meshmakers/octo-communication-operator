namespace Meshmakers.Octo.Communication.Operator.Models;

public class PoolDescriptor : K8Pool
{
    public string ControllerUri { get; set; } = string.Empty;
    public string BrokerHost { get; set; } = string.Empty;
    public string BrokerVirtualHost { get; set; } = string.Empty;
    public int BrokerPort { get; set; } = 5672;

    public string InstancePrefix { get; set; } = string.Empty;
    
    public bool IgnoreCertificateValidation { get; set; }
}