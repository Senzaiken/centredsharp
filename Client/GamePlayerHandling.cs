using System.Buffers;
using CentrED.Network;

namespace CentrED.Client;

public class GamePlayer
{
    public uint Serial;
    public string Name = "";
    public ushort X;
    public ushort Y;
    public sbyte Z;
    public bool Mounted;
}

public static class GamePlayerHandling
{
    public static void OnGamePlayerPacket(SpanReader reader, NetState<CentrEDClient> ns)
    {
        ns.LogDebug("Client OnGamePlayerPacket");
        var op = reader.ReadByte();
        var serial = reader.ReadUInt32();
        if (op == 1)
        {
            ns.Parent.GamePlayers[serial] = new GamePlayer
            {
                Serial = serial,
                Name = reader.ReadString(),
                X = reader.ReadUInt16(),
                Y = reader.ReadUInt16(),
                Z = reader.ReadSByte(),
                Mounted = reader.ReadByte() != 0,
            };
        }
        else
        {
            ns.Parent.GamePlayers.Remove(serial);
        }
        ns.Parent.OnGamePlayersChanged();
    }
}
