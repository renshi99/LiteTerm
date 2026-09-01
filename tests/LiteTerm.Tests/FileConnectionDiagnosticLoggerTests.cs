using System.Text.Json;
using LiteTerm.Core.Connections;
using LiteTerm.Infrastructure.Diagnostics;

namespace LiteTerm.Tests;

public sealed class FileConnectionDiagnosticLoggerTests
{
    [Fact]
    public async Task WriteAsync_WritesUtf8WithoutBomAndOnlyDiagnosticFields()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"LiteTerm-diagnostics-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "connection.jsonl");
        try
        {
            var logger = new FileConnectionDiagnosticLogger(path);
            await logger.WriteAsync(new ConnectionDiagnosticEntry(
                DateTimeOffset.Parse("2026-09-01T00:00:00Z"),
                ConnectionProtocol.Ssh,
                ConnectionOperation.Connect,
                "connection_timeout",
                "Renci.SshNet.Common.SshOperationTimeoutException"));

            var bytes = await File.ReadAllBytesAsync(path);
            var text = await File.ReadAllTextAsync(path);

            Assert.False(bytes.Length >= 3
                && bytes[0] == 0xEF
                && bytes[1] == 0xBB
                && bytes[2] == 0xBF);
            Assert.Contains("connection_timeout", text, StringComparison.Ordinal);
            Assert.Contains("SshOperationTimeoutException", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Host", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Username", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Password", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Path", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WriteAsync_WhenMaximumSizeWasReached_RotatesOnePreviousFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"LiteTerm-diagnostics-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "connection.jsonl");
        try
        {
            var logger = new FileConnectionDiagnosticLogger(path, maximumFileBytes: 1);
            await logger.WriteAsync(CreateEntry("first"));
            await logger.WriteAsync(CreateEntry("second"));

            var previousText = await File.ReadAllTextAsync($"{path}.previous");
            var currentText = await File.ReadAllTextAsync(path);

            Assert.Contains("first", previousText, StringComparison.Ordinal);
            Assert.DoesNotContain("second", previousText, StringComparison.Ordinal);
            Assert.Contains("second", currentText, StringComparison.Ordinal);
            Assert.DoesNotContain("first", currentText, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WriteAsync_WhenCalledConcurrently_WritesCompleteJsonLines()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"LiteTerm-diagnostics-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "connection.jsonl");
        try
        {
            var logger = new FileConnectionDiagnosticLogger(path);
            var writes = Enumerable.Range(0, 20)
                .Select(index => logger.WriteAsync(CreateEntry($"failure_{index:D2}")).AsTask());

            await Task.WhenAll(writes);

            var lines = await File.ReadAllLinesAsync(path);
            Assert.Equal(20, lines.Length);
            foreach (var line in lines)
            {
                using var document = JsonDocument.Parse(line);
                Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
                Assert.True(document.RootElement.TryGetProperty("FailureCode", out _));
            }

            Assert.Equal(20, lines.Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static ConnectionDiagnosticEntry CreateEntry(string failureCode) => new(
        DateTimeOffset.Parse("2026-09-01T00:00:00Z"),
        ConnectionProtocol.Ssh,
        ConnectionOperation.Connect,
        failureCode,
        "TestException");
}
