using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using DualRoute.Client.Windows;
using Xunit;

namespace DualRoute.Client.Windows.Tests;

public sealed class WindowsAdapterConfigurationTests
{
    [Fact]
    public void IpInterfaceRowMatchesWindowsSdkX64Abi()
    {
        // netioapi.h: default alignment 8, BOOLEAN/offload bitfields occupy one byte.
        Assert.Equal(168, Marshal.SizeOf<WindowsAdapterConfiguration.IpInterfaceRow>());
        Assert.Equal(0, Marshal.OffsetOf<WindowsAdapterConfiguration.IpInterfaceRow>("Family").ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<WindowsAdapterConfiguration.IpInterfaceRow>("Luid").ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<WindowsAdapterConfiguration.IpInterfaceRow>("InterfaceIndex").ToInt32());
        Assert.Equal(144, Marshal.OffsetOf<WindowsAdapterConfiguration.IpInterfaceRow>("SitePrefixLength").ToInt32());
        Assert.Equal(152, Marshal.OffsetOf<WindowsAdapterConfiguration.IpInterfaceRow>("Mtu").ToInt32());
    }

    [Fact]
    public void Ipv4MtuCanBeQueriedWithoutAdministratorAndInvalidIndexFails()
    {
        var index = NetworkInterface.LoopbackInterfaceIndex;
        Assert.InRange(WindowsAdapterConfiguration.GetIpv4Mtu((uint)index), 68u, uint.MaxValue);
        Assert.Throws<IOException>(() => WindowsAdapterConfiguration.GetIpv4Mtu(uint.MaxValue));
    }
}
