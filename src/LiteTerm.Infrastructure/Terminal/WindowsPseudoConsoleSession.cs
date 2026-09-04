using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using LiteTerm.Core.Connections;
using LiteTerm.Core.Terminal;
using Microsoft.Win32.SafeHandles;

namespace LiteTerm.Infrastructure.Terminal;

/// <summary>
/// Hosts Windows PowerShell in a ConPTY instance so local tabs preserve terminal semantics.
/// </summary>
public sealed class WindowsPseudoConsoleSession : ILocalTerminalSession
{
    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(2);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TaskCompletionSource _disposeCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private NativeSession? _nativeSession;
    private CancellationTokenSource? _sessionCancellation;
    private Task? _outputTask;
    private Task<int>? _processExitTask;
    private int _disposeStarted;
    private int _running;
    private int _stopping;

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public event EventHandler<TerminalOutputEventArgs>? OutputReceived;
    public event EventHandler<LocalTerminalExitedEventArgs>? Exited;

    public async Task StartAsync(
        int columns,
        int rows,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposingOrDisposed, this);
        ValidateSize(columns, rows);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            throw new PlatformNotSupportedException("本地终端需要 Windows 10 1809 或更高版本。");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(IsDisposingOrDisposed, this);
            if (IsRunning)
            {
                return;
            }

            await StopCoreAsync().ConfigureAwait(false);
            var nativeSession = await Task.Run(
                () => NativeSession.Create(columns, rows),
                cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Run(nativeSession.TerminateAndDispose).ConfigureAwait(false);
                }
                finally
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            var sessionCancellation = new CancellationTokenSource();
            _nativeSession = nativeSession;
            _sessionCancellation = sessionCancellation;
            Volatile.Write(ref _stopping, 0);
            Volatile.Write(ref _running, 1);
            _outputTask = Task.Run(() => ReadOutput(
                nativeSession.OutputStream,
                sessionCancellation.Token));
            _processExitTask = Task.Run(nativeSession.WaitForExit);
            _ = ObserveProcessExitAsync(nativeSession, _processExitTask);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SendAsync(
        string data,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposingOrDisposed, this);
        ArgumentNullException.ThrowIfNull(data);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsRunning || _nativeSession is null)
            {
                throw new InvalidOperationException("本地终端尚未运行。");
            }

            var bytes = Encoding.UTF8.GetBytes(data);
            var input = _nativeSession.InputStream;
            await Task.Run(() =>
            {
                input.Write(bytes);
                input.Flush();
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Resize(int columns, int rows)
    {
        if (!IsRunning || columns <= 0 || rows <= 0)
        {
            return;
        }

        _nativeSession?.Resize(columns, rows);
    }

    public async Task StopAsync()
    {
        if (IsDisposingOrDisposed)
        {
            await _disposeCompletion.Task.ConfigureAwait(false);
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeStarted, 1, 0) == 0)
        {
            _ = DisposeCoreAsync();
        }

        return new ValueTask(_disposeCompletion.Task);
    }

    private bool IsDisposingOrDisposed => Volatile.Read(ref _disposeStarted) != 0;

    private async Task DisposeCoreAsync()
    {
        Exception? failure = null;
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            _gate.Dispose();
            if (failure is null)
            {
                _disposeCompletion.TrySetResult();
            }
            else
            {
                _disposeCompletion.TrySetException(failure);
            }
        }
    }

    private async Task StopCoreAsync()
    {
        var nativeSession = _nativeSession;
        if (nativeSession is null)
        {
            Volatile.Write(ref _running, 0);
            return;
        }

        Volatile.Write(ref _stopping, 1);
        Volatile.Write(ref _running, 0);
        var cancellation = _sessionCancellation;
        var outputTask = _outputTask;
        var processExitTask = _processExitTask;
        _nativeSession = null;
        _sessionCancellation = null;
        _outputTask = null;
        _processExitTask = null;

        try
        {
            nativeSession.InputStream.Dispose();
            var closePseudoConsoleTask = Task.Run(nativeSession.ClosePseudoConsole);
            if (processExitTask is not null)
            {
                try
                {
                    await processExitTask.WaitAsync(ProcessExitTimeout).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    nativeSession.Terminate();
                    await processExitTask.WaitAsync(ProcessExitTimeout).ConfigureAwait(false);
                }
            }

            await closePseudoConsoleTask.ConfigureAwait(false);
        }
        finally
        {
            cancellation?.Cancel();
            nativeSession.OutputStream.Dispose();
            if (outputTask is not null)
            {
                try
                {
                    await outputTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true)
                {
                    // Closing the owning tab or switching to SSH stops the output pump.
                }
                catch (IOException)
                {
                    // Closing the ConPTY output pipe is the normal way to unblock a pending read.
                }
                catch (ObjectDisposedException)
                {
                    // The output stream is disposed to unblock a synchronous anonymous-pipe read.
                }
            }

            cancellation?.Dispose();
            nativeSession.Dispose();
            Volatile.Write(ref _stopping, 0);
        }
    }

    private void ReadOutput(Stream output, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytesRead = output.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0)
            {
                return;
            }

            var data = buffer.AsSpan(0, bytesRead).ToArray();
            try
            {
                OutputReceived?.Invoke(this, new TerminalOutputEventArgs(data));
            }
            catch
            {
                // A UI subscriber must not terminate the ConPTY output pump.
            }
        }
    }

    private async Task ObserveProcessExitAsync(
        NativeSession nativeSession,
        Task<int> processExitTask)
    {
        try
        {
            var exitCode = await processExitTask.ConfigureAwait(false);
            if (!ReferenceEquals(_nativeSession, nativeSession))
            {
                return;
            }

            Volatile.Write(ref _running, 0);
            if (Volatile.Read(ref _stopping) == 0 && !IsDisposingOrDisposed)
            {
                try
                {
                    Exited?.Invoke(this, new LocalTerminalExitedEventArgs(exitCode));
                }
                catch
                {
                    // A UI subscriber must not turn process exit into an unobserved task failure.
                }
            }
        }
        catch
        {
            // StopAsync owns cleanup and reports failures to its caller.
        }
    }

    private static void ValidateSize(int columns, int rows)
    {
        if (columns is <= 0 or > short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(columns));
        }

        if (rows is <= 0 or > short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(rows));
        }
    }

    private sealed class NativeSession : IDisposable
    {
        private readonly object _pseudoConsoleLock = new();
        private IntPtr _pseudoConsole;

        private NativeSession(
            IntPtr pseudoConsole,
            FileStream inputStream,
            FileStream outputStream,
            SafeWaitHandle processHandle)
        {
            _pseudoConsole = pseudoConsole;
            InputStream = inputStream;
            OutputStream = outputStream;
            ProcessHandle = processHandle;
        }

        public FileStream InputStream { get; }
        public FileStream OutputStream { get; }
        private SafeWaitHandle ProcessHandle { get; }

        public static NativeSession Create(int columns, int rows)
        {
            SafeFileHandle? inputRead = null;
            SafeFileHandle? inputWrite = null;
            SafeFileHandle? outputRead = null;
            SafeFileHandle? outputWrite = null;
            SafeWaitHandle? processHandle = null;
            IntPtr pseudoConsole = IntPtr.Zero;
            IntPtr attributeList = IntPtr.Zero;
            try
            {
                NativeMethods.ThrowIfFalse(
                    NativeMethods.CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0));
                NativeMethods.ThrowIfFalse(
                    NativeMethods.CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0));

                var result = NativeMethods.CreatePseudoConsole(
                    new Coord(columns, rows),
                    inputRead,
                    outputWrite,
                    0,
                    out pseudoConsole);
                if (result < 0)
                {
                    Marshal.ThrowExceptionForHR(result);
                }

                inputRead.Dispose();
                inputRead = null;
                outputWrite.Dispose();
                outputWrite = null;

                nuint attributeListSize = 0;
                _ = NativeMethods.InitializeProcThreadAttributeList(
                    IntPtr.Zero,
                    1,
                    0,
                    ref attributeListSize);
                attributeList = Marshal.AllocHGlobal(checked((nint)attributeListSize));
                NativeMethods.ThrowIfFalse(NativeMethods.InitializeProcThreadAttributeList(
                    attributeList,
                    1,
                    0,
                    ref attributeListSize));
                NativeMethods.ThrowIfFalse(NativeMethods.UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    NativeMethods.ProcThreadAttributePseudoConsole,
                    pseudoConsole,
                    (nuint)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero));

                var startupInfo = new StartupInfoEx
                {
                    StartupInfo = new StartupInfo
                    {
                        Size = Marshal.SizeOf<StartupInfoEx>()
                    },
                    AttributeList = attributeList
                };
                var shell = GetShell();
                var commandLine = new StringBuilder($"\"{shell.Path}\"{shell.Arguments}");
                var workingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                NativeMethods.ThrowIfFalse(NativeMethods.CreateProcess(
                    shell.Path,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    NativeMethods.ExtendedStartupInfoPresent | NativeMethods.CreateUnicodeEnvironment,
                    IntPtr.Zero,
                    workingDirectory,
                    ref startupInfo,
                    out var processInformation));

                processHandle = new SafeWaitHandle(processInformation.Process, true);
                using var threadHandle = new SafeWaitHandle(processInformation.Thread, true);
                var inputStream = new FileStream(inputWrite, FileAccess.Write, 4096, false);
                inputWrite = null;
                var outputStream = new FileStream(outputRead, FileAccess.Read, 4096, false);
                outputRead = null;
                var session = new NativeSession(
                    pseudoConsole,
                    inputStream,
                    outputStream,
                    processHandle);
                pseudoConsole = IntPtr.Zero;
                processHandle = null;
                return session;
            }
            finally
            {
                if (attributeList != IntPtr.Zero)
                {
                    NativeMethods.DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                }

                if (pseudoConsole != IntPtr.Zero)
                {
                    NativeMethods.ClosePseudoConsole(pseudoConsole);
                }

                inputRead?.Dispose();
                inputWrite?.Dispose();
                outputRead?.Dispose();
                outputWrite?.Dispose();
                processHandle?.Dispose();
            }
        }

        public int WaitForExit()
        {
            var waitResult = NativeMethods.WaitForSingleObject(ProcessHandle, NativeMethods.Infinite);
            if (waitResult == NativeMethods.WaitFailed)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            NativeMethods.ThrowIfFalse(NativeMethods.GetExitCodeProcess(ProcessHandle, out var exitCode));
            return unchecked((int)exitCode);
        }

        public void Resize(int columns, int rows)
        {
            ValidateSize(columns, rows);
            lock (_pseudoConsoleLock)
            {
                if (_pseudoConsole == IntPtr.Zero)
                {
                    return;
                }

                var result = NativeMethods.ResizePseudoConsole(
                    _pseudoConsole,
                    new Coord(columns, rows));
                if (result < 0)
                {
                    Marshal.ThrowExceptionForHR(result);
                }
            }
        }

        public void Terminate()
        {
            if (ProcessHandle.IsInvalid || ProcessHandle.IsClosed)
            {
                return;
            }

            if (!NativeMethods.TerminateProcess(ProcessHandle, 1))
            {
                var error = Marshal.GetLastPInvokeError();
                if (NativeMethods.WaitForSingleObject(ProcessHandle, 0) != NativeMethods.WaitObject0)
                {
                    throw new Win32Exception(error);
                }
            }
        }

        public void ClosePseudoConsole()
        {
            lock (_pseudoConsoleLock)
            {
                if (_pseudoConsole == IntPtr.Zero)
                {
                    return;
                }

                NativeMethods.ClosePseudoConsole(_pseudoConsole);
                _pseudoConsole = IntPtr.Zero;
            }
        }

        public void TerminateAndDispose()
        {
            try
            {
                Terminate();
            }
            finally
            {
                Dispose();
            }
        }

        public void Dispose()
        {
            InputStream.Dispose();
            OutputStream.Dispose();
            ClosePseudoConsole();
            ProcessHandle.Dispose();
        }

        private static ShellInfo GetShell()
        {
            var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var powershellPath = Path.Combine(
                systemDirectory,
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            if (File.Exists(powershellPath))
            {
                return new ShellInfo(powershellPath, " -NoLogo");
            }

            var commandPromptPath = Path.Combine(systemDirectory, "cmd.exe");
            if (File.Exists(commandPromptPath))
            {
                return new ShellInfo(commandPromptPath, string.Empty);
            }

            throw new FileNotFoundException("未找到可用的本地 Shell。", powershellPath);
        }

        private readonly record struct ShellInfo(string Path, string Arguments);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public Coord(int columns, int rows)
        {
            X = checked((short)columns);
            Y = checked((short)rows);
        }

        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    private static class NativeMethods
    {
        public const uint ExtendedStartupInfoPresent = 0x00080000;
        public const uint CreateUnicodeEnvironment = 0x00000400;
        public const uint Infinite = 0xFFFFFFFF;
        public const uint WaitObject0 = 0x00000000;
        public const uint WaitFailed = 0xFFFFFFFF;
        public static readonly IntPtr ProcThreadAttributePseudoConsole = (IntPtr)0x00020016;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreatePipe(
            out SafeFileHandle readPipe,
            out SafeFileHandle writePipe,
            IntPtr pipeAttributes,
            uint size);

        [DllImport("kernel32.dll")]
        public static extern int CreatePseudoConsole(
            Coord size,
            SafeFileHandle input,
            SafeFileHandle output,
            uint flags,
            out IntPtr pseudoConsole);

        [DllImport("kernel32.dll")]
        public static extern int ResizePseudoConsole(IntPtr pseudoConsole, Coord size);

        [DllImport("kernel32.dll")]
        public static extern void ClosePseudoConsole(IntPtr pseudoConsole);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool InitializeProcThreadAttributeList(
            IntPtr attributeList,
            int attributeCount,
            int flags,
            ref nuint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateProcThreadAttribute(
            IntPtr attributeList,
            uint flags,
            IntPtr attribute,
            IntPtr value,
            nuint size,
            IntPtr previousValue,
            IntPtr returnSize);

        [DllImport("kernel32.dll")]
        public static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateProcess(
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetExitCodeProcess(SafeWaitHandle process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TerminateProcess(SafeWaitHandle process, uint exitCode);

        public static void ThrowIfFalse(bool result)
        {
            if (!result)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
    }
}
