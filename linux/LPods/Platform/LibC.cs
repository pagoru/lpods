using System.Runtime.InteropServices;

namespace LPods.Platform;

/// <summary>The few glibc calls the AAP socket needs (.NET's Socket has no AF_BLUETOOTH).</summary>
internal static unsafe partial class LibC
{
    private const string Lib = "libc.so.6";

    public const int AfUnix = 1;
    public const int AfBluetooth = 31;
    public const int SockSeqPacket = 5;
    public const int SockCloexec = 0x80000;
    public const int BtProtoL2cap = 0;
    public const int SolSocket = 1;
    public const int SoSndTimeo = 21;
    public const int ShutRdWr = 2;

    // errno values (Linux, all architectures .NET supports).
    public const int EIntr = 4;
    public const int EBusy = 16;
    public const int EConnReset = 104;
    public const int ENotConn = 107;
    public const int ETimedOut = 110;
    public const int EConnRefused = 111;
    public const int EHostDown = 112;
    public const int EHostUnreach = 113;
    public const int EAcces = 13;

    [LibraryImport(Lib, EntryPoint = "socket", SetLastError = true)]
    public static partial int Socket(int domain, int type, int protocol);

    [LibraryImport(Lib, EntryPoint = "socketpair", SetLastError = true)]
    public static partial int SocketPair(int domain, int type, int protocol, int* fds);

    [LibraryImport(Lib, EntryPoint = "connect", SetLastError = true)]
    public static partial int Connect(int fd, byte* address, int length);

    [LibraryImport(Lib, EntryPoint = "setsockopt", SetLastError = true)]
    public static partial int SetSockOpt(int fd, int level, int name, void* value, int length);

    [LibraryImport(Lib, EntryPoint = "read", SetLastError = true)]
    public static partial nint Read(int fd, byte* buffer, nint count);

    [LibraryImport(Lib, EntryPoint = "write", SetLastError = true)]
    public static partial nint Write(int fd, byte* buffer, nint count);

    [LibraryImport(Lib, EntryPoint = "shutdown", SetLastError = true)]
    public static partial int Shutdown(int fd, int how);

    [LibraryImport(Lib, EntryPoint = "close", SetLastError = true)]
    public static partial int Close(int fd);
}
