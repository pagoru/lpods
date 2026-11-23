using System.Runtime.InteropServices;
using LPods.Platform;

namespace LPods.Tests;

/// <summary>
/// The AirPods end of an AAP channel: one end of a SOCK_SEQPACKET socket pair whose other end stands in for the
/// L2CAP socket. Records what the host writes and sends packets back.
/// </summary>
internal sealed class FakeAirPods : IDisposable
{
    private readonly int _fd;
    private readonly List<byte[]> _received = [];
    private readonly SemaphoreSlim _arrived = new(0);
    private readonly Thread _reader;
    private bool _closed;

    private FakeAirPods(int fd)
    {
        _fd = fd;
        _reader = new Thread(Read) { IsBackground = true };
        _reader.Start();
    }

    /// <summary>Creates the pair: returns the fake and the host-side descriptor (the "L2CAP socket").</summary>
    public static unsafe (FakeAirPods AirPods, int Channel) Create()
    {
        int* fds = stackalloc int[2];
        if (LibC.SocketPair(LibC.AfUnix, LibC.SockSeqPacket | LibC.SockCloexec, 0, fds) != 0)
        {
            throw new IOException($"socketpair failed: errno {Marshal.GetLastPInvokeError()}");
        }
        return (new FakeAirPods(fds[0]), fds[1]);
    }

    public unsafe void Send(params byte[] packet)
    {
        fixed (byte* p = packet)
        {
            if (LibC.Write(_fd, p, packet.Length) != packet.Length)
            {
                throw new IOException("write failed");
            }
        }
    }

    /// <summary>Waits for a packet the host wrote that matches.</summary>
    public async Task<byte[]> ExpectAsync(Func<byte[], bool> match, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        int seen = 0;
        while (true)
        {
            lock (_received)
            {
                for (; seen < _received.Count; seen++)
                {
                    if (match(_received[seen]))
                    {
                        return _received[seen];
                    }
                }
            }
            TimeSpan left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !await _arrived.WaitAsync(left).ConfigureAwait(false))
            {
                throw new TimeoutException("expected packet not written by the host");
            }
        }
    }

    public Task<byte[]> ExpectAsync(byte[] exact) => ExpectAsync(p => p.AsSpan().SequenceEqual(exact));

    /// <summary>Drops the channel, as when the AirPods go out of range.</summary>
    public void Close()
    {
        if (!_closed)
        {
            _closed = true;
            LibC.Shutdown(_fd, LibC.ShutRdWr);
        }
    }

    public void Dispose()
    {
        Close();
        _reader.Join(TimeSpan.FromSeconds(2));
        LibC.Close(_fd);
    }

    private unsafe void Read()
    {
        byte[] buffer = new byte[4096];
        fixed (byte* p = buffer)
        {
            while (true)
            {
                nint count = LibC.Read(_fd, p, buffer.Length);
                if (count <= 0)
                {
                    return;
                }
                lock (_received)
                {
                    _received.Add(buffer.AsSpan(0, (int)count).ToArray());
                }
                _arrived.Release();
            }
        }
    }
}
