using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SteamDirect
{
    internal sealed class HistoryNetwork : IDisposable
    {
        internal struct LinkChange { internal long At; internal bool Available; }
        private static readonly LinkChange[] noChanges = new LinkChange[0];
        private readonly Queue<LinkChange> linkChanges = new Queue<LinkChange>();
        internal const string Target = "1.1.1.1";
        private static readonly IPAddress external = IPAddress.Parse(Target);
        private static readonly byte[] payload = new byte[16];
        private readonly Ping gatewayPing = new Ping(), externalPing = new Ping();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private IPAddress gateway;
        private bool adapterUp, route, tunnel, knownProxy;
        private int dirty = 1;
        private long routeAt = -60000, proxyAt = -30000, dnsAt = -60000;
        private Task<IPAddress[]> dns;
        private bool dnsTimeoutReported, subscribedAddress, subscribedAvailability;
        private long pingCount, dnsCount;
        private ulong idleBefore, totalBefore;
        internal long PingCount { get { return Interlocked.Read(ref pingCount); } }
        internal long DnsCount { get { return Interlocked.Read(ref dnsCount); } }

        [DllImport("iphlpapi.dll")] private static extern uint GetBestInterface(uint destination, out uint index);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

        internal HistoryNetwork()
        {
            try { NetworkChange.NetworkAddressChanged += AddressChanged; subscribedAddress = true; }
            catch (NetworkInformationException) { }
            try { NetworkChange.NetworkAvailabilityChanged += AvailabilityChanged; subscribedAvailability = true; }
            catch (NetworkInformationException) { }
        }

        private void AddressChanged(object sender, EventArgs args) { Interlocked.Exchange(ref dirty, 1); }
        private void AvailabilityChanged(object sender, NetworkAvailabilityEventArgs args)
        {
            Interlocked.Exchange(ref dirty, 1);
            lock (linkChanges)
            {
                if (linkChanges.Count == 64) linkChanges.Dequeue();
                linkChanges.Enqueue(new LinkChange { At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Available = args.IsAvailable });
            }
        }

        internal LinkChange[] DrainLinkChanges()
        {
            lock (linkChanges)
            {
                if (linkChanges.Count == 0) return noChanges;
                var result = linkChanges.ToArray();
                linkChanges.Clear();
                return result;
            }
        }

        internal double Load()
        {
            ulong idle, kernel, user;
            if (!GetSystemTimes(out idle, out kernel, out user)) return .9;
            ulong total = kernel + user;
            double load = totalBefore > 0 && total > totalBefore && idle >= idleBefore ?
                Math.Max(0, Math.Min(1, 1 - (double)(idle - idleBefore) / (total - totalBefore))) : 0;
            totalBefore = total;
            idleBefore = idle;
            return load;
        }

        private void RefreshRoute()
        {
            if (Interlocked.Exchange(ref dirty, 0) == 0 && clock.ElapsedMilliseconds - routeAt < 60000) return;
            routeAt = clock.ElapsedMilliseconds;
            uint index;
            route = GetBestInterface(BitConverter.ToUInt32(external.GetAddressBytes(), 0), out index) == 0;
            gateway = null;
            adapterUp = false;
            tunnel = false;
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                adapterUp = true;
                if (!route || !adapter.Supports(NetworkInterfaceComponent.IPv4)) continue;
                var properties = adapter.GetIPProperties();
                var ipv4 = properties.GetIPv4Properties();
                if (ipv4 == null || ipv4.Index != index) continue;
                string device = (adapter.Name + " " + adapter.Description).ToLowerInvariant();
                tunnel = adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                    device.Contains("wintun") || device.Contains("wireguard") || device.Contains("tailscale") ||
                    device.Contains("openvpn") || device.Contains("clash") || device.Contains("mihomo");
                foreach (var candidate in properties.GatewayAddresses)
                {
                    if (candidate.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !candidate.Address.Equals(IPAddress.Any))
                    {
                        gateway = candidate.Address;
                        break;
                    }
                }
            }
        }

        private string Mode()
        {
            if (clock.ElapsedMilliseconds - proxyAt >= 30000)
            {
                proxyAt = clock.ElapsedMilliseconds;
                knownProxy = false;
                var processes = Process.GetProcesses();
                try
                {
                    foreach (var process in processes)
                    {
                        try { if (ProxySettings.IsProxyComponent(process.ProcessName)) { knownProxy = true; break; } }
                        catch (InvalidOperationException) { }
                    }
                }
                finally { foreach (var process in processes) process.Dispose(); }
            }
            return ProxySettings.ObserveHistoryMode(tunnel, route, knownProxy);
        }

        private int PingOnce(Ping client, IPAddress address)
        {
            if (address == null || !adapterUp) return -2;
            Interlocked.Increment(ref pingCount);
            try
            {
                var reply = client.Send(address, 800, payload);
                return reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : -1;
            }
            catch (PingException) { return -2; }
        }

        private int ReadDns()
        {
            long now = clock.ElapsedMilliseconds;
            if (dns != null)
            {
                if (dns.IsCompleted)
                {
                    bool ok = dns.Status == TaskStatus.RanToCompletion && dns.Result.Length > 0;
                    if (dns.IsFaulted) { var ignored = dns.Exception; }
                    dns = null;
                    return dnsTimeoutReported ? -1 : ok ? 1 : 0;
                }
                if (!dnsTimeoutReported && now - dnsAt >= 5000) { dnsTimeoutReported = true; return 0; }
                return -1;
            }
            if (!adapterUp || now - dnsAt < 30000) return -1;
            dnsAt = now;
            dnsTimeoutReported = false;
            Interlocked.Increment(ref dnsCount);
            try { dns = Dns.GetHostAddressesAsync("one.one.one.one"); }
            catch (System.Net.Sockets.SocketException) { return 0; }
            if (dns.IsCompleted) return ReadDns();
            return -1;
        }

        internal HistorySample Sample(long at, CancellationToken token)
        {
            RefreshRoute();
            string mode = Mode();
            int dnsStatus = ReadDns();
            int local = PingOnce(gatewayPing, gateway);
            token.ThrowIfCancellationRequested();
            int remote = PingOnce(externalPing, route ? external : null);
            return new HistorySample
            {
                At = at, Mode = mode, Gateway = gateway == null ? "" : gateway.ToString(),
                GatewayMs = local, ExternalMs = remote, Dns = dnsStatus, AdapterUp = adapterUp
            };
        }

        public void Dispose()
        {
            if (subscribedAddress) NetworkChange.NetworkAddressChanged -= AddressChanged;
            if (subscribedAvailability) NetworkChange.NetworkAvailabilityChanged -= AvailabilityChanged;
            gatewayPing.Dispose();
            externalPing.Dispose();
            if (dns != null) dns.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}

