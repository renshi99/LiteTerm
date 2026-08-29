using LiteTerm.Core.Logs;

namespace LiteTerm.Tests;

public sealed class ServerLogEntryTests
{
    [Fact]
    public void Normalize_TrimsNameAndNormalizesAbsolutePosixPath()
    {
        var entry = new ServerLogEntry(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "  应用日志  ",
            "/var/log/../app//application.log");

        Assert.Equal(
            entry with { Name = "应用日志", RemotePath = "/var/app/application.log" },
            entry.Normalize());
    }

    [Theory]
    [InlineData("relative/application.log")]
    [InlineData("../application.log")]
    [InlineData("")]
    public void Normalize_RejectsNonAbsoluteOrEmptyPath(string path)
    {
        var entry = new ServerLogEntry(Guid.NewGuid(), Guid.NewGuid(), "日志", path);

        Assert.Throws<ArgumentException>(() => entry.Normalize());
    }

    [Fact]
    public void Normalize_RejectsControlCharactersInPath()
    {
        var entry = new ServerLogEntry(Guid.NewGuid(), Guid.NewGuid(), "日志", "/var/log/app\u001b.log");

        Assert.Throws<ArgumentException>(() => entry.Normalize());
    }

    [Fact]
    public void NormalizeAll_RejectsDifferentServerAndDuplicateNames()
    {
        var serverId = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => ServerLogEntry.NormalizeAll(serverId,
        [
            new ServerLogEntry(Guid.NewGuid(), Guid.NewGuid(), "日志", "/var/log/app.log")
        ]));

        Assert.Throws<ArgumentException>(() => ServerLogEntry.NormalizeAll(serverId,
        [
            new ServerLogEntry(Guid.NewGuid(), serverId, "App Log", "/var/log/app.log"),
            new ServerLogEntry(Guid.NewGuid(), serverId, "app log", "/var/log/other.log")
        ]));
    }

    [Fact]
    public void NormalizeAll_AcceptsMaximumCountAndRejectsOneMore()
    {
        var serverId = Guid.NewGuid();
        var entries = Enumerable.Range(1, ServerLogEntry.MaximumCountPerServer + 1)
            .Select(index => new ServerLogEntry(
                Guid.NewGuid(), serverId, $"日志 {index}", $"/var/log/app-{index}.log"))
            .ToArray();

        Assert.Equal(
            ServerLogEntry.MaximumCountPerServer,
            ServerLogEntry.NormalizeAll(serverId, entries[..ServerLogEntry.MaximumCountPerServer]).Count);
        Assert.Throws<ArgumentException>(() => ServerLogEntry.NormalizeAll(serverId, entries));
    }
}
