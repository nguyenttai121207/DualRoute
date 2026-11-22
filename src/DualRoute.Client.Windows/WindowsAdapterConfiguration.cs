using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using DualRoute.Tunnel;
using Microsoft.Win32;

namespace DualRoute.Client.Windows;

internal sealed class WindowsAdapterConfiguration : IAdapterSystem
{
    private static readonly Guid NetworkClass = new("4d36e972-e325-11ce-bfc1-08002be10318");
    public IReadOnlyList<AdapterIdentity> Find()
    {
        var guid = NetworkClass;
        var set = SetupDiGetClassDevsW(in guid, null, 0, 0);
        if (set == -1) throw WintunNative.Error("Cannot enumerate network devices");
        try
        {
            var result = new List<AdapterIdentity>();
            for (uint i = 0; ; i++)
            {
                var data = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref data))
                { if (Marshal.GetLastPInvokeError() == 259) break; throw WintunNative.Error("Cannot enumerate network device"); }
                var driver = Property(set, ref data, 9);
                if (driver is null) continue;
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driver);
                if (!Guid.TryParse(key?.GetValue("NetCfgInstanceId") as string, out var adapterGuid)) continue;
                // Include foreign devices with our GUID so a collision cannot be silently adopted.
                var description = Property(set, ref data, 0);
                var hardware = Property(set, ref data, 1);
                using var connection = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Network\" + NetworkClass.ToString("B") + "\\" + adapterGuid.ToString("B") + @"\Connection");
                var name = connection?.GetValue("Name") as string ?? "";
                var instance = new StringBuilder(512);
                if (!SetupDiGetDeviceInstanceIdW(set, ref data, instance, 512, out _)) throw WintunNative.Error("Cannot read adapter instance identity");
                var ownedType = description == "DualRoute Tunnel" && hardware?.Split('\0').Any(v => v.Equals("Wintun", StringComparison.OrdinalIgnoreCase)) == true;
                if (adapterGuid == WintunRecovery.AdapterGuid || ownedType)
                    result.Add(new(adapterGuid, ownedType ? name : "FOREIGN:" + name, instance.ToString()));
            }
            return result;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    public void Remove(AdapterIdentity adapter)
    {
        if (adapter.Guid != WintunRecovery.AdapterGuid || !Find().Contains(adapter))
            throw new IOException("Adapter identity changed; refusing removal.");
        var set = SetupDiCreateDeviceInfoList(0, 0);
        if (set == -1) throw WintunNative.Error("Cannot open device information set");
        try
        {
            var data = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
            if (!SetupDiOpenDeviceInfoW(set, adapter.InstanceId, 0, 0, ref data)) throw WintunNative.Error("Cannot open owned Wintun device");
            var remove = new RemoveParams { HeaderSize = 8, Function = 5, Scope = 1 };
            if (!SetupDiSetClassInstallParamsW(set, ref data, ref remove, Marshal.SizeOf<RemoveParams>()) ||
                !SetupDiCallClassInstaller(5, set, ref data)) throw WintunNative.Error("Cannot remove owned Wintun adapter; run as Administrator");
            var install = new InstallParams { Size = (uint)Marshal.SizeOf<InstallParams>() };
            if (!SetupDiGetDeviceInstallParamsW(set, ref data, ref install)) throw WintunNative.Error("Cannot verify device removal result");
            if ((install.Flags & (0x80 | 0x100)) != 0)
                throw new IOException("Windows requires a restart to finish removing the owned Wintun adapter; journal retained.");
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    public async Task ConfigureAsync(ulong luid, IPAddress ip, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var row = new UnicastRow();
        InitializeUnicastIpAddressEntry(ref row);
        row.Family = 2; row.Address = BitConverter.ToUInt32(ip.GetAddressBytes()); row.Luid = luid; row.PrefixLength = 24;
        Check(CreateUnicastIpAddressEntry(in row), "Cannot assign client IPv4; check Administrator rights/address conflicts");
        var iface = new IpInterfaceRow { Family = 2, Luid = luid };
        Check(GetIpInterfaceEntry(ref iface), "Cannot read Wintun IPv4 interface");
        iface.Mtu = Protocol.MaxPayloadSize; iface.SitePrefixLength = 0;
        Check(SetIpInterfaceEntry(ref iface), "Cannot set Wintun MTU");
        Check(ConvertInterfaceLuidToIndex(in luid, out var index), "Cannot resolve Wintun interface index");
        var admin = new IfRow { Index = index, AdminStatus = 1 };
        Check(SetIfEntry(in admin), "Cannot set Wintun administrative UP");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            Check(GetUnicastIpAddressEntry(ref row), "Cannot verify Wintun IP");
            var mtu = GetIpv4Mtu(index);
            if (row.DadState == 2) throw new IOException("Duplicate client virtual IP; check server configuration.");
            if (row.DadState == 4 && row.PrefixLength == 24 && mtu == Protocol.MaxPayloadSize) return;
            await Task.Delay(50, timeout.Token);
        }
    }
    internal static uint GetIpv4Mtu(uint interfaceIndex)
    {
        // A fresh AF_INET query verifies the subinterface, not GetAdaptersAddresses' link MTU.
        var row = new IpInterfaceRow { Family = 2, InterfaceIndex = interfaceIndex };
        Check(GetIpInterfaceEntry(ref row), "Cannot read IPv4 subinterface MTU");
        return row.Mtu;
    }
    internal static void Check(uint error, string operation) { if (error != 0) throw new IOException(operation, new Win32Exception((int)error)); }
    private static string? Property(nint set, ref DeviceInfo data, uint property)
    {
        var buffer = new byte[4096];
        if (SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buffer, (uint)buffer.Length, out var size))
            return Encoding.Unicode.GetString(buffer, 0, checked((int)size)).TrimEnd('\0');
        var error = Marshal.GetLastPInvokeError();
        if (error == 13) return null;
        throw WintunNative.Error("Cannot read network device property", error);
    }

    // Windows SDK netioapi.h x64 ABI: SOCKADDR_INET=28, NET_LUID aligned to 8.
    [StructLayout(LayoutKind.Explicit, Size = 80)] internal struct UnicastRow
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(4)] public uint Address;
        [FieldOffset(32)] public ulong Luid;
        [FieldOffset(60)] public byte PrefixLength;
        [FieldOffset(64)] public uint DadState;
    }
    [StructLayout(LayoutKind.Explicit, Size = 168)] internal struct IpInterfaceRow
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(8)] public ulong Luid;
        [FieldOffset(16)] public uint InterfaceIndex;
        [FieldOffset(144)] public uint SitePrefixLength;
        [FieldOffset(152)] public uint Mtu;
    }
    // MIB_IFROW: WCHAR name[256], index, type, MTU, speed, phys length, address[8], admin status.
    [StructLayout(LayoutKind.Explicit, Size = 860)] private struct IfRow
    { [FieldOffset(512)] public uint Index; [FieldOffset(540)] public uint AdminStatus; }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo
    { public uint Size; public Guid ClassGuid; public uint DevInst; public nuint Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct RemoveParams
    { public uint HeaderSize, Function, Scope, Profile; }
    [StructLayout(LayoutKind.Explicit, Size = 584)] private struct InstallParams
    { [FieldOffset(0)] public uint Size; [FieldOffset(4)] public uint Flags; }
    [DllImport("iphlpapi.dll")] private static extern void InitializeUnicastIpAddressEntry(ref UnicastRow row);
    [DllImport("iphlpapi.dll")] private static extern uint CreateUnicastIpAddressEntry(in UnicastRow row);
    [DllImport("iphlpapi.dll")] private static extern uint GetUnicastIpAddressEntry(ref UnicastRow row);
    [DllImport("iphlpapi.dll")] private static extern uint GetIpInterfaceEntry(ref IpInterfaceRow row);
    [DllImport("iphlpapi.dll")] private static extern uint SetIpInterfaceEntry(ref IpInterfaceRow row);
    [DllImport("iphlpapi.dll")] private static extern uint ConvertInterfaceLuidToIndex(in ulong luid, out uint index);
    [DllImport("iphlpapi.dll")] private static extern uint SetIfEntry(in IfRow row);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint SetupDiGetClassDevsW(in Guid guid, string? enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref DeviceInfo data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceRegistryPropertyW(nint set, ref DeviceInfo data, uint property, out uint type, byte[] buffer, uint size, out uint required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInstanceIdW(nint set, ref DeviceInfo data, StringBuilder id, uint size, out uint required);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern nint SetupDiCreateDeviceInfoList(nint guid, nint parent);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiOpenDeviceInfoW(nint set, string id, nint parent, uint flags, ref DeviceInfo data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiSetClassInstallParamsW(nint set, ref DeviceInfo data, ref RemoveParams parameters, int size);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiCallClassInstaller(uint function, nint set, ref DeviceInfo data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInstallParamsW(nint set, ref DeviceInfo data, ref InstallParams parameters);
}
