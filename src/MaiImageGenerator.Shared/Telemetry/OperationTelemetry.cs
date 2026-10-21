using System.Diagnostics;

namespace MaiImageGenerator.Shared.Telemetry;

public static class OperationTelemetry
{
    public static string? CurrentTraceParent => Activity.Current?.Id;

    public static Activity StartConsumerActivity(string operationName, string? traceParent)
    {
        var activity = new Activity(operationName);
        if (!string.IsNullOrWhiteSpace(traceParent))
        {
            activity.SetParentId(traceParent);
        }

        return activity.Start();
    }
}
