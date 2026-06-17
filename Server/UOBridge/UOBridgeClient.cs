using System.Net;
using System.Net.Sockets;

namespace CentrED.Server.Bridge;

public class UOBridgeClient : IDisposable
{
    public UOBridgeClient(Socket socket)
    {
        Socket = socket;
        Socket.NoDelay = true;
        Socket.Blocking = false;
        try
        {
            RemoteAddress = (socket.RemoteEndPoint as IPEndPoint)?.Address;
        }
        catch
        {
            RemoteAddress = null;
        }
    }

    public Socket Socket { get; }
    public readonly IPAddress? RemoteAddress;
    public readonly long ConnectedTick = Environment.TickCount64;
    public bool GamePhase { get; set; }
    public bool SeedReceived;
    public bool InWorld;
    public bool Dead;

    public uint Serial;
    public uint MountSerial => 0x40000000 | Serial;
    public string Username = "Editor";
    public CentrED.Server.Config.Account? Account;

    public uint ClientVersion = 0x07000F01;

    public ushort X;
    public ushort Y;
    public sbyte Z;
    public byte Direction;
    public bool Mounted = true;
    public bool LastMoveRun;
    public int LastQueryBlockX = -1;
    public int LastQueryBlockY = -1;

    public enum TargetKind
    {
        None,
        Tele,
        ZGet,
        ZAdd,
        ZSet,
    }

    public TargetKind PendingTarget;
    public int PendingZAmount;
    public string ZAmountText = "1";
    public bool ZAreaMode;
    public (ushort X, ushort Y)? AreaCorner;

    public readonly HashSet<uint> KnownMobiles = new();

    public byte[] RecvBuffer = new byte[64 * 1024];
    public int RecvLength;

    private byte[] _compressBuffer = Array.Empty<byte>();

    private byte[] _sendBuffer = Array.Empty<byte>();
    private int _sendLength;
    private const int MaxSendQueue = 2 * 1024 * 1024;

    public void Send(ReadOnlySpan<byte> packet)
    {
        if (Dead)
            return;
        if (GamePhase)
        {
            int max = HuffmanEncoder.MaxCompressedSize(packet.Length);
            if (max > _compressBuffer.Length)
            {
                Array.Resize(ref _compressBuffer, max);
            }
            int len = HuffmanEncoder.Compress(packet, _compressBuffer);
            Enqueue(_compressBuffer.AsSpan(0, len));
        }
        else
        {
            Enqueue(packet);
        }
    }

    private void Enqueue(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return;
        if (_sendLength + data.Length > MaxSendQueue)
        {
            Dead = true;
            return;
        }
        if (_sendLength + data.Length > _sendBuffer.Length)
        {
            int size = _sendBuffer.Length == 0 ? 4096 : _sendBuffer.Length * 2;
            while (size < _sendLength + data.Length)
                size *= 2;
            Array.Resize(ref _sendBuffer, size);
        }
        data.CopyTo(_sendBuffer.AsSpan(_sendLength));
        _sendLength += data.Length;
    }

    public void Flush()
    {
        if (_sendLength == 0)
            return;
        try
        {
            int sent = 0;
            while (sent < _sendLength)
            {
                int n = Socket.Send(_sendBuffer.AsSpan(sent, _sendLength - sent), SocketFlags.None, out var error);
                if (n > 0)
                {
                    sent += n;
                    continue;
                }
                if (error == SocketError.WouldBlock)
                    break;
                Dead = true;
                break;
            }
            if (sent > 0)
            {
                _sendLength -= sent;
                if (_sendLength > 0)
                    Buffer.BlockCopy(_sendBuffer, sent, _sendBuffer, 0, _sendLength);
            }
        }
        catch (SocketException)
        {
            Dead = true;
        }
        catch (ObjectDisposedException)
        {
            Dead = true;
        }
    }

    public void Dispose()
    {
        Dead = true;
        try
        {
            Socket.Shutdown(SocketShutdown.Both);
        }
        catch
        {
        }
        Socket.Close();
    }
}
