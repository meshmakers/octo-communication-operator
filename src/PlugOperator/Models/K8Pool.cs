namespace PlugOperator.Models;

public class K8Pool
{
    public string Namespace { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string PoolName { get; set; } = string.Empty;
}