using CentrED.Client;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using static CentrED.Application;

namespace CentrED.Map;

public class World
{
    public CentrEDClient Client { get; }
    public MapManager Map { get; }
    public RadarMap RadarMap { get; }
    public string Name = "World";
    public int FacetIndex = -1;

    public World(GraphicsDevice gd, GameWindow window, Keymap keymap, CentrEDClient client)
    {
        Client = client;
        Map = new MapManager(gd, window, keymap, client);
        RadarMap = new RadarMap(gd, client);
    }

    public void Close()
    {
        try
        {
            if (Client.Running)
                Client.Disconnect();
        }
        catch
        {
        }
        Map.Dispose();
    }
}

public class WorldManager
{
    private GraphicsDevice _gd = null!;
    private GameWindow _window = null!;
    private Keymap _keymap = null!;

    private readonly List<World> _worlds = new();
    public IReadOnlyList<World> Worlds => _worlds;
    public World? Active { get; private set; }
    public CentrEDClient? ActiveClient => Active?.Client;
    public World? Focused { get; private set; }
    public World? FocusRequest;

    public void Initialize(GraphicsDevice gd, GameWindow window, Keymap keymap)
    {
        _gd = gd;
        _window = window;
        _keymap = keymap;
    }

    public World CreateWorld(CentrEDClient client)
    {
        var world = new World(_gd, _window, _keymap, client);
        _worlds.Add(world);
        Active ??= world;
        return world;
    }

    public World? FindIdleWorld() => _worlds.Find(w => w.FacetIndex < 0 && !w.Client.Running);

    public void SetActive(World world)
    {
        if (_worlds.Contains(world))
            Active = world;
    }

    public void SetFocused(World world)
    {
        if (_worlds.Contains(world))
            Focused = world;
    }

    public void RequestFocus(World world)
    {
        SetActive(world);
        SetFocused(world);
        FocusRequest = world;
    }

    public void Remove(World world)
    {
        if (!_worlds.Remove(world))
            return;
        world.Close();
        if (Active == world)
            Active = _worlds.Count > 0 ? _worlds[0] : null;
        if (Focused == world)
            Focused = Active;
    }
}
