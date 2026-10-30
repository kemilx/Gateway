using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace SyntheticTelemetry;

internal sealed class SyntheticOtlpLogProcessor : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord data)
    {
        data.TraceId = default;
        data.SpanId = default;
        data.TraceFlags = ActivityTraceFlags.None;
        data.TraceState = null;
        data.FormattedMessage = null;
        data.Exception = null;
    }
}
