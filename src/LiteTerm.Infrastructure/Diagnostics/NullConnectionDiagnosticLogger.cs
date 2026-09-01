using LiteTerm.Core.Connections;

namespace LiteTerm.Infrastructure.Diagnostics;

internal sealed class NullConnectionDiagnosticLogger : IConnectionDiagnosticLogger
{
    public static NullConnectionDiagnosticLogger Instance { get; } = new();

    private NullConnectionDiagnosticLogger()
    {
    }

    public ValueTask WriteAsync(
        ConnectionDiagnosticEntry entry,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
