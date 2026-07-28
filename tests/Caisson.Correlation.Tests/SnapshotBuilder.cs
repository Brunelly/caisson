using Caisson.Correlation.Input;
using Caisson.Domain.Enums;
using Caisson.Domain.ValueObjects;
using Caisson.Drivers.Abstractions.Bmc;
using Caisson.Drivers.Abstractions.Switches;

namespace Caisson.Correlation.Tests;

/// <summary>
/// A small fluent builder for assembling synthetic <see cref="TopologyCorrelationInput"/> graphs directly
/// from the story-3 driver records. No mocks are needed — the engine consumes plain POCOs.
/// </summary>
internal sealed class SnapshotBuilder
{
    private readonly List<SwitchTopologySnapshot> _switches = new();
    private readonly List<ServerNicSnapshot> _servers = new();

    public SnapshotBuilder Switch(string switchId, Action<SwitchBuilder> configure)
    {
        var builder = new SwitchBuilder(switchId);
        configure(builder);
        _switches.Add(builder.Build());
        return this;
    }

    public SnapshotBuilder Server(string serverId, Action<ServerBuilder> configure)
    {
        var builder = new ServerBuilder(serverId);
        configure(builder);
        _servers.Add(builder.Build());
        return this;
    }

    public TopologyCorrelationInput Build() => new(_switches, _servers);

    internal sealed class SwitchBuilder
    {
        private readonly string _switchId;
        private readonly List<SwitchPortInfo> _ports = new();
        private readonly List<LldpNeighbourInfo> _lldp = new();
        private readonly List<BridgeHostEntry> _bridge = new();
        private readonly List<VlanInfo> _vlans = new();
        private SwitchDeviceInfo? _device;

        public SwitchBuilder(string switchId) => _switchId = switchId;

        public SwitchBuilder Device(string? managementIp = null, string? serial = null)
        {
            _device = new SwitchDeviceInfo(managementIp, serial, null, null);
            return this;
        }

        public SwitchBuilder Port(string name, int? pvid = null, int[]? tagged = null, bool isUp = true)
        {
            _ports.Add(new SwitchPortInfo(name, isUp, pvid, tagged ?? Array.Empty<int>()));
            return this;
        }

        public SwitchBuilder Lldp(
            string port,
            string chassisId = "",
            string portId = "",
            string? systemName = null,
            string? mgmtAddress = null)
        {
            _lldp.Add(new LldpNeighbourInfo(port, chassisId, portId, systemName, mgmtAddress));
            return this;
        }

        public SwitchBuilder Bridge(string port, string mac)
        {
            _bridge.Add(new BridgeHostEntry(port, MacAddressValue.Parse(mac)));
            return this;
        }

        public SwitchBuilder Vlan(int vlanId, string? name = null)
        {
            _vlans.Add(new VlanInfo(vlanId, name));
            return this;
        }

        public SwitchTopologySnapshot Build()
            => new(_switchId, _device, _ports, _lldp, _bridge, _vlans);
    }

    internal sealed class ServerBuilder
    {
        private readonly string _serverId;
        private readonly List<BmcNetworkInterfaceInfo> _nics = new();
        private BmcSystemInventory? _system;

        public ServerBuilder(string serverId) => _serverId = serverId;

        public ServerBuilder System(string? hostname = null, string? uuid = null)
        {
            _system = new BmcSystemInventory(BmcType.Redfish, "0.0.0.0", uuid, hostname);
            return this;
        }

        public ServerBuilder Nic(string name, string? mac)
        {
            var value = string.IsNullOrWhiteSpace(mac) ? (MacAddressValue?)null : MacAddressValue.Parse(mac);
            _nics.Add(new BmcNetworkInterfaceInfo(name, value));
            return this;
        }

        public ServerNicSnapshot Build() => new(_serverId, _system, _nics);
    }
}
