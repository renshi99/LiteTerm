namespace LiteTerm.Core.Terminal;

/// <summary>
/// Stores pending terminal bytes within a fixed memory budget.
/// </summary>
public sealed class BoundedTerminalOutputBuffer
{
    private readonly object _gate = new();
    private readonly Queue<Chunk> _chunks = new();
    private readonly int _capacityBytes;
    private long _bufferedBytes;
    private long _droppedBytes;

    public BoundedTerminalOutputBuffer(int capacityBytes)
    {
        if (capacityBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityBytes));
        }

        _capacityBytes = capacityBytes;
    }

    public long BufferedBytes
    {
        get
        {
            lock (_gate)
            {
                return _bufferedBytes;
            }
        }
    }

    public void Enqueue(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            if (data.Length >= _capacityBytes)
            {
                DropAllBufferedData();
                var retainedData = data[^_capacityBytes..].ToArray();
                _chunks.Enqueue(new Chunk(retainedData));
                _bufferedBytes = retainedData.Length;
                _droppedBytes += data.Length - retainedData.Length;
                return;
            }

            while (_bufferedBytes + data.Length > _capacityBytes && _chunks.TryDequeue(out var discarded))
            {
                _bufferedBytes -= discarded.RemainingBytes;
                _droppedBytes += discarded.RemainingBytes;
            }

            var copy = data.ToArray();
            _chunks.Enqueue(new Chunk(copy));
            _bufferedBytes += copy.Length;
        }
    }

    public TerminalOutputBatch DequeueUpTo(int maximumBytes)
    {
        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        lock (_gate)
        {
            var droppedBytes = _droppedBytes;
            _droppedBytes = 0;

            if (_bufferedBytes == 0)
            {
                return new TerminalOutputBatch([], droppedBytes);
            }

            var batchLength = (int)Math.Min(_bufferedBytes, maximumBytes);
            var batch = new byte[batchLength];
            var written = 0;

            while (written < batchLength && _chunks.TryPeek(out var chunk))
            {
                var remaining = batchLength - written;
                var bytesToCopy = Math.Min(chunk.RemainingBytes, remaining);
                Buffer.BlockCopy(chunk.Data, chunk.Offset, batch, written, bytesToCopy);
                written += bytesToCopy;
                _bufferedBytes -= bytesToCopy;
                chunk.Offset += bytesToCopy;

                if (chunk.RemainingBytes == 0)
                {
                    _chunks.Dequeue();
                }
            }

            return new TerminalOutputBatch(batch, droppedBytes);
        }
    }

    private void DropAllBufferedData()
    {
        _droppedBytes += _bufferedBytes;
        _bufferedBytes = 0;
        _chunks.Clear();
    }

    private sealed class Chunk(byte[] data)
    {
        public byte[] Data { get; } = data;
        public int Offset { get; set; }
        public int RemainingBytes => Data.Length - Offset;
    }
}

public readonly record struct TerminalOutputBatch(byte[] Data, long DroppedBytes)
{
    public bool IsEmpty => Data.Length == 0;
}
