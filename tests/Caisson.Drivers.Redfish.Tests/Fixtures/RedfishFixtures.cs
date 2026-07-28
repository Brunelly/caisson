using Caisson.Drivers.Redfish.Tests.Fakes;
using Caisson.Drivers.Redfish.Transport;

namespace Caisson.Drivers.Redfish.Tests.Fixtures;

/// <summary>
/// Canned iLO Redfish JSON and <c>ipmitool</c> text used across the unit tests. The JSON mirrors the
/// navigation chain the driver walks (service root → Systems collection → ComputerSystem →
/// EthernetInterfaces collection → each EthernetInterface), so a <see cref="FakeRedfishClient"/> seeded
/// from these behaves like a real endpoint for that scenario.
/// </summary>
public static class RedfishFixtures
{
    public const string ServiceRootPath = "/redfish/v1";
    public const string SystemsPath = "/redfish/v1/Systems";
    public const string SystemPath = "/redfish/v1/Systems/1";
    public const string EthernetCollectionPath = "/redfish/v1/Systems/1/EthernetInterfaces";
    public const string Nic1Path = "/redfish/v1/Systems/1/EthernetInterfaces/1";
    public const string Nic2Path = "/redfish/v1/Systems/1/EthernetInterfaces/2";

    public const string ServiceRoot = """
        {
          "@odata.id": "/redfish/v1",
          "Systems": { "@odata.id": "/redfish/v1/Systems" },
          "Managers": { "@odata.id": "/redfish/v1/Managers" },
          "Chassis": { "@odata.id": "/redfish/v1/Chassis" }
        }
        """;

    public const string SystemsCollection = """
        {
          "@odata.id": "/redfish/v1/Systems",
          "Members@odata.count": 1,
          "Members": [ { "@odata.id": "/redfish/v1/Systems/1" } ]
        }
        """;

    public const string EthernetCollection = """
        {
          "@odata.id": "/redfish/v1/Systems/1/EthernetInterfaces",
          "Members@odata.count": 2,
          "Members": [
            { "@odata.id": "/redfish/v1/Systems/1/EthernetInterfaces/1" },
            { "@odata.id": "/redfish/v1/Systems/1/EthernetInterfaces/2" }
          ]
        }
        """;

    public const string EmptyEthernetCollection = """
        {
          "@odata.id": "/redfish/v1/Systems/1/EthernetInterfaces",
          "Members@odata.count": 0,
          "Members": []
        }
        """;

    /// <summary>A full ComputerSystem with UUID, serial, model, hostname, BIOS version and NIC link.</summary>
    public const string SystemFull = """
        {
          "@odata.id": "/redfish/v1/Systems/1",
          "Id": "1",
          "UUID": "38373035-3831-4247-3830-353531384752",
          "SerialNumber": "CZ3629abcd",
          "Model": "ProLiant DL380 Gen10",
          "Manufacturer": "HPE",
          "HostName": "esx-node-07",
          "BiosVersion": "U30 v2.60",
          "EthernetInterfaces": { "@odata.id": "/redfish/v1/Systems/1/EthernetInterfaces" }
        }
        """;

    /// <summary>A ComputerSystem with neither UUID nor SerialNumber — the degraded-identity case.</summary>
    public const string SystemNoIdentity = """
        {
          "@odata.id": "/redfish/v1/Systems/1",
          "Id": "1",
          "Model": "ProLiant DL380 Gen10",
          "Manufacturer": "HPE",
          "HostName": "esx-node-07",
          "BiosVersion": "U30 v2.60",
          "EthernetInterfaces": { "@odata.id": "/redfish/v1/Systems/1/EthernetInterfaces" }
        }
        """;

    public const string Nic1 = """
        {
          "@odata.id": "/redfish/v1/Systems/1/EthernetInterfaces/1",
          "Id": "1",
          "Name": "eth0",
          "MACAddress": "00-1a-2b-3c-4d-5e",
          "LinkStatus": "LinkUp"
        }
        """;

    public const string Nic2 = """
        {
          "@odata.id": "/redfish/v1/Systems/1/EthernetInterfaces/2",
          "Id": "2",
          "Name": "eth1",
          "MACAddress": "001A.2B3C.4D5F",
          "LinkStatus": "LinkDown"
        }
        """;

    /// <summary>A NIC that reports no MAC address at all (the null-MAC case).</summary>
    public const string NicNoMac = """
        {
          "@odata.id": "/redfish/v1/Systems/1/EthernetInterfaces/2",
          "Id": "2",
          "Name": "eth1",
          "LinkStatus": "NoLink"
        }
        """;

    // --- ipmitool text fixtures ---

    public const string IpmiMcInfo = """
        Device ID                 : 32
        Device Revision           : 1
        Firmware Revision         : 2.61
        Manufacturer Name         : Hewlett-Packard
        Product ID                : 8192 (0x2000)
        """;

    public const string IpmiFruPrint = """
        FRU Device Description : Builtin FRU Device (ID 0)
         Chassis Type          : Rack Mount Chassis
         Chassis Serial        : CZ3629abcd
         Board Mfg             : HPE
         Board Product         : ProLiant DL380 Gen10
         Board Serial          : CZ3629abcd
         Product Manufacturer  : HPE
         Product Name          : ProLiant DL380 Gen10
         Product Serial        : CZ3629abcd
        """;

    public const string IpmiLanPrint = """
        Set in Progress         : Set Complete
        IP Address Source       : Static Address
        IP Address              : 10.4.7.20
        Subnet Mask             : 255.255.255.0
        MAC Address             : 00:1a:2b:3c:4d:99
        """;

    /// <summary>
    /// Seeds a <see cref="FakeRedfishClient"/> for the happy-path scenario: a full ComputerSystem and two
    /// NICs with normalizable MACs.
    /// </summary>
    public static FakeRedfishClient SuccessClient()
    {
        var client = new FakeRedfishClient();
        client.SetJson(ServiceRootPath, ServiceRoot);
        client.SetJson(SystemsPath, SystemsCollection);
        client.SetJson(SystemPath, SystemFull);
        client.SetJson(EthernetCollectionPath, EthernetCollection);
        client.SetJson(Nic1Path, Nic1);
        client.SetJson(Nic2Path, Nic2);
        return client;
    }
}
