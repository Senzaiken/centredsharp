using System.Buffers;
using CentrED.Network;

namespace CentrED.Server;

public class ConnectionHandling
{
    private static PacketHandler<CEDServer>?[] Handlers { get; }

    static ConnectionHandling()
    {
        Handlers = new PacketHandler<CEDServer>?[0x100];

        Handlers[0x03] = new PacketHandler<CEDServer>(0, OnLoginRequestPacket);
        Handlers[0x05] = new PacketHandler<CEDServer>(0, OnQuitPacket);
        Handlers[0x20] = new PacketHandler<CEDServer>(0, OnSelectFacetPacket);
        Handlers[0x21] = new PacketHandler<CEDServer>(0, OnRequestFacetListPacket);
    }

    private static void OnSelectFacetPacket(SpanReader reader, NetState<CEDServer> ns)
    {
        var index = reader.ReadByte();
        if (!ns.Parent.BindFacet(ns, index))
            ns.LogDebug($"Client requested invalid facet index {index}");
    }

    private static void OnRequestFacetListPacket(SpanReader reader, NetState<CEDServer> ns)
    {
        ns.Send(new FacetListPacket(ns.Parent));
    }

    public static void OnConnectionHandlerPacket(SpanReader reader, NetState<CEDServer> ns)
    {
        ns.LogDebug("Server OnConnectionHandlerPacket");
        var id = reader.ReadByte();
        var packetHandler = Handlers[id];
        packetHandler?.OnReceive(reader, ns);
    }

    private static void OnLoginRequestPacket(SpanReader reader, NetState<CEDServer> ns)
    {
        ns.LogDebug("Server OnLoginRequestPacket");
        var username = reader.ReadString();
        var password = reader.ReadString();
        if (!ns.Parent.IsFacetSelected(ns))
            ns.Parent.BindFacet(ns, 0);
        var account = ns.Parent.GetAccount(username);
        if (account == null)
        {
            ns.LogDebug($"Invalid account specified: {username}");
            ns.Send(new LoginResponsePacket(LoginState.InvalidUser));
            ns.Disconnect();
        }
        else if (account.AccessLevel <= AccessLevel.None)
        {
            ns.LogDebug("Access Denied");
            ns.Send(new LoginResponsePacket(LoginState.NoAccess));
            ns.Disconnect();
        }
        else if (!account.CheckPassword(password))
        {
            ns.LogDebug("Invalid password");
            ns.Send(new LoginResponsePacket(LoginState.InvalidPassword));
            ns.Disconnect();
        }
        else if (ns.Parent.Clients.Any(client =>
                     client.Username == account.Name && ns.Parent.GetLandscape(client) == ns.Parent.GetLandscape(ns)))
        {
            ns.Send(new LoginResponsePacket(LoginState.AlreadyLoggedIn));
            ns.Disconnect();
        }
        else
        {
            ns.LogInfo($"Login {username} (facet {ns.Parent.GetFacetIndex(ns)})");
            ns.Username = account.Name;
            ns.Send(new LoginResponsePacket(LoginState.Ok, ns));
            ns.Send(new FacetListPacket(ns.Parent));
            ns.SendCompressed(new ClientListPacket(ns));
            ns.Parent.Broadcast(new ClientConnectedPacket(ns));
            ns.Send(new SetClientPosPacket(ns));
        }
    }

    private static void OnQuitPacket(SpanReader reader, NetState<CEDServer> ns)
    {
        ns.LogDebug("Server OnQuitPacket");
        ns.Send(new QuitAckPacket());
        ns.Disconnect();
    }
    
    public static void OnNoOpPacket(SpanReader buffer, NetState<CEDServer> ns)
    {
        ns.LogDebug("Server OnNoOpPacket");
    }
}