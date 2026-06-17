using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CentrED.Network;
using CentrED.Server.Config;
using CentrED.Server.Map;

namespace CentrED.Server.Bridge;

public class UOBridgeServer : IDisposable
{
    private const ushort PlayerBody = 0x0190;
    private const ushort PlayerHue = 0x83EA;
    private const ushort HorseGraphic = 0x3E9F;
    private const byte LayerMount = 0x19;

    private const int ViewRange = 24;

    private const int MaxBytesPerTick = 256 * 1024;

    private const byte ClientMapIndex = 0;

    private const uint CvNewCityFormat = 0x07000D00;
    private const uint CvEquipHue = 0x07002101;
    private const uint CvFeaturesWide = 0x06000E02;
    private const uint Cv6017 = 0x06000107;
    private const uint Cv70180 = 0x07001200;

    private readonly CEDServer _server;
    private readonly UOBridgeConfig _config;
    private readonly Socket _listener;
    private readonly ConcurrentQueue<UOBridgeClient> _connectedQueue = new();
    private readonly List<UOBridgeClient> _clients = new();
    private readonly List<UOBridgeClient> _toRemove = new();
    private readonly HashSet<uint> _dirtyLandBlocks = new();
    private readonly HashSet<uint> _dirtyStaticBlocks = new();
    private bool _running = true;
    private uint _nextPlayerSerial = 1;

    private const int MaxConnectionsPerIp = 4;
    private const int AuthTimeoutMs = 30_000;
    private readonly object _connLock = new();
    private int _connectionCount;
    private readonly Dictionary<IPAddress, int> _perIp = new();

    public int Port => _config.Port;

    public UOBridgeServer(CEDServer server)
    {
        _server = server;
        _config = server.Config.UOBridge;
        _listener = Bind(Port);

        var landscape = server.Landscape;
        landscape.LandTileReplaced += OnLandChanged;
        landscape.LandTileElevated += OnLandElevated;
        landscape.StaticTileAdded += OnStaticChanged;
        landscape.StaticTileRemoved += OnStaticChanged;
        landscape.StaticTileReplaced += OnStaticReplaced;
        landscape.StaticTileMoved += OnStaticMoved;
        landscape.StaticTileElevated += OnStaticElevated;
        landscape.StaticTileHued += OnStaticHued;
        landscape.BlockUpdated += OnBlockUpdated;

        AcceptLoop(_listener);
        _server.LogInfo($"[UOBridge] Listening for UO clients on port {Port}");
    }

    private Socket Bind(int port)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
            LingerState = new LingerOption(false, 0),
        };
        s.Bind(new IPEndPoint(IPAddress.Any, port));
        s.Listen(8);
        return s;
    }

    private async void AcceptLoop(Socket listener)
    {
        while (_running)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptAsync();
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception e)
            {
                if (!_running)
                    break;
                _server.LogWarn($"[UOBridge] Accept failed: {e.Message}");
                try { await Task.Delay(100); } catch { }
                continue;
            }

            if (!TryAdmit(socket))
            {
                try { socket.Close(); } catch { }
                continue;
            }
            _connectedQueue.Enqueue(new UOBridgeClient(socket));
        }
    }

    private bool TryAdmit(Socket socket)
    {
        IPAddress? ip = null;
        try { ip = (socket.RemoteEndPoint as IPEndPoint)?.Address; } catch { }
        lock (_connLock)
        {
            if (_connectionCount >= _config.MaxClients)
            {
                _server.LogWarn($"[UOBridge] Refusing connection from {ip}: client limit ({_config.MaxClients}) reached");
                return false;
            }
            if (ip != null)
            {
                _perIp.TryGetValue(ip, out var perIp);
                if (perIp >= MaxConnectionsPerIp)
                {
                    _server.LogWarn($"[UOBridge] Refusing connection from {ip}: per-IP limit ({MaxConnectionsPerIp}) reached");
                    return false;
                }
                _perIp[ip] = perIp + 1;
            }
            _connectionCount++;
        }
        return true;
    }

    private void ReleaseConnection(IPAddress? ip)
    {
        lock (_connLock)
        {
            if (_connectionCount > 0)
                _connectionCount--;
            if (ip != null && _perIp.TryGetValue(ip, out var perIp))
            {
                if (perIp <= 1)
                    _perIp.Remove(ip);
                else
                    _perIp[ip] = perIp - 1;
            }
        }
    }

    public void Update()
    {
        while (_connectedQueue.TryDequeue(out var client))
        {
            _clients.Add(client);
            _server.LogInfo("[UOBridge] UO client connected");
        }

        long now = Environment.TickCount64;
        foreach (var client in _clients)
        {
            ReceiveClient(client);
            if (!client.Dead && !client.InWorld && now - client.ConnectedTick > AuthTimeoutMs)
            {
                _server.LogWarn($"[UOBridge] Dropping client from {client.RemoteAddress} that did not enter the world within {AuthTimeoutMs / 1000}s");
                client.Dead = true;
            }
        }

        FlushDirtyBlocks();

        foreach (var client in _clients)
        {
            client.Flush();
        }

        foreach (var client in _clients)
        {
            if (client.Dead)
            {
                _toRemove.Add(client);
            }
        }
        foreach (var client in _toRemove)
        {
            _clients.Remove(client);
            client.Dispose();
            ReleaseConnection(client.RemoteAddress);
            if (client.InWorld)
            {
                foreach (var other in _clients)
                {
                    if (!other.Dead && other.KnownMobiles.Remove(client.Serial))
                    {
                        SendDeleteObject(other, client.Serial);
                    }
                }
                BroadcastPlayerRemovalToEditors(client.Serial);
            }
            _server.LogInfo("[UOBridge] UO client disconnected");
        }
        _toRemove.Clear();
    }

    #region Receive & framing

    private void ReceiveClient(UOBridgeClient client)
    {
        if (client.Dead)
            return;
        try
        {
            var socket = client.Socket;
            bool closed = socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0;
            int readThisTick = 0;
            while (socket.Available > 0 && readThisTick < MaxBytesPerTick && !client.Dead)
            {
                int free = client.RecvBuffer.Length - client.RecvLength;
                if (free == 0)
                {
                    client.Dead = true;
                    return;
                }
                int read = socket.Receive(client.RecvBuffer, client.RecvLength, free, SocketFlags.None);
                if (read <= 0)
                    break;
                client.RecvLength += read;
                readThisTick += read;
                ProcessBuffer(client);
            }
            if (closed)
            {
                client.Dead = true;
            }
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock)
        {
        }
        catch (Exception)
        {
            client.Dead = true;
        }
    }

    private void ProcessBuffer(UOBridgeClient client)
    {
        int pos = 0;
        var buffer = client.RecvBuffer;

        while (pos < client.RecvLength && !client.Dead)
        {
            int available = client.RecvLength - pos;

            if (!client.SeedReceived)
            {
                if (buffer[pos] != 0xEF)
                {
                    if (available < 4)
                        break;
                    pos += 4;
                    client.SeedReceived = true;
                    continue;
                }
                client.SeedReceived = true;
            }

            byte id = buffer[pos];
            int length = GetPacketLength(id, client.ClientVersion);
            if (length == -1)
            {
                if (available < 3)
                    break;
                length = (buffer[pos + 1] << 8) | buffer[pos + 2];
                if (length < 3 || length > 0x8000)
                {
                    _server.LogError($"[UOBridge] Bad length {length} for packet 0x{id:X2}, dropping client");
                    client.Dead = true;
                    return;
                }
            }
            else if (length == 0)
            {
                _server.LogError($"[UOBridge] Unknown packet 0x{id:X2}, dropping client");
                client.Dead = true;
                return;
            }

            if (available < length)
                break;

            HandlePacket(client, buffer.AsSpan(pos, length));
            pos += length;
        }

        if (pos > 0)
        {
            Buffer.BlockCopy(buffer, pos, buffer, 0, client.RecvLength - pos);
            client.RecvLength -= pos;
        }
    }

    private static readonly short[] PacketLengths =
    {
        0x68, 0x05, 0x07, -1, 0x02, 0x05, 0x05, 0x07, 0x0F, 0x05, 0x0B, 0x07, -1, 0x03, -1, 0x3D,
        0xD7, -1, -1, 0x0A, 0x06, 0x09, -1, -1, -1, -1, -1, 0x25, -1, 0x05, 0x04, 0x08,
        0x13, 0x08, 0x03, 0x1A, 0x07, 0x15, 0x05, 0x02, 0x05, 0x01, 0x05, 0x02, 0x02, 0x11, 0x0F, 0x0A,
        0x05, -1, 0x02, 0x02, 0x0A, 0x28D, -1, 0x08, 0x07, 0x09, -1, -1, -1, 0x02, 0x25, -1,
        0xC9, -1, -1, 0x229, 0x2C9, 0x05, -1, 0x0B, 0x49, 0x5D, 0x05, 0x09, -1, -1, 0x06, 0x02,
        -1, -1, -1, 0x02, 0x0C, 0x01, 0x0B, 0x6E, 0x6A, -1, -1, 0x04, 0x02, 0x49, -1, 0x31,
        0x05, 0x09, 0x0F, 0x0D, 0x01, 0x04, -1, 0x15, -1, -1, 0x03, 0x09, 0x13, 0x03, 0x0E, -1,
        0x1C, -1, 0x05, 0x02, -1, 0x23, 0x10, 0x11, -1, 0x09, -1, 0x02, -1, 0x0D, 0x02, -1,
        0x3E, -1, 0x02, 0x27, 0x45, 0x02, -1, -1, 0x42, -1, -1, -1, 0x0B, -1, -1, -1,
        0x13, 0x41, -1, 0x63, -1, 0x09, -1, 0x02, -1, 0x1A, -1, 0x102, 0x135, 0x33, -1, -1,
        0x03, 0x09, 0x09, 0x09, 0x95, -1, -1, 0x04, -1, -1, 0x05, -1, -1, -1, -1, 0x0D,
        -1, -1, -1, -1, -1, 0x40, 0x09, -1, -1, 0x05, 0x06, 0x09, 0x03, -1, -1, -1,
        0x24, -1, -1, -1, 0x06, 0xCB, 0x01, 0x31, 0x02, 0x06, 0x06, 0x07, -1, 0x01, -1, 0x4E,
        -1, 0x02, 0x19, -1, -1, -1, -1, -1, -1, 0x10C, -1, -1, 0x09, -1, -1, -1,
        -1, -1, 0x0A, -1, -1, -1, 0x05, 0x0C, 0x0D, 0x4B, 0x03, -1, -1, -1, 0x0A, 0x15,
        -1, 0x09, 0x19, 0x1A, -1, 0x15, -1, -1, 0x6A, -1, 0x01, 0x02, -1, 0x02, -1, -1,
    };

    private int GetPacketLength(byte id, uint clientVersion)
    {
        switch (id)
        {
            case 0x00:
                return clientVersion >= Cv70180 ? 0x6A : 0x68;
            case 0x08:
                return clientVersion >= Cv6017 ? 0x0F : 0x0E;
            case 0xEF:
                return 0x15;
        }
        return PacketLengths[id];
    }

    #endregion

    #region Packet dispatch

    private void HandlePacket(UOBridgeClient client, ReadOnlySpan<byte> packet)
    {
        switch (packet[0])
        {
            case 0xEF:
                client.ClientVersion = (uint)(packet[8] << 24 | packet[12] << 16 | packet[16] << 8 | packet[20]);
                _server.LogInfo($"[UOBridge] Client version {packet[8]}.{packet[12]}.{packet[16]}.{packet[20]}");
                return;
            case 0x80:
                if (Authenticate(client, packet, 1, 31) == null)
                {
                    SendLoginDenied(client);
                    return;
                }
                SendServerList(client);
                return;
            case 0xA0:
                SendRelay(client);
                return;
            case 0x91:
                client.GamePhase = true;
                var account = Authenticate(client, packet, 5, 35);
                if (account == null)
                {
                    SendLoginDenied(client);
                    return;
                }
                client.Serial = _nextPlayerSerial++;
                client.Account = account;
                client.Username = account.Name;
                SendFeatures(client);
                SendCharacterList(client);
                return;
            case 0x5D:
            case 0x00:
            case 0xF8:
                EnterWorld(client);
                return;
            case 0x73:
                SendPing(client, packet[1]);
                return;
        }

        if (!client.InWorld)
            return;

        switch (packet[0])
        {
            case 0x02:
                HandleWalk(client, packet);
                break;
            case 0x22:
                SendUpdatePlayer(client);
                break;
            case 0x34:
                if (packet[5] == 4)
                {
                    uint statusSerial = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(6));
                    var subject = FindBySerial(statusSerial) ?? client;
                    SendStatus(client, subject);
                }
                break;
            case 0x09:
                uint clickSerial = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(1));
                var clicked = FindBySerial(clickSerial);
                if (clicked != null)
                {
                    SendName(client, clicked);
                }
                break;
            case 0x03:
                if (packet.Length >= 6)
                    HandleSpeech(client, ParseAsciiSpeech(packet), BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(4)));
                break;
            case 0xAD:
                if (packet.Length >= 6)
                    HandleSpeech(client, ParseUnicodeSpeech(packet), BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(4)));
                break;
            case 0x3F:
                HandleHashResponse(client, packet);
                break;
            case 0x6C:
                HandleTargetResponse(client, packet);
                break;
            case 0xB1:
                HandleGumpResponse(client, packet);
                break;
            default:
                break;
        }
    }

    #endregion

    #region Login sequence

    private const int MaxFailedLogins = 5;
    private const long LoginThrottleWindowMs = 60_000;
    private readonly Dictionary<IPAddress, (int failures, long windowStart)> _loginFailures = new();

    private Account? Authenticate(UOBridgeClient client, ReadOnlySpan<byte> packet, int userOffset, int passOffset)
    {
        var ip = (client.Socket.RemoteEndPoint as IPEndPoint)?.Address;
        if (ip != null && IsLoginThrottled(ip))
        {
            _server.LogWarn($"[UOBridge] Throttled UO login from {ip} (too many failed attempts)");
            return null;
        }
        var user = Encoding.ASCII.GetString(packet.Slice(userOffset, 30)).TrimEnd('\0').Trim();
        var password = Encoding.ASCII.GetString(packet.Slice(passOffset, 30)).TrimEnd('\0');
        var account = _server.GetAccount(user);
        if (account == null || account.AccessLevel == AccessLevel.None || !account.CheckPassword(password))
        {
            if (ip != null)
                RecordLoginFailure(ip);
            _server.LogWarn($"[UOBridge] Rejected UO login for '{user}'");
            return null;
        }
        if (ip != null)
            _loginFailures.Remove(ip);
        return account;
    }

    private bool IsLoginThrottled(IPAddress ip)
    {
        if (!_loginFailures.TryGetValue(ip, out var record))
            return false;
        if (Environment.TickCount64 - record.windowStart > LoginThrottleWindowMs)
        {
            _loginFailures.Remove(ip);
            return false;
        }
        return record.failures >= MaxFailedLogins;
    }

    private void RecordLoginFailure(IPAddress ip)
    {
        var now = Environment.TickCount64;
        if (_loginFailures.TryGetValue(ip, out var record) && now - record.windowStart <= LoginThrottleWindowMs)
        {
            _loginFailures[ip] = (record.failures + 1, record.windowStart);
            return;
        }
        if (_loginFailures.Count > 256)
        {
            foreach (var stale in _loginFailures.Where(e => now - e.Value.windowStart > LoginThrottleWindowMs).Select(e => e.Key).ToList())
                _loginFailures.Remove(stale);
        }
        _loginFailures[ip] = (1, now);
    }

    private void SendLoginDenied(UOBridgeClient client)
    {
        var w = new UOWriter();
        w.WriteByte(0x82);
        w.WriteByte(0x00);
        client.Send(w.Span);
        client.Dead = true;
    }

    private void SendServerList(UOBridgeClient client)
    {
        var w = new UOWriter();
        w.WriteVariableHeader(0xA8);
        w.WriteByte(0x5D);
        w.WriteUInt16BE(1);
        w.WriteUInt16BE(0);
        w.WriteAsciiFixed(_config.ShardName, 32);
        w.WriteByte(0);
        w.WriteByte(0);
        w.WriteUInt32BE(0x7F000001);
        w.PatchLength();
        client.Send(w.Span);
    }

    private void SendRelay(UOBridgeClient client)
    {
        var w = new UOWriter();
        w.WriteByte(0x8C);
        w.WriteUInt32BE(0);
        w.WriteUInt16BE((ushort)Port);
        w.WriteUInt32BE(0xC0FFEE01);
        client.Send(w.Span);
    }

    private void SendFeatures(UOBridgeClient client)
    {
        var w = new UOWriter();
        w.WriteByte(0xB9);
        if (client.ClientVersion >= CvFeaturesWide)
        {
            w.WriteUInt32BE(0x000000FF);
        }
        else
        {
            w.WriteUInt16BE(0x00FF);
        }
        client.Send(w.Span);
    }

    private void SendCharacterList(UOBridgeClient client)
    {
        var spawn = GetSpawnPoint(client);
        var w = new UOWriter();
        w.WriteVariableHeader(0xA9);
        w.WriteByte(1);
        w.WriteAsciiFixed(client.Username, 30);
        w.WriteZero(30);
        w.WriteByte(1);
        if (client.ClientVersion >= CvNewCityFormat)
        {
            w.WriteByte(0);
            w.WriteAsciiFixed(_config.ShardName, 32);
            w.WriteAsciiFixed("Editor", 32);
            w.WriteUInt32BE(spawn.x);
            w.WriteUInt32BE(spawn.y);
            w.WriteUInt32BE((uint)(int)spawn.z);
            w.WriteUInt32BE((uint)ClientMapIndex);
            w.WriteUInt32BE(1075072);
            w.WriteZero(4);
        }
        else
        {
            w.WriteByte(0);
            w.WriteAsciiFixed(_config.ShardName, 31);
            w.WriteAsciiFixed("Editor", 31);
        }
        w.WriteUInt32BE(0x08);
        w.PatchLength();
        client.Send(w.Span);
    }

    private void EnterWorld(UOBridgeClient client)
    {
        if (client.Account == null)
        {
            SendLoginDenied(client);
            return;
        }
        var spawn = GetSpawnPoint(client);
        client.X = (ushort)spawn.x;
        client.Y = (ushort)spawn.y;
        client.Z = spawn.z;
        client.Direction = 4;

        var w = new UOWriter();
        w.WriteByte(0x1B);
        w.WriteUInt32BE(client.Serial);
        w.WriteZero(4);
        w.WriteUInt16BE(PlayerBody);
        w.WriteUInt16BE(client.X);
        w.WriteUInt16BE(client.Y);
        w.WriteUInt16BE((ushort)(short)client.Z);
        w.WriteByte(client.Direction);
        w.WriteZero(37 - 18);
        client.Send(w.Span);

        w = new UOWriter();
        w.WriteVariableHeader(0xBF);
        w.WriteUInt16BE(0x08);
        w.WriteByte(ClientMapIndex);
        w.PatchLength();
        client.Send(w.Span);

        SendUpdatePlayer(client);
        SendMobileTo(client, client);
        SendAttributes(client);

        w = new UOWriter();
        w.WriteByte(0x4F);
        w.WriteByte(0);
        client.Send(w.Span);

        w = new UOWriter();
        w.WriteByte(0x55);
        client.Send(w.Span);

        bool seeded = SeedLocalClientFiles(client);
        SendUltimaLiveIntro(client, seeded);
        SendHashQuery(client);

        client.InWorld = true;
        UpdateVisibility(client, moving: false);
        BroadcastPlayerToEditors(client);
        _server.LogInfo($"[UOBridge] {client.Username} entered world at {client.X},{client.Y},{client.Z}");
        SendSystemMessage(client, $"Welcome to {_config.ShardName}. Say 'help' for commands.");
        SendCommandGump(client);
    }

    private (uint x, uint y, sbyte z) GetSpawnPoint(UOBridgeClient client)
    {
        var landscape = _server.Landscape;
        uint x;
        uint y;
        var lastPos = client.Account?.LastPos;
        if (lastPos != null && (lastPos.X != 0 || lastPos.Y != 0)
            && lastPos.X < landscape.WidthInTiles && lastPos.Y < landscape.HeightInTiles)
        {
            x = lastPos.X;
            y = lastPos.Y;
        }
        else
        {
            x = _config.StartX >= 0 ? (uint)_config.StartX : (uint)(landscape.WidthInTiles / 2);
            y = _config.StartY >= 0 ? (uint)_config.StartY : (uint)(landscape.HeightInTiles / 2);
        }
        x = Math.Min(x, (uint)(landscape.WidthInTiles - 1));
        y = Math.Min(y, (uint)(landscape.HeightInTiles - 1));
        return (x, y, GetLandZ((ushort)x, (ushort)y));
    }

    private sbyte GetLandZ(ushort x, ushort y)
    {
        try
        {
            return _server.Landscape.GetLandTile(x, y).Z;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    #endregion

    #region World state packets

    private void SendMobileTo(UOBridgeClient viewer, UOBridgeClient subject)
    {
        var w = new UOWriter();
        w.WriteVariableHeader(0x78);
        w.WriteUInt32BE(subject.Serial);
        w.WriteUInt16BE(PlayerBody);
        w.WriteUInt16BE(subject.X);
        w.WriteUInt16BE(subject.Y);
        w.WriteSByte(subject.Z);
        w.WriteByte(subject.Direction);
        w.WriteUInt16BE(PlayerHue);
        w.WriteByte(0);
        w.WriteByte(1);
        if (subject.Mounted)
        {
            w.WriteUInt32BE(subject.MountSerial);
            if (viewer.ClientVersion >= CvEquipHue)
            {
                w.WriteUInt16BE(HorseGraphic);
                w.WriteByte(LayerMount);
                w.WriteUInt16BE(0);
            }
            else
            {
                w.WriteUInt16BE(HorseGraphic);
                w.WriteByte(LayerMount);
            }
        }
        w.WriteUInt32BE(0);
        w.PatchLength();
        viewer.Send(w.Span);
    }

    private void SendMobileMoving(UOBridgeClient viewer, UOBridgeClient subject)
    {
        var w = new UOWriter(17);
        w.WriteByte(0x77);
        w.WriteUInt32BE(subject.Serial);
        w.WriteUInt16BE(PlayerBody);
        w.WriteUInt16BE(subject.X);
        w.WriteUInt16BE(subject.Y);
        w.WriteSByte(subject.Z);
        w.WriteByte((byte)(subject.Direction | (subject.LastMoveRun ? 0x80 : 0)));
        w.WriteUInt16BE(PlayerHue);
        w.WriteByte(0);
        w.WriteByte(1);
        viewer.Send(w.Span);
    }

    private void SendUpdatePlayer(UOBridgeClient client)
    {
        var w = new UOWriter();
        w.WriteByte(0x20);
        w.WriteUInt32BE(client.Serial);
        w.WriteUInt16BE(PlayerBody);
        w.WriteByte(0);
        w.WriteUInt16BE(PlayerHue);
        w.WriteByte(0);
        w.WriteUInt16BE(client.X);
        w.WriteUInt16BE(client.Y);
        w.WriteUInt16BE(0);
        w.WriteByte(client.Direction);
        w.WriteSByte(client.Z);
        client.Send(w.Span);
    }

    private void SendAttributes(UOBridgeClient client)
    {
        var w = new UOWriter();
        w.WriteByte(0x2D);
        w.WriteUInt32BE(client.Serial);
        w.WriteUInt16BE(100);
        w.WriteUInt16BE(100);
        w.WriteUInt16BE(100);
        w.WriteUInt16BE(100);
        w.WriteUInt16BE(100);
        w.WriteUInt16BE(100);
        client.Send(w.Span);
    }

    private void SendPing(UOBridgeClient client, byte idx)
    {
        var w = new UOWriter();
        w.WriteByte(0x73);
        w.WriteByte(idx);
        client.Send(w.Span);
    }

    private void SendStatus(UOBridgeClient client, UOBridgeClient subject)
    {
        var w = new UOWriter();
        w.WriteVariableHeader(0x11);
        w.WriteUInt32BE(subject.Serial);
        w.WriteAsciiFixed(subject.Username, 30);
        w.WriteUInt16BE(100);
        w.WriteUInt16BE(100);
        w.WriteByte(0);
        w.WriteByte(0);
        w.PatchLength();
        client.Send(w.Span);
    }

    private void SendSystemMessage(UOBridgeClient client, string text)
    {
        var w = new UOWriter();
        w.WriteVariableHeader(0x1C);
        w.WriteUInt32BE(0xFFFFFFFF);
        w.WriteUInt16BE(0xFFFF);
        w.WriteByte(6);
        w.WriteUInt16BE(0x03B2);
        w.WriteUInt16BE(3);
        w.WriteAsciiFixed("System", 30);
        w.WriteAsciiNullTerminated(text);
        w.PatchLength();
        client.Send(w.Span);
    }

    #endregion

    #region Movement

    private static readonly int[] OffsetX = { 0, 1, 1, 1, 0, -1, -1, -1 };
    private static readonly int[] OffsetY = { -1, -1, 0, 1, 1, 1, 0, -1 };

    private void HandleWalk(UOBridgeClient client, ReadOnlySpan<byte> packet)
    {
        byte direction = (byte)(packet[1] & 0x07);
        byte sequence = packet[2];
        client.LastMoveRun = (packet[1] & 0x80) != 0;

        if (direction == client.Direction)
        {
            var landscape = _server.Landscape;
            int nx = client.X + OffsetX[direction];
            int ny = client.Y + OffsetY[direction];
            if (nx >= 0 && ny >= 0 && nx < landscape.WidthInTiles && ny < landscape.HeightInTiles)
            {
                client.X = (ushort)nx;
                client.Y = (ushort)ny;
                client.Z = GetLandZ(client.X, client.Y);
                if (client.X / 8 != client.LastQueryBlockX || client.Y / 8 != client.LastQueryBlockY)
                {
                    SendHashQuery(client);
                }
            }
        }
        else
        {
            client.Direction = direction;
        }
        UpdateVisibility(client, moving: true);
        BroadcastPlayerToEditors(client);

        var w = new UOWriter();
        w.WriteByte(0x22);
        w.WriteByte(sequence);
        w.WriteByte(0x01);
        client.Send(w.Span);
    }

    #endregion

    #region Speech commands

    private static string ParseAsciiSpeech(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 9 || (packet[3] & 0xC0) != 0)
            return "";
        var text = packet.Slice(8);
        int end = text.IndexOf((byte)0);
        if (end >= 0)
            text = text.Slice(0, end);
        return Encoding.ASCII.GetString(text);
    }

    private static string ParseUnicodeSpeech(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 14 || (packet[3] & 0xC0) != 0)
            return "";
        var text = packet.Slice(12);
        int chars = text.Length / 2;
        int end = chars;
        for (int i = 0; i < chars; i++)
        {
            if (text[i * 2] == 0 && text[i * 2 + 1] == 0)
            {
                end = i;
                break;
            }
        }
        return Encoding.BigEndianUnicode.GetString(text.Slice(0, end * 2));
    }

    private void HandleSpeech(UOBridgeClient client, string text, ushort hue)
    {
        if (!client.InWorld || string.IsNullOrWhiteSpace(text))
            return;
        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "where":
                SendSystemMessage(client, $"Position: {client.X}, {client.Y}, {client.Z}");
                break;
            case "go" when parts.Length >= 3
                           && ushort.TryParse(parts[1], out var gx)
                           && ushort.TryParse(parts[2], out var gy):
                Teleport(client, gx, gy);
                break;
            case "menu":
                SendCommandGump(client);
                break;
            case "mount":
                SetMounted(client, true);
                break;
            case "dismount":
                SetMounted(client, false);
                break;
            case "tele":
                SendTargetRequest(client, UOBridgeClient.TargetKind.Tele, "Target the tile to teleport to.");
                break;
            case "help":
                SendSystemMessage(client, "Available commands:");
                SendSystemMessage(client, "  tele - target a tile and teleport to it");
                SendSystemMessage(client, "  go <x> <y> - teleport to coordinates");
                SendSystemMessage(client, "  where - show your current position");
                SendSystemMessage(client, "  mount / dismount - get on or off the horse");
                SendSystemMessage(client, "  menu - reopen the command panel");
                SendSystemMessage(client, "  help - this list");
                SendSystemMessage(client, "Anything else is said out loud to nearby players.");
                break;
            default:
                BroadcastSpeech(client, text, hue);
                break;
        }
    }

    private void SetMounted(UOBridgeClient client, bool mounted)
    {
        if (client.Mounted == mounted)
            return;
        client.Mounted = mounted;
        foreach (var viewer in _clients)
        {
            bool self = viewer == client;
            if (!self && (viewer.Dead || !viewer.InWorld || !viewer.KnownMobiles.Contains(client.Serial)))
                continue;
            if (!mounted)
            {
                SendDeleteObject(viewer, client.MountSerial);
            }
            SendMobileTo(viewer, client);
        }
        BroadcastPlayerToEditors(client);
    }

    private void BroadcastSpeech(UOBridgeClient speaker, string text, ushort hue)
    {
        if (hue == 0)
        {
            hue = 0x02B2;
        }
        foreach (var viewer in _clients)
        {
            if (viewer.Dead || !viewer.InWorld)
                continue;
            if (viewer != speaker && Distance(viewer, speaker) > ViewRange)
                continue;
            var w = new UOWriter();
            w.WriteVariableHeader(0xAE);
            w.WriteUInt32BE(speaker.Serial);
            w.WriteUInt16BE(PlayerBody);
            w.WriteByte(0);
            w.WriteUInt16BE(hue);
            w.WriteUInt16BE(3);
            w.WriteAsciiFixed("ENU", 4);
            w.WriteAsciiFixed(speaker.Username, 30);
            w.WriteBytes(Encoding.BigEndianUnicode.GetBytes(text));
            w.WriteZero(2);
            w.PatchLength();
            viewer.Send(w.Span);
        }
    }

    private UOBridgeClient? FindBySerial(uint serial)
    {
        foreach (var client in _clients)
        {
            if (client.InWorld && !client.Dead && client.Serial == serial)
                return client;
        }
        return null;
    }

    private void SendName(UOBridgeClient viewer, UOBridgeClient subject)
    {
        var w = new UOWriter();
        w.WriteVariableHeader(0x98);
        w.WriteUInt32BE(subject.Serial);
        w.WriteAsciiNullTerminated(subject.Username);
        w.PatchLength();
        viewer.Send(w.Span);
    }

    private static int Distance(UOBridgeClient a, UOBridgeClient b)
    {
        return Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    private void BroadcastPlayerToEditors(UOBridgeClient player)
    {
        var packet = new GamePlayerPacket(player.Serial, player.Username, player.X, player.Y, player.Z, player.Mounted);
        foreach (var ns in _server.Clients)
        {
            ns.Send(packet);
        }
    }

    private void BroadcastPlayerRemovalToEditors(uint serial)
    {
        var packet = new GamePlayerPacket(serial);
        foreach (var ns in _server.Clients)
        {
            ns.Send(packet);
        }
    }

    public void SendPlayerSnapshot(NetState<CEDServer> ns)
    {
        foreach (var client in _clients)
        {
            if (client.InWorld && !client.Dead)
            {
                ns.Send(new GamePlayerPacket(client.Serial, client.Username, client.X, client.Y, client.Z, client.Mounted));
            }
        }
    }

    private void UpdateVisibility(UOBridgeClient subject, bool moving)
    {
        foreach (var other in _clients)
        {
            if (other == subject || other.Dead || !other.InWorld || !subject.InWorld)
                continue;
            UpdatePairVisibility(other, subject, moving);
            UpdatePairVisibility(subject, other, false);
        }
    }

    private void UpdatePairVisibility(UOBridgeClient viewer, UOBridgeClient subject, bool moving)
    {
        bool inRange = Distance(viewer, subject) <= ViewRange;
        bool known = viewer.KnownMobiles.Contains(subject.Serial);
        if (inRange && !known)
        {
            viewer.KnownMobiles.Add(subject.Serial);
            SendMobileTo(viewer, subject);
        }
        else if (inRange && moving)
        {
            SendMobileMoving(viewer, subject);
        }
        else if (!inRange && known)
        {
            viewer.KnownMobiles.Remove(subject.Serial);
            SendDeleteObject(viewer, subject.Serial);
        }
    }

    private void Teleport(UOBridgeClient client, ushort x, ushort y)
    {
        var landscape = _server.Landscape;
        client.X = Math.Min(x, (ushort)(landscape.WidthInTiles - 1));
        client.Y = Math.Min(y, (ushort)(landscape.HeightInTiles - 1));
        client.Z = GetLandZ(client.X, client.Y);
        SendUpdatePlayer(client);
        UpdateVisibility(client, moving: true);
        BroadcastPlayerToEditors(client);
        if (client.X / 8 != client.LastQueryBlockX || client.Y / 8 != client.LastQueryBlockY)
        {
            SendHashQuery(client);
        }
        SendSystemMessage(client, $"Teleported to {client.X}, {client.Y}");
    }

    private void SendDeleteObject(UOBridgeClient client, uint serial)
    {
        var w = new UOWriter();
        w.WriteByte(0x1D);
        w.WriteUInt32BE(serial);
        client.Send(w.Span);
    }

    private void SendTargetRequest(UOBridgeClient client, UOBridgeClient.TargetKind kind, string? prompt = null)
    {
        client.PendingTarget = kind;
        var w = new UOWriter();
        w.WriteByte(0x6C);
        w.WriteByte(1);
        w.WriteUInt32BE(TeleCursorId);
        w.WriteByte(0);
        w.WriteZero(12);
        client.Send(w.Span);
        if (prompt != null)
        {
            SendSystemMessage(client, prompt);
        }
    }

    private const uint TeleCursorId = 0x0CED0CED;
    private const uint CommandGumpId = 0x0CED0001;
    private const int GumpButtonTele = 1;
    private const int GumpButtonMountToggle = 2;
    private const int GumpButtonWhere = 3;
    private const int GumpButtonGo = 4;
    private const int GumpButtonZUp = 5;
    private const int GumpButtonZDown = 6;
    private const int GumpButtonZGet = 7;
    private const int GumpButtonZSet = 8;

    private void SendCommandGump(UOBridgeClient client)
    {
        string layout =
            "{ page 0 }" +
            "{ resizepic 0 0 9270 165 345 }" +
            "{ text 45 12 88 0 }" +
            $"{{ button 15 45 4005 4007 1 0 {GumpButtonTele} }}{{ text 50 45 1152 1 }}" +
            $"{{ button 15 75 4005 4007 1 0 {GumpButtonMountToggle} }}{{ text 50 75 1152 2 }}" +
            $"{{ button 15 105 4005 4007 1 0 {GumpButtonWhere} }}{{ text 50 105 1152 3 }}" +
            "{ text 15 135 88 4 }" +
            "{ resizepic 13 155 3000 65 22 }" +
            "{ textentry 18 157 55 18 0 1 5 }" +
            "{ resizepic 85 155 3000 65 22 }" +
            "{ textentry 90 157 55 18 0 2 6 }" +
            $"{{ button 15 185 4005 4007 1 0 {GumpButtonGo} }}{{ text 50 185 1152 7 }}" +
            "{ text 15 220 88 8 }" +
            $"{{ checkbox 95 220 210 211 {(client.ZAreaMode ? 1 : 0)} 1 }}" +
            "{ text 118 220 1152 12 }" +
            $"{{ button 18 243 2435 2436 1 0 {GumpButtonZUp} }}" +
            "{ resizepic 48 242 3000 60 22 }" +
            "{ textentry 53 244 50 18 0 3 9 }" +
            $"{{ button 118 243 2437 2438 1 0 {GumpButtonZDown} }}" +
            $"{{ button 15 275 4005 4007 1 0 {GumpButtonZGet} }}{{ text 50 275 1152 10 }}" +
            $"{{ button 15 305 4005 4007 1 0 {GumpButtonZSet} }}{{ text 50 305 1152 11 }}";

        string[] lines =
        {
            _config.ShardName,
            "Teleport (target)",
            client.Mounted ? "Dismount" : "Mount",
            "Where am I",
            "Go to X / Y:",
            client.X.ToString(),
            client.Y.ToString(),
            "Go",
            "Z height:",
            client.ZAmountText,
            "Get Z (target)",
            "Set Z (target)",
            "Area",
        };

        var w = new UOWriter();
        w.WriteVariableHeader(0xB0);
        w.WriteUInt32BE(client.Serial);
        w.WriteUInt32BE(CommandGumpId);
        w.WriteUInt32BE(40);
        w.WriteUInt32BE(40);
        w.WriteUInt16BE((ushort)layout.Length);
        w.WriteBytes(Encoding.ASCII.GetBytes(layout));
        w.WriteUInt16BE((ushort)lines.Length);
        foreach (var line in lines)
        {
            w.WriteUInt16BE((ushort)line.Length);
            w.WriteBytes(Encoding.BigEndianUnicode.GetBytes(line));
        }
        w.PatchLength();
        client.Send(w.Span);
    }

    private void HandleGumpResponse(UOBridgeClient client, ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 23 || !client.InWorld)
            return;
        uint gumpId = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(7));
        if (gumpId != CommandGumpId)
            return;
        int button = (int)BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(11));
        if (button == 0)
            return;

        var switches = new HashSet<uint>();
        var entries = new Dictionary<ushort, string>();
        int pos = 15;
        uint switchCount = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(pos));
        pos += 4;
        for (uint i = 0; i < switchCount && pos + 4 <= packet.Length; i++, pos += 4)
        {
            switches.Add(BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(pos)));
        }
        if (pos + 4 <= packet.Length)
        {
            uint entryCount = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(pos));
            pos += 4;
            for (uint i = 0; i < entryCount && pos + 4 <= packet.Length; i++)
            {
                ushort entryId = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(pos));
                int textLen = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(pos + 2)) * 2;
                pos += 4;
                if (pos + textLen > packet.Length)
                    break;
                entries[entryId] = Encoding.BigEndianUnicode.GetString(packet.Slice(pos, textLen));
                pos += textLen;
            }
        }

        if (entries.TryGetValue(3, out var zText) && !string.IsNullOrWhiteSpace(zText))
        {
            client.ZAmountText = zText.Trim();
        }
        client.ZAreaMode = switches.Contains(1);

        switch (button)
        {
            case GumpButtonTele:
                SendTargetRequest(client, UOBridgeClient.TargetKind.Tele, "Target the tile to teleport to.");
                break;
            case GumpButtonMountToggle:
                SetMounted(client, !client.Mounted);
                break;
            case GumpButtonWhere:
                SendSystemMessage(client, $"Position: {client.X}, {client.Y}, {client.Z}");
                break;
            case GumpButtonGo:
                if (entries.TryGetValue(1, out var xText) && entries.TryGetValue(2, out var yText)
                    && ushort.TryParse(xText.Trim(), out var gx) && ushort.TryParse(yText.Trim(), out var gy))
                {
                    Teleport(client, gx, gy);
                }
                else
                {
                    SendSystemMessage(client, "Enter numeric X and Y coordinates.");
                }
                break;
            case GumpButtonZUp:
            case GumpButtonZDown:
            case GumpButtonZSet:
                if (client.Account == null || client.Account.AccessLevel < AccessLevel.Normal)
                {
                    SendSystemMessage(client, "Your account does not have edit access.");
                    break;
                }
                if (!int.TryParse(client.ZAmountText, out var amount))
                {
                    SendSystemMessage(client, "Enter a numeric Z value.");
                    break;
                }
                client.PendingZAmount = button == GumpButtonZDown ? -amount : amount;
                SendTargetRequest(client,
                    button == GumpButtonZSet ? UOBridgeClient.TargetKind.ZSet : UOBridgeClient.TargetKind.ZAdd,
                    button switch
                    {
                        GumpButtonZUp => $"Target what to raise by {amount}.",
                        GumpButtonZDown => $"Target what to lower by {amount}.",
                        _ => $"Target what to set to z {amount}.",
                    });
                break;
            case GumpButtonZGet:
                SendTargetRequest(client, UOBridgeClient.TargetKind.ZGet, "Target to read its Z.");
                break;
        }
        SendCommandGump(client);
    }

    private void HandleTargetResponse(UOBridgeClient client, ReadOnlySpan<byte> packet)
    {
        if (!client.InWorld)
            return;
        var kind = client.PendingTarget;
        client.PendingTarget = UOBridgeClient.TargetKind.None;
        uint serial = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(7));
        ushort x = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(11));
        ushort y = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(13));
        sbyte z = (sbyte)packet[16];
        ushort graphic = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(17));
        var landscape = _server.Landscape;
        if (x == 0xFFFF || x >= landscape.WidthInTiles || y >= landscape.HeightInTiles)
        {
            client.AreaCorner = null;
            if (kind == UOBridgeClient.TargetKind.ZAdd)
            {
                SendSystemMessage(client, "Z editing finished.");
            }
            return;
        }

        switch (kind)
        {
            case UOBridgeClient.TargetKind.Tele:
                client.X = x;
                client.Y = y;
                client.Z = Math.Max(z, GetLandZ(x, y));
                SendUpdatePlayer(client);
                UpdateVisibility(client, moving: true);
                BroadcastPlayerToEditors(client);
                if (client.X / 8 != client.LastQueryBlockX || client.Y / 8 != client.LastQueryBlockY)
                {
                    SendHashQuery(client);
                }
                SendSystemMessage(client, $"Teleported to {client.X}, {client.Y}, {client.Z}");
                break;

            case UOBridgeClient.TargetKind.ZGet:
                client.ZAmountText = z.ToString();
                SendSystemMessage(client, $"Z of target: {z}");
                SendCommandGump(client);
                break;

            case UOBridgeClient.TargetKind.ZAdd:
            case UOBridgeClient.TargetKind.ZSet:
                if (client.ZAreaMode)
                {
                    if (client.AreaCorner == null)
                    {
                        client.AreaCorner = (x, y);
                        SendTargetRequest(client, kind, "Select the opposite corner.");
                        break;
                    }
                    var corner = client.AreaCorner.Value;
                    client.AreaCorner = null;
                    ApplyAreaZEdit(client, kind, corner.X, corner.Y, x, y);
                }
                else
                {
                    ApplyZEdit(client, kind, serial, x, y, z, graphic);
                }
                if (kind == UOBridgeClient.TargetKind.ZAdd)
                {
                    SendTargetRequest(client, UOBridgeClient.TargetKind.ZAdd);
                }
                break;
        }
    }

    private const int MaxAreaEdge = 128;

    private void ApplyAreaZEdit(UOBridgeClient client, UOBridgeClient.TargetKind kind, ushort x1, ushort y1, ushort x2, ushort y2)
    {
        var minX = Math.Min(x1, x2);
        var maxX = Math.Max(x1, x2);
        var minY = Math.Min(y1, y2);
        var maxY = Math.Max(y1, y2);
        int width = maxX - minX + 1;
        int height = maxY - minY + 1;
        if (width > MaxAreaEdge || height > MaxAreaEdge)
        {
            SendSystemMessage(client, $"Area too large ({width}x{height}); the limit is {MaxAreaEdge}x{MaxAreaEdge}.");
            return;
        }

        bool absolute = kind == UOBridgeClient.TargetKind.ZSet;
        Func<ushort, ushort, bool>? allowed = IsUnrestricted(client) ? null : (tx, ty) => CanEdit(client, tx, ty);
        var (edited, denied) = _server.Landscape.AreaSetZ(_server, (ushort)minX, (ushort)minY, (ushort)maxX, (ushort)maxY, client.PendingZAmount, absolute, allowed);
        if (edited == 0 && denied > 0)
        {
            SendSystemMessage(client, "You do not have permission to edit that area.");
            return;
        }
        SendSystemMessage(client, absolute
            ? $"Area {width}x{height} at {minX},{minY}: z set to {client.PendingZAmount}"
            : $"Area {width}x{height} at {minX},{minY}: z {(client.PendingZAmount >= 0 ? "+" : "")}{client.PendingZAmount}");
        if (denied > 0)
        {
            SendSystemMessage(client, $"{denied} tiles outside your allowed regions were skipped.");
        }
        _server.LogInfo($"[UOBridge] {client.Username} area z edit {minX},{minY}-{maxX},{maxY} ({(absolute ? "set" : "add")} {client.PendingZAmount}, {denied} denied)");
    }

    private bool CanEdit(UOBridgeClient client, ushort x, ushort y)
    {
        var account = client.Account;
        if (account == null || account.AccessLevel < AccessLevel.Normal)
            return false;
        if (account.Regions.Count == 0 || account.AccessLevel >= AccessLevel.Administrator)
            return true;
        foreach (var regionName in account.Regions)
        {
            var region = _server.GetRegion(regionName);
            if (region != null && region.Area.Any(a => a.Contains(x, y)))
                return true;
        }
        return false;
    }

    private bool IsUnrestricted(UOBridgeClient client)
    {
        var account = client.Account;
        return account != null
               && (account.Regions.Count == 0 || account.AccessLevel >= AccessLevel.Administrator);
    }

    private void ApplyZEdit(UOBridgeClient client, UOBridgeClient.TargetKind kind, uint serial, ushort x, ushort y, sbyte targetedZ, ushort graphic)
    {
        if (serial != 0)
        {
            SendSystemMessage(client, "Target a map tile or a static, not a player.");
            return;
        }
        if (!CanEdit(client, x, y))
        {
            SendSystemMessage(client, "You do not have permission to edit that location.");
            return;
        }
        var landscape = _server.Landscape;
        if (graphic == 0)
        {
            var current = GetLandZ(x, y);
            var newZ = (sbyte)Math.Clamp(
                kind == UOBridgeClient.TargetKind.ZSet ? client.PendingZAmount : current + client.PendingZAmount,
                sbyte.MinValue, sbyte.MaxValue);
            landscape.SetLandZ(_server, x, y, newZ);
            SendSystemMessage(client, $"Land at {x},{y}: z {current} -> {newZ}");
        }
        else
        {
            var tile = landscape.FindStatic(x, y, graphic, targetedZ);
            if (tile == null)
            {
                SendSystemMessage(client, "Could not match that static on the server.");
                return;
            }
            var current = tile.Z;
            var newZ = (sbyte)Math.Clamp(
                kind == UOBridgeClient.TargetKind.ZSet ? client.PendingZAmount : current + client.PendingZAmount,
                sbyte.MinValue, sbyte.MaxValue);
            landscape.SetStaticZ(_server, tile, newZ);
            SendSystemMessage(client, $"Static 0x{graphic:X4} at {x},{y}: z {current} -> {newZ}");
        }
    }

    #endregion

    #region UltimaLive streaming

    private bool SeedLocalClientFiles(UOBridgeClient client)
    {
        if (!_config.SeedLocalClient)
            return false;
        if (_server.Landscape.IsUop)
        {
            _server.LogWarn("[UOBridge] Map is UOP format, skipping client file seeding (CRC healing will sync instead)");
            return false;
        }
        if (client.Socket.RemoteEndPoint is not IPEndPoint remote || !IPAddress.IsLoopback(remote.Address))
            return false;

        try
        {
            var folder = Environment.GetFolderPath(OperatingSystem.IsWindows()
                ? Environment.SpecialFolder.CommonApplicationData
                : Environment.SpecialFolder.LocalApplicationData);
            var shardDir = Path.GetFullPath(Path.Combine(folder, _config.ShardName));
            var sources = _server.Landscape.PrepareExport(ClientMapIndex);
            _server.LogInfo($"[UOBridge] Seeding full map into {shardDir}");
            ServerLandscape.ExportCopy(shardDir, sources);
            _server.LogInfo("[UOBridge] Map seeding complete");
            return true;
        }
        catch (Exception e)
        {
            _server.LogWarn($"[UOBridge] Could not seed client files: {e.Message}");
            return false;
        }
    }

    private void SendUltimaLiveIntro(UOBridgeClient client, bool seeded)
    {
        var landscape = _server.Landscape;

        if (!seeded)
        {
            _server.LogWarn(
                $"[UOBridge] Client at {client.RemoteAddress} was not seeded; it must already have map{ClientMapIndex} " +
                $"files matching the server dimensions ({landscape.Width}x{landscape.Height} blocks, " +
                $"{landscape.WidthInTiles}x{landscape.HeightInTiles} tiles). If they differ, the live view will be " +
                "misaligned and CRC healing will patch the wrong blocks.");
        }

        var w = new UOWriter();
        w.WriteVariableHeader(0x3F);
        w.WriteUInt32BE(0);
        w.WriteUInt32BE(0);
        w.WriteZero(2);
        w.WriteByte(0x02);
        w.WriteByte(0);
        w.WriteAsciiFixed(_config.ShardName, 43 - 15);
        w.PatchLength();
        client.Send(w.Span);

        const int defBytes = 9;
        int count = (defBytes + 6) / 7;
        w = new UOWriter();
        w.WriteVariableHeader(0x3F);
        w.WriteUInt32BE(0);
        w.WriteUInt32BE((uint)count);
        w.WriteZero(2);
        w.WriteByte(0x01);
        w.WriteByte(0);
        w.WriteByte(ClientMapIndex);
        w.WriteUInt16BE(landscape.WidthInTiles);
        w.WriteUInt16BE(landscape.HeightInTiles);
        w.WriteUInt16BE(landscape.WidthInTiles);
        w.WriteUInt16BE(landscape.HeightInTiles);
        w.WriteZero(count * 7 - defBytes);
        w.PatchLength();
        client.Send(w.Span);
    }

    private void SendHashQuery(UOBridgeClient client)
    {
        var landscape = _server.Landscape;
        var blockX = (ushort)(client.X / 8);
        var blockY = (ushort)(client.Y / 8);
        client.LastQueryBlockX = blockX;
        client.LastQueryBlockY = blockY;

        var w = new UOWriter();
        w.WriteVariableHeader(0x3F);
        w.WriteUInt32BE(landscape.BlockIndex(blockX, blockY));
        w.WriteZero(6);
        w.WriteByte(0xFF);
        w.WriteByte(ClientMapIndex);
        w.PatchLength();
        client.Send(w.Span);
    }

    private void HandleHashResponse(UOBridgeClient client, ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 15 + 50 || packet[13] != 0xFF)
        {
            return;
        }

        var landscape = _server.Landscape;
        uint block = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(3));
        int blockX = (int)(block / landscape.Height);
        int blockY = (int)(block % landscape.Height);

        for (int dx = -2; dx <= 2; dx++)
        {
            for (int dy = -2; dy <= 2; dy++)
            {
                int bx = blockX + dx;
                int by = blockY + dy;
                if (bx < 0 || by < 0 || bx >= landscape.Width || by >= landscape.Height)
                    continue;

                ushort clientCrc = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(15 + ((dx + 2) * 5 + dy + 2) * 2));
                var blockNumber = landscape.BlockIndex((ushort)bx, (ushort)by);
                if (ComputeBlockCrc(blockNumber) != clientCrc)
                {
                    _dirtyLandBlocks.Add(blockNumber);
                    _dirtyStaticBlocks.Add(blockNumber);
                }
            }
        }
    }

    private ushort ComputeBlockCrc(uint blockNumber)
    {
        var landscape = _server.Landscape;
        var blockX = (ushort)(blockNumber / landscape.Height);
        var blockY = (ushort)(blockNumber % landscape.Height);

        int sum1 = 0, sum2 = 0;

        void Add(ReadOnlySpan<byte> data)
        {
            foreach (var value in data)
            {
                sum1 = (sum1 + value) % 255;
                sum2 = (sum2 + sum1) % 255;
            }
        }

        Add(landscape.ReadRawLandBlock(blockX, blockY));
        Add(landscape.ReadRawStaticsBlock(blockX, blockY));
        return (ushort)((sum2 << 8) | sum1);
    }

    private void OnLandChanged(LandTile tile, ushort newId, sbyte newZ) => MarkLandDirty(tile.X, tile.Y);
    private void OnLandElevated(LandTile tile, sbyte newZ) => MarkLandDirty(tile.X, tile.Y);
    private void OnStaticChanged(StaticTile tile) => MarkStaticDirty(tile.X, tile.Y);
    private void OnStaticReplaced(StaticTile tile, ushort newId) => MarkStaticDirty(tile.X, tile.Y);
    private void OnStaticElevated(StaticTile tile, sbyte newZ) => MarkStaticDirty(tile.X, tile.Y);
    private void OnStaticHued(StaticTile tile, ushort newHue) => MarkStaticDirty(tile.X, tile.Y);

    private void OnStaticMoved(StaticTile tile, ushort newX, ushort newY)
    {
        MarkStaticDirty(tile.X, tile.Y);
        MarkStaticDirty(newX, newY);
    }

    private void OnBlockUpdated(Block block)
    {
        if (HasInWorldClients())
        {
            var blockNumber = _server.Landscape.BlockIndex(block.LandBlock.X, block.LandBlock.Y);
            _dirtyLandBlocks.Add(blockNumber);
            _dirtyStaticBlocks.Add(blockNumber);
        }
    }

    private bool HasInWorldClients()
    {
        foreach (var client in _clients)
        {
            if (client.InWorld && !client.Dead)
                return true;
        }
        return false;
    }

    private void MarkLandDirty(ushort x, ushort y)
    {
        if (HasInWorldClients())
        {
            _dirtyLandBlocks.Add(_server.Landscape.TileBlockIndex(x, y));
        }
    }

    private void MarkStaticDirty(ushort x, ushort y)
    {
        if (HasInWorldClients())
        {
            _dirtyStaticBlocks.Add(_server.Landscape.TileBlockIndex(x, y));
        }
    }

    private void FlushDirtyBlocks()
    {
        if (_dirtyLandBlocks.Count == 0 && _dirtyStaticBlocks.Count == 0)
            return;
        if (!HasInWorldClients())
        {
            _dirtyLandBlocks.Clear();
            _dirtyStaticBlocks.Clear();
            return;
        }

        foreach (var block in _dirtyLandBlocks)
        {
            Broadcast(BuildLandBlockPacket(block));
        }
        foreach (var block in _dirtyStaticBlocks)
        {
            Broadcast(BuildStaticsBlockPacket(block));
        }
        _dirtyLandBlocks.Clear();
        _dirtyStaticBlocks.Clear();
    }

    private void Broadcast(UOWriter packet)
    {
        foreach (var client in _clients)
        {
            if (client.InWorld && !client.Dead)
            {
                client.Send(packet.Span);
            }
        }
    }

    private UOWriter BuildLandBlockPacket(uint blockNumber)
    {
        var landscape = _server.Landscape;
        var blockX = (ushort)(blockNumber / landscape.Height);
        var blockY = (ushort)(blockNumber % landscape.Height);

        var w = new UOWriter(201);
        w.WriteByte(0x40);
        w.WriteUInt32BE(blockNumber);
        w.WriteBytes(landscape.ReadRawLandBlock(blockX, blockY));
        w.WriteZero(3);
        w.WriteByte(ClientMapIndex);
        return w;
    }

    private UOWriter BuildStaticsBlockPacket(uint blockNumber)
    {
        var landscape = _server.Landscape;
        var blockX = (ushort)(blockNumber / landscape.Height);
        var blockY = (ushort)(blockNumber % landscape.Height);
        var statics = landscape.ReadRawStaticsBlock(blockX, blockY);

        var w = new UOWriter();
        w.WriteVariableHeader(0x3F);
        w.WriteUInt32BE(blockNumber);
        w.WriteUInt32BE((uint)(statics.Length / 7));
        w.WriteZero(2);
        w.WriteByte(0x00);
        w.WriteByte(ClientMapIndex);
        w.WriteBytes(statics);
        w.PatchLength();
        return w;
    }

    #endregion

    public void Dispose()
    {
        _running = false;
        _listener.Close();

        var landscape = _server.Landscape;
        landscape.LandTileReplaced -= OnLandChanged;
        landscape.LandTileElevated -= OnLandElevated;
        landscape.StaticTileAdded -= OnStaticChanged;
        landscape.StaticTileRemoved -= OnStaticChanged;
        landscape.StaticTileReplaced -= OnStaticReplaced;
        landscape.StaticTileMoved -= OnStaticMoved;
        landscape.StaticTileElevated -= OnStaticElevated;
        landscape.StaticTileHued -= OnStaticHued;
        landscape.BlockUpdated -= OnBlockUpdated;

        foreach (var client in _clients)
        {
            client.Dispose();
        }
        _clients.Clear();
    }
}
