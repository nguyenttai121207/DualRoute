using System.ComponentModel;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace DualRoute.Client.Windows;

internal interface IWintunApi : IDisposable
{
    nint Create(string name, Guid guid);
    void Close(nint adapter);
    ulong Luid(nint adapter);
    nint Start(nint adapter);
    void End(nint session);
    WaitHandle ReadEvent(nint session);
    nint Receive(nint session, out uint size, out int error);
    void Release(nint session, nint packet);
    nint Allocate(nint session, uint size, out int error);
    void Send(nint session, nint packet);
}

// Signatures and ownership: Native/wintun.h, official Wintun 0.14.1.
internal sealed class WintunNative : IWintunApi
{
    internal const string DllHash = "E5DA8447DC2C320EDC0FC52FA01885C103DE8C118481F683643CACC3220DAFCE";
    private nint module;
    private readonly CreateFn create;
    private readonly HandleFn close, end;
    private readonly LuidFn luid;
    private readonly StartFn start;
    private readonly EventFn readEvent;
    private readonly ReceiveFn receive;
    private readonly PacketFn release, send;
    private readonly AllocateFn allocate;

    internal static void ValidateDll(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Wintun DLL path must be absolute.");
        using var file = File.OpenRead(path);
        using (var pe = new PEReader(file, PEStreamOptions.LeaveOpen))
            if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64)
                throw new BadImageFormatException("Wintun requires the official AMD64 DLL and an x64 process.");
        file.Position = 0;
        if (Convert.ToHexString(SHA256.HashData(file)) != DllHash)
            throw new IOException("Wintun DLL hash mismatch; restore AMD64 wintun.dll from the verified 0.14.1 archive.");
    }

    internal WintunNative()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Wintun requires Windows AMD64 and an x64 process.");
        var path = Path.Combine(AppContext.BaseDirectory, "wintun.dll");
        ValidateDll(path);
        module = LoadLibraryExW(path, 0, 0x100 | 0x800);
        if (module == 0) throw Error("Cannot load packaged wintun.dll; check installation and permissions");
        try
        {
            create = Export<CreateFn>("WintunCreateAdapter"); close = Export<HandleFn>("WintunCloseAdapter");
            luid = Export<LuidFn>("WintunGetAdapterLUID"); start = Export<StartFn>("WintunStartSession");
            end = Export<HandleFn>("WintunEndSession"); readEvent = Export<EventFn>("WintunGetReadWaitEvent");
            receive = Export<ReceiveFn>("WintunReceivePacket"); release = Export<PacketFn>("WintunReleaseReceivePacket");
            allocate = Export<AllocateFn>("WintunAllocateSendPacket"); send = Export<PacketFn>("WintunSendPacket");
        }
        catch { Dispose(); throw; }
    }
    private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, name));
    public nint Create(string name, Guid guid)
    {
        var value = create(name, "DualRoute", in guid);
        return value != 0 ? value : throw Error("Cannot create DualRoute adapter; run as Administrator and check adapter conflicts");
    }
    public void Close(nint adapter) => close(adapter);
    public ulong Luid(nint adapter) { luid(adapter, out var value); return value; }
    public nint Start(nint adapter)
    {
        var value = start(adapter, 0x20000);
        return value != 0 ? value : throw Error("Cannot start Wintun session");
    }
    public void End(nint session) => end(session);
    public WaitHandle ReadEvent(nint session)
    {
        var value = readEvent(session);
        if (value == 0) throw Error("Cannot get Wintun read event");
        return new BorrowedEvent(value);
    }
    public nint Receive(nint session, out uint size, out int error)
    { var p = receive(session, out size); error = p == 0 ? Marshal.GetLastPInvokeError() : 0; return p; }
    public void Release(nint session, nint packet) => release(session, packet);
    public nint Allocate(nint session, uint size, out int error)
    { var p = allocate(session, size); error = p == 0 ? Marshal.GetLastPInvokeError() : 0; return p; }
    public void Send(nint session, nint packet) => send(session, packet);
    public void Dispose() { var value = Interlocked.Exchange(ref module, 0); if (value != 0) NativeLibrary.Free(value); }
    internal static IOException Error(string message, int? code = null) => new(message, new Win32Exception(code ?? Marshal.GetLastPInvokeError()));
    private sealed class BorrowedEvent : WaitHandle
    { internal BorrowedEvent(nint handle) { SafeWaitHandle = new SafeWaitHandle(handle, false); } }

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode, SetLastError = true)]
    private delegate nint CreateFn(string name, string type, in Guid guid);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void HandleFn(nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void LuidFn(nint adapter, out ulong luid);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)] private delegate nint StartFn(nint adapter, uint capacity);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint EventFn(nint session);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)] private delegate nint ReceiveFn(nint session, out uint size);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)] private delegate nint AllocateFn(nint session, uint size);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void PacketFn(nint session, nint packet);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadLibraryExW(string path, nint file, uint flags);
}
