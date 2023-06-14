namespace PlugOperator.Models;

public class PoolDescriptor : K8Pool
{
    public string PlugControllerUri { get; set; } = string.Empty;
    public string BrokerHost { get; set; } = string.Empty;
    public string BrokerVirtualHost { get; set; } = string.Empty;
    public int BrokerPort { get; set; } = 5672;
}