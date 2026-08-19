namespace LiteTerm.Core.Connections;

public sealed class TerminalOutputEventArgs(byte[] data) : EventArgs
{
    public ReadOnlyMemory<byte> Data { get; } = data;
}
