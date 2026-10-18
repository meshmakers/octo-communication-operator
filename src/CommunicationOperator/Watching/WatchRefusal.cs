using k8s;
using k8s.Autorest;

namespace Meshmakers.Octo.Communication.Operator.Watching;

/// <summary>
///     Recognises the API server's refusal of a watch that needs etcd watch progress notifications (AB#6898):
///     <c>a watch stream was requested by the client but the required storage feature RequestWatchProgress is disabled</c>
///     (HTTP 500). Matched on the message because the status carries no dedicated reason.
/// </summary>
public static class WatchRefusal
{
    private const string FeatureName = "RequestWatchProgress";
    private const string FeatureSentence = "required storage feature";

    public static bool IsRefusal(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (Matches(current.Message))
            {
                return true;
            }

            switch (current)
            {
                case KubernetesException { Status: { } status } when Matches(status.Message):
                case HttpOperationException { Response: { } response } when Matches(response.Content):
                    return true;
            }
        }

        return false;
    }

    private static bool Matches(string? text)
    {
        return !string.IsNullOrEmpty(text)
               && (text.Contains(FeatureName, StringComparison.OrdinalIgnoreCase)
                   || text.Contains(FeatureSentence, StringComparison.OrdinalIgnoreCase));
    }
}
