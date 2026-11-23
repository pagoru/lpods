using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.IO.Pipes;
using LPods.Platform;
using Microsoft.Win32.SafeHandles;

namespace WPods.Core;

/// <summary>
/// Linux stand-in for upstream's ServicePipes. On Windows the AAP channel comes from WPods.Service over a message-mode
/// named pipe. On Linux the app opens the L2CAP channel (PSM 0x1001) itself and hands <see cref="AapSession"/> one end
/// of a SOCK_SEQPACKET socket pair, which keeps message boundaries exactly like the pipe, so the shared session code
/// runs unchanged. A bridge thread pair copies packets between the two sockets and speaks the pipe's framing:
/// inbound packets get the <see cref="AapPacket"/> kind byte, and a closed channel sends <see cref="AapEnded"/>.
/// </summary>
internal static unsafe class ServicePipes
{
    public const byte AapPacket = 0x00;
    public const byte AapEnded = 0x01;
    public const byte AapCallAudio = 0x03;
    public const byte VoiceState = 0x04;
    public const byte VoiceData = 0x05;
    public const byte VoiceControl = 0xF1;
    public const byte VoiceDownlink = 0xF2;
    public const byte VoiceSilence = 0xF3;

    private const ushort AapPsm = 0x1001;
    private const int ErrorBusy = 170;
    private const int ErrorSemTimeout = 121;
    private const int ErrorConnectionRefused = 1225;
    private const int ConnectTimeoutSeconds = 10;
    private const int BufferSize = 64 * 1024;

    /// <summary>
    /// Opens an AAP channel to <paramref name="address"/> without paging: only when BlueZ reports the device connected.
    /// Null with the Win32-style error upstream expects: 1167 no link, 170 busy, 1225 refused, 121 no answer.
    /// </summary>
    public static NamedPipeClientStream? OpenAap(ulong address, out int error)
    {
        try
        {
            if (BlueZ.FindDeviceAsync(address).GetAwaiter().GetResult() is not { Connected: true })
            {
                error = Native.ErrorDeviceNotConnected;
                return null;
            }
        }
        catch (Exception e)
        {
            Log.Warn("aap", $"open {Mac.Format(address)}: BlueZ not reachable: {e.Message}");
            error = Native.ErrorDeviceNotConnected;
            return null;
        }

        int l2cap = OpenL2cap(address, out error);
        if (l2cap < 0)
        {
            return null;
        }
        NamedPipeClientStream? stream = Bridged(address, l2cap, out error);
        if (stream is null)
        {
            LibC.Close(l2cap);
        }
        return stream;
    }

    /// <summary>
    /// The session's end of a socket pair bridged to <paramref name="channel"/> (an open AAP channel, or in tests any
    /// SOCK_SEQPACKET socket), which the bridge then owns.
    /// </summary>
    internal static NamedPipeClientStream? Bridged(ulong address, int channel, out int error)
    {
        int* pair = stackalloc int[2];
        if (LibC.SocketPair(LibC.AfUnix, LibC.SockSeqPacket | LibC.SockCloexec, 0, pair) != 0)
        {
            error = Marshal.GetLastPInvokeError();
            return null;
        }
        var stream = new NamedPipeClientStream(PipeDirection.InOut, isAsync: true, isConnected: true, new SafePipeHandle(pair[0], ownsHandle: true));
        new Bridge(address, channel, pair[1]).Start();
        error = Native.ErrorSuccess;
        return stream;
    }

    private static int OpenL2cap(ulong address, out int error)
    {
        int fd = LibC.Socket(LibC.AfBluetooth, LibC.SockSeqPacket | LibC.SockCloexec, LibC.BtProtoL2cap);
        if (fd < 0)
        {
            error = Marshal.GetLastPInvokeError();
            Log.Warn("aap", $"L2CAP socket failed: errno {error}");
            return -1;
        }
        // A blocking L2CAP connect gives up after SO_SNDTIMEO.
        long* timeout = stackalloc long[2] { ConnectTimeoutSeconds, 0 };
        LibC.SetSockOpt(fd, LibC.SolSocket, LibC.SoSndTimeo, timeout, 2 * sizeof(long));

        // struct sockaddr_l2 { sa_family_t; __le16 psm; bdaddr_t (little-endian); __le16 cid; __u8 bdaddr_type; }
        byte* sockaddr = stackalloc byte[14];
        new Span<byte>(sockaddr, 14).Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(sockaddr, 2), LibC.AfBluetooth);
        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(sockaddr + 2, 2), AapPsm);
        for (int i = 0; i < 6; i++)
        {
            sockaddr[4 + i] = (byte)(address >> (8 * i));
        }
        int result;
        do
        {
            result = LibC.Connect(fd, sockaddr, 14);
        }
        while (result != 0 && Marshal.GetLastPInvokeError() == LibC.EIntr);
        if (result == 0)
        {
            // Writes must not time out mid-session; AapSession has its own 5 s write timeout.
            timeout[0] = 0;
            LibC.SetSockOpt(fd, LibC.SolSocket, LibC.SoSndTimeo, timeout, 2 * sizeof(long));
            Log.Info("aap", $"L2CAP PSM 0x1001 open to {Mac.Format(address)}");
            error = Native.ErrorSuccess;
            return fd;
        }
        int errno = Marshal.GetLastPInvokeError();
        LibC.Close(fd);
        error = ToWin32(errno);
        Log.Warn("aap", $"L2CAP connect to {Mac.Format(address)} failed: errno {errno} (as {error})");
        return -1;
    }

    private static int ToWin32(int errno) => errno switch
    {
        LibC.ENotConn or LibC.EHostDown or LibC.EHostUnreach or LibC.EConnReset => Native.ErrorDeviceNotConnected,
        LibC.EConnRefused or LibC.EAcces => ErrorConnectionRefused,
        LibC.ETimedOut => ErrorSemTimeout,
        LibC.EBusy => ErrorBusy,
        _ => errno,
    };

    /// <summary>Copies packets both ways until either side closes, then closes both sockets.</summary>
    private sealed class Bridge(ulong address, int l2cap, int app)
    {
        private int _running = 2;

        public void Start()
        {
            new Thread(Inbound) { IsBackground = true, Name = "aap-in" }.Start();
            new Thread(Outbound) { IsBackground = true, Name = "aap-out" }.Start();
        }

        // AirPods -> app: kind byte + packet; on close, AapEnded + error code.
        private void Inbound()
        {
            byte[] buffer = new byte[1 + BufferSize];
            int error = 0;
            fixed (byte* p = buffer)
            {
                p[0] = AapPacket;
                while (true)
                {
                    nint count = LibC.Read(l2cap, p + 1, BufferSize);
                    if (count < 0 && Marshal.GetLastPInvokeError() == LibC.EIntr)
                    {
                        continue;
                    }
                    if (count <= 0)
                    {
                        error = count == 0 ? 0 : ToWin32(Marshal.GetLastPInvokeError());
                        break;
                    }
                    if (LibC.Write(app, p, count + 1) < 0)
                    {
                        break;
                    }
                }
                byte* ended = stackalloc byte[5];
                ended[0] = AapEnded;
                BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(ended + 1, 4), error);
                LibC.Write(app, ended, 5);
            }
            Log.Info("aap", $"L2CAP channel to {Mac.Format(address)} ended (error {error})");
            LibC.Shutdown(app, LibC.ShutRdWr);
            Finish();
        }

        // App -> AirPods: AAP packets as they are; HD voice messages (0xF1..0xF3) have nowhere to go yet.
        private void Outbound()
        {
            byte[] buffer = new byte[BufferSize];
            fixed (byte* p = buffer)
            {
                while (true)
                {
                    nint count = LibC.Read(app, p, BufferSize);
                    if (count < 0 && Marshal.GetLastPInvokeError() == LibC.EIntr)
                    {
                        continue;
                    }
                    if (count <= 0)
                    {
                        break;
                    }
                    if (p[0] >= VoiceControl)
                    {
                        continue;
                    }
                    if (LibC.Write(l2cap, p, count) < 0)
                    {
                        Log.Warn("aap", $"L2CAP write failed: errno {Marshal.GetLastPInvokeError()}");
                        break;
                    }
                }
            }
            LibC.Shutdown(l2cap, LibC.ShutRdWr);
            Finish();
        }

        private void Finish()
        {
            if (Interlocked.Decrement(ref _running) == 0)
            {
                LibC.Close(l2cap);
                LibC.Close(app);
            }
        }
    }
}
