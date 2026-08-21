using System.Text;
using LiteTerm.Core.Terminal;

namespace LiteTerm.Tests;

public sealed class BoundedTerminalOutputBufferTests
{
    [Fact]
    public void Enqueue_WhenCapacityIsExceeded_DropsOldestChunksAndRetainsLatestData()
    {
        var buffer = new BoundedTerminalOutputBuffer(5);

        buffer.Enqueue(Encoding.UTF8.GetBytes("abc"));
        buffer.Enqueue(Encoding.UTF8.GetBytes("de"));
        buffer.Enqueue(Encoding.UTF8.GetBytes("f"));

        var batch = buffer.DequeueUpTo(10);

        Assert.Equal("def", Encoding.UTF8.GetString(batch.Data));
        Assert.Equal(3, batch.DroppedBytes);
        Assert.Equal(0, buffer.BufferedBytes);
    }

    [Fact]
    public void DequeueUpTo_WhenBatchIsLimited_PreservesTheRemainingDataOrder()
    {
        var buffer = new BoundedTerminalOutputBuffer(16);
        buffer.Enqueue(Encoding.UTF8.GetBytes("abc"));
        buffer.Enqueue(Encoding.UTF8.GetBytes("def"));
        buffer.Enqueue(Encoding.UTF8.GetBytes("ghi"));

        var first = buffer.DequeueUpTo(4);
        var second = buffer.DequeueUpTo(4);
        var third = buffer.DequeueUpTo(4);

        Assert.Equal("abcd", Encoding.UTF8.GetString(first.Data));
        Assert.Equal(0, first.DroppedBytes);
        Assert.Equal("efgh", Encoding.UTF8.GetString(second.Data));
        Assert.Equal(0, second.DroppedBytes);
        Assert.Equal("i", Encoding.UTF8.GetString(third.Data));
        Assert.Equal(0, third.DroppedBytes);
    }

    [Fact]
    public void Enqueue_WhenSingleChunkExceedsCapacity_RetainsItsLatestBytes()
    {
        var buffer = new BoundedTerminalOutputBuffer(3);

        buffer.Enqueue(Encoding.UTF8.GetBytes("abcdef"));

        var batch = buffer.DequeueUpTo(3);

        Assert.Equal("def", Encoding.UTF8.GetString(batch.Data));
        Assert.Equal(3, batch.DroppedBytes);
    }
}
