namespace PlugOperator.Common;

public static class DictionaryExtensions
{
    public static string AsLabelSelector(this Dictionary<string, string> labels)
    {
        return string.Join(",", labels.Select(x => $"{x.Key}={x.Value}"));
    }
}