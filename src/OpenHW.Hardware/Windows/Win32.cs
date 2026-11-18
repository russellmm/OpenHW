using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace OpenHW.Hardware.Windows;

/// <summary>The Windows API calls OpenHW's Windows readers use: kernel32, PDH, IP Helper and the configuration manager.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class Win32
{
    // ---- kernel32 ----

    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    public struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged,
            KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetPerformanceInfo(ref PerformanceInformation info, uint size);

    public const int RelationProcessorCore = 0;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetLogicalProcessorInformationEx(int relationship, byte* buffer, ref uint length);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(SafeFileHandle device, uint code, byte* input, uint inputLength, byte* output,
        uint outputLength, out uint returned, nint overlapped);

    public const uint FileShareReadWrite = 3, OpenExisting = 3;

    /// <summary>Opens a device for queries only (no read/write access): enough for the storage queries below, without elevation.</summary>
    public static SafeFileHandle OpenForQuery(string path) => CreateFile(path, 0, FileShareReadWrite, 0, OpenExisting, 0, 0);

    // ---- PDH ----

    public const uint PdhFmtDouble = 0x200, PdhFmtNoCap100 = 0x8000;
    public const int PdhMoreData = unchecked((int)0x800007D2), PdhCstatusValidData = 0, PdhCstatusNewData = 1;

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int PdhOpenQuery(string? source, nint userData, out nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int PdhAddEnglishCounter(nint query, string path, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    public static partial int PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    public static partial int PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint itemCount, byte* buffer);

    [LibraryImport("pdh.dll")]
    public static partial int PdhCloseQuery(nint query);

    // ---- IP Helper ----

    [LibraryImport("iphlpapi.dll")]
    public static partial int GetIfTable2(out nint table);

    [LibraryImport("iphlpapi.dll")]
    public static partial void FreeMibTable(nint table);

    /// <summary>MIB_IF_ROW2 (netioapi.h), 1352 bytes on x64.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MibIfRow2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public Guid InterfaceGuid;
        public fixed char Alias[257];
        public fixed char Description[257];
        public uint PhysicalAddressLength;
        public fixed byte PhysicalAddress[32];
        public fixed byte PermanentPhysicalAddress[32];
        public uint Mtu, Type, TunnelType, MediaType, PhysicalMediumType, AccessType, DirectionType;
        public byte InterfaceAndOperStatusFlags; // bit 0 HardwareInterface, 1 FilterInterface, 2 ConnectorPresent, …
        public uint OperStatus, AdminStatus, MediaConnectState;
        public Guid NetworkGuid;
        public uint ConnectionType;
        public ulong TransmitLinkSpeed, ReceiveLinkSpeed;
        public ulong InOctets, InUcastPkts, InNUcastPkts, InDiscards, InErrors, InUnknownProtos, InUcastOctets, InMulticastOctets, InBroadcastOctets;
        public ulong OutOctets, OutUcastPkts, OutNUcastPkts, OutDiscards, OutErrors, OutUcastOctets, OutMulticastOctets, OutBroadcastOctets, OutQLen;
    }

    // ---- Configuration manager (device tree properties) ----

    [StructLayout(LayoutKind.Sequential)]
    public struct DevPropKey(Guid category, uint id)
    {
        public Guid Category = category;
        public uint Id = id;
    }

    private static readonly Guid DeviceProperties = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    public static readonly DevPropKey BusNumber = new(DeviceProperties, 23);
    public static readonly DevPropKey Address = new(DeviceProperties, 30);
    public static readonly DevPropKey EnumeratorName = new(DeviceProperties, 24);

    public const int CrSuccess = 0;

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    public static partial int CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    public static partial int CM_Get_DevNode_Property(uint devInst, in DevPropKey key, out uint type, byte* buffer, ref uint size, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    public static partial int CM_Get_Device_Interface_List_Size(out uint length, in Guid interfaceClass, nint deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW")]
    public static partial int CM_Get_Device_Interface_List(in Guid interfaceClass, nint deviceId, char* buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_PropertyW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CM_Get_Device_Interface_Property(string deviceInterface, in DevPropKey key, out uint type, byte* buffer, ref uint size, uint flags);

    /// <summary>DEVPKEY_Device_InstanceId, read from a device interface.</summary>
    public static readonly DevPropKey InstanceId = new(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);

    public static readonly Guid DiskInterface = new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");

    /// <summary>Present devices' interface paths of a class (\\?\…).</summary>
    public static IReadOnlyList<string> InterfacePaths(Guid interfaceClass)
    {
        if (CM_Get_Device_Interface_List_Size(out var length, interfaceClass, 0, 0) != CrSuccess || length <= 1) return [];
        var buffer = new char[length];
        fixed (char* p = buffer)
            if (CM_Get_Device_Interface_List(interfaceClass, 0, p, length, 0) != CrSuccess) return [];
        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    public static string? InterfaceString(string deviceInterface, DevPropKey key)
    {
        var buffer = new byte[1024];
        var size = (uint)buffer.Length;
        fixed (byte* p = buffer)
            if (CM_Get_Device_Interface_Property(deviceInterface, key, out _, p, ref size, 0) != CrSuccess) return null;
        return System.Text.Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
    }

    public static uint? DevNodeUInt(uint devInst, DevPropKey key)
    {
        uint value = 0, size = 4;
        return CM_Get_DevNode_Property(devInst, key, out _, (byte*)&value, ref size, 0) == CrSuccess && size == 4 ? value : null;
    }

    public static string? DevNodeString(uint devInst, DevPropKey key)
    {
        var buffer = new byte[512];
        var size = (uint)buffer.Length;
        fixed (byte* p = buffer)
            if (CM_Get_DevNode_Property(devInst, key, out _, p, ref size, 0) != CrSuccess) return null;
        return System.Text.Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
    }
}
