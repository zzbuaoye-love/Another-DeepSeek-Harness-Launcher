using System.Runtime.InteropServices;

namespace AnotherDSHL.Services;

internal static class ListeningProcessResolver
{
    private const int AddressFamilyIPv4 = 2;
    private const int OwnerPidListenerTable = 3;
    private const uint InsufficientBuffer = 122;
    private const uint TcpStateListen = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint ProcessId;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order,
        int addressFamily, int tableClass, uint reserved);

    public static int? FindLoopbackListener(int port)
    {
        var size = 0;
        if (GetExtendedTcpTable(IntPtr.Zero, ref size, false, AddressFamilyIPv4,
                OwnerPidListenerTable, 0) != InsufficientBuffer || size < 4)
            return null;
        var table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, AddressFamilyIPv4,
                    OwnerPidListenerTable, 0) != 0) return null;
            var count = Marshal.ReadInt32(table);
            var rowSize = Marshal.SizeOf<TcpRow>();
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<TcpRow>(IntPtr.Add(table, 4 + index * rowSize));
                var localPort = ((row.LocalPort & 0xff) << 8) | ((row.LocalPort >> 8) & 0xff);
                if (row.State == TcpStateListen && localPort == port &&
                    row.LocalAddress is 0 or 0x0100007f && row.ProcessId > 0)
                    return checked((int)row.ProcessId);
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(table); }
    }
}
