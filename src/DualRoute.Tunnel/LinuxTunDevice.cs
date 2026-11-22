using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace DualRoute.Tunnel;

/// <summary>Linux x64/arm64 nonpersistent TUN, without a packet-information prefix.</summary>
public sealed class LinuxTunDevice : ITunDevice
{
    // Linux UAPI: linux/if_tun.h, linux/if.h, asm-generic/{fcntl,poll,ioctl}.h.
    private const int OpenFlags = 2 | 0x800 | 0x80000; // O_RDWR | O_NONBLOCK | O_CLOEXEC
    private const uint TunSetIff = 0x400454ca;
    private const ushort TunFlags = 0x0001 | 0x1000 | 0x8000; // TUN | NO_PI | TUN_EXCL
    private readonly SafeFileHandle handle;
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<byte[]> reads = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
        { FullMode = BoundedChannelFullMode.Wait });
    private sealed record WriteRequest(byte[] Packet, CancellationToken Token, TaskCompletionSource Completion);
    private readonly Channel<WriteRequest> writes = Channel.CreateBounded<WriteRequest>(64);
    private readonly Task worker;
    private readonly object disposeGate = new();
    private Task? disposal;
    private long dropped;
    public int Mtu => Protocol.MaxPayloadSize;
    public string Name { get; }
    public long DroppedPackets => Interlocked.Read(ref dropped);

    private LinuxTunDevice(SafeFileHandle handle, string name)
    {
        this.handle = handle;
        Name = name;
        worker = Task.Factory.StartNew(Pump, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>Exclusively creates tun0; closing its last fd removes it, including on process exit.</summary>
    public static async Task<LinuxTunDevice> CreateAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux TUN requires Linux.");
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException("Linux TUN ABI is supported on x64/arm64 only.");
        cancellationToken.ThrowIfCancellationRequested();
        var fd = Native.open("/dev/net/tun", OpenFlags);
        if (fd < 0) throw NativeError("Cannot open /dev/net/tun; check CAP_NET_ADMIN and TUN availability");
        var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        try
        {
            // ifreq is 40 bytes on these ABIs; name[16], flags in the union at offset 16.
            var request = new byte[40];
            Encoding.ASCII.GetBytes("tun0").CopyTo(request, 0);
            BitConverter.GetBytes(TunFlags).CopyTo(request, 16);
            if (Native.ioctl(fd, TunSetIff, request) < 0)
                throw NativeError("Cannot create tun0 exclusively; check permissions and that tun0 does not already exist");
            await RunIpAsync(["address", "add", "10.77.0.1/24", "dev", "tun0"], cancellationToken);
            await RunIpAsync(["link", "set", "dev", "tun0", "mtu",
                Protocol.MaxPayloadSize.ToString(CultureInfo.InvariantCulture), "up"], cancellationToken);
            return new(handle, "tun0");
        }
        catch { handle.Dispose(); throw; }
    }

    private static async Task RunIpAsync(string[] arguments, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var process = new Process { StartInfo = new ProcessStartInfo("ip")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new IOException("Cannot start ip; install iproute2.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(output, error).WaitAsync(deadline.Token);
            if (process.ExitCode != 0) throw new IOException($"ip setup failed (exit {process.ExitCode}): {error.Result.Trim()}");
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(2));
            throw;
        }
    }

    public ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken) => reads.Reader.ReadAsync(cancellationToken);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Ipv4Packet.TryParse(packet.Span, out _, out _))
        { Interlocked.Increment(ref dropped); return; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new WriteRequest(packet.ToArray(), cancellationToken, completion);
        await writes.Writer.WriteAsync(request, cancellationToken);
        await completion.Task.WaitAsync(cancellationToken);
    }

    private void Pump()
    {
        var fd = handle.DangerousGetHandle().ToInt32();
        // Full IPv4 size detects oversize frames without forwarding a truncated packet.
        var buffer = new byte[ushort.MaxValue];
        WriteRequest? pending = null;
        Exception? failure = null;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (pending is null) writes.Reader.TryRead(out pending);
                if (pending is not null && pending.Token.IsCancellationRequested)
                { pending.Completion.TrySetCanceled(pending.Token); pending = null; }
                var poll = new PollFd { Descriptor = fd, Events = (short)(1 | (pending is null ? 0 : 4)) };
                var ready = Native.poll(ref poll, 1, 50);
                if (ready < 0)
                {
                    if (Marshal.GetLastPInvokeError() == 4) continue; // EINTR
                    throw NativeError("TUN poll failed");
                }
                if ((poll.ReturnedEvents & (8 | 16 | 32)) != 0) throw new IOException("TUN descriptor closed or failed.");
                if ((poll.ReturnedEvents & 1) != 0)
                {
                    var count = Native.read(fd, buffer, (nuint)buffer.Length);
                    if (count < 0) { if (!Retryable()) throw NativeError("TUN read failed"); }
                    else if (count == 0) throw new IOException("TUN returned end of stream.");
                    else if (count > Protocol.MaxPayloadSize || !reads.Writer.TryWrite(buffer.AsSpan(0, (int)count).ToArray()))
                        Interlocked.Increment(ref dropped);
                }
                if (pending is not null && (poll.ReturnedEvents & 4) != 0)
                {
                    if (pending.Token.IsCancellationRequested)
                        pending.Completion.TrySetCanceled(pending.Token);
                    else
                    {
                        var count = Native.write(fd, pending.Packet, (nuint)pending.Packet.Length);
                        if (count < 0 && Retryable()) continue;
                        if (count < 0) throw NativeError("TUN write failed");
                        if (count != pending.Packet.Length) throw new IOException("TUN write did not transfer a whole packet.");
                        pending.Completion.TrySetResult();
                    }
                    pending = null;
                }
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            failure ??= new OperationCanceledException("TUN stopped.");
            pending?.Completion.TrySetException(failure);
            writes.Writer.TryComplete(failure);
            while (writes.Reader.TryRead(out var request)) request.Completion.TrySetException(failure);
            reads.Writer.TryComplete(failure);
        }
    }

    private static bool Retryable() => Marshal.GetLastPInvokeError() is 4 or 11; // EINTR, EAGAIN
    private static IOException NativeError(string message) => new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    public ValueTask DisposeAsync()
    {
        lock (disposeGate) return new(disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        stop.Cancel();
        writes.Writer.TryComplete();
        try { await worker; }
        finally { handle.Dispose(); stop.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd { public int Descriptor; public short Events; public short ReturnedEvents; }
    private static class Native
    {
        [DllImport("libc", SetLastError = true)] internal static extern int open(string path, int flags);
        [DllImport("libc", SetLastError = true)] internal static extern int ioctl(int fd, uint request, [In, Out] byte[] argument);
        [DllImport("libc", SetLastError = true)] internal static extern int poll(ref PollFd fds, nuint count, int timeout);
        [DllImport("libc", SetLastError = true)] internal static extern nint read(int fd, [Out] byte[] buffer, nuint count);
        [DllImport("libc", SetLastError = true)] internal static extern nint write(int fd, byte[] buffer, nuint count);
    }
}
