using CentrED.IO;
using static CentrED.Application;
using Point = System.Drawing.Point;

namespace CentrED.Map;

public class FacetController
{
    public FacetManager Manager { get; } = new();
    private readonly FacetServerHost _host = new();
    private readonly Dictionary<int, World> _worldsByFacet = new();
    private readonly HashSet<World> _loadAttempted = new();
    private bool _syncRequested;

    private sealed record RemoteSession(string Host, int Port, string User, string Pass, List<Client.ServerFacet> Facets);
    private RemoteSession? _remoteSession;
    public bool InRemoteMode => _remoteSession != null;
    public IReadOnlyList<Client.ServerFacet> RemoteFacets =>
        _remoteSession?.Facets ?? (IReadOnlyList<Client.ServerFacet>)Array.Empty<Client.ServerFacet>();

    private volatile World? _busyWorld;
    public World? BusyWorld { get => _busyWorld; private set => _busyWorld = value; }

    private volatile string _status = "";
    public string Status { get => _status; private set => _status = value; }

    public IReadOnlyList<Facet> Facets => Manager.Facets;
    public bool IsHosted(int index) => _host.IsRunning(index);
    public bool IsBusy(World world) => BusyWorld == world;
    public bool Switching => BusyWorld != null;

    public void Refresh() => Manager.Scan(Config.Instance.Facets.MapsFolder, Config.Instance.Facets);

    public List<Facet> ValidFacets() => Facets.Where(f => f.DimensionsKnown && f.HasStatics).ToList();

    public bool IsOpen(int facetIndex) =>
        _worldsByFacet.TryGetValue(facetIndex, out var w) && CEDGame.Worlds.Worlds.Contains(w);

    public void RequestSync() => _syncRequested = true;

    public void ProcessSync()
    {
        if (_syncRequested)
        {
            _syncRequested = false;
            SyncWorlds();
        }
        TryAdoptRemoteFacets();
        TryLeaveRemoteFacets();
    }

    private void TryAdoptRemoteFacets()
    {
        if (_remoteSession != null || Manager.Facets.Count > 0)
            return;
        var world = CEDGame.Worlds.Active;
        if (world == null || !world.Client.Running || world.Client.ServerFacets.Count <= 1)
            return;
        AdoptRemoteFacets(world);
    }

    private void TryLeaveRemoteFacets()
    {
        if (_remoteSession == null || BusyWorld != null)
            return;
        var worlds = CEDGame.Worlds;
        if (worlds.Worlds.Any(w => w.Client.Running))
            return;
        var keep = worlds.Active ?? (worlds.Worlds.Count > 0 ? worlds.Worlds[0] : worlds.CreateWorld(new Client.CentrEDClient()));
        foreach (var w in worlds.Worlds.ToList())
            if (w != keep)
                CloseWorld(w);
        ResetToPlain(keep);
        _remoteSession = null;
        worlds.RequestFocus(keep);
    }

    private void AdoptRemoteFacets(World connected)
    {
        var worlds = CEDGame.Worlds;
        var client = connected.Client;
        var facets = client.ServerFacets.ToList();
        _remoteSession = new RemoteSession(client.Hostname, client.Port, client.Username ?? "", client.Password ?? "", facets);

        var primaryIndex = client.Facet;
        var primary = facets.Find(f => f.Index == primaryIndex);
        connected.FacetIndex = primaryIndex;
        connected.Name = string.IsNullOrEmpty(primary.Name) ? $"Facet {primaryIndex}" : primary.Name;
        _worldsByFacet[primaryIndex] = connected;
        _loadAttempted.Add(connected);

        foreach (var facet in facets)
        {
            if (facet.Index == primaryIndex || _worldsByFacet.ContainsKey(facet.Index))
                continue;
            var world = worlds.FindIdleWorld() ?? worlds.CreateWorld(new Client.CentrEDClient());
            world.FacetIndex = facet.Index;
            world.Name = string.IsNullOrEmpty(facet.Name) ? $"Facet {facet.Index}" : facet.Name;
            _worldsByFacet[facet.Index] = world;
        }
        worlds.RequestFocus(connected);
    }

    public void SyncWorlds()
    {
        _remoteSession = null;
        var worlds = CEDGame.Worlds;
        var facets = Facets.Where(f => f.DimensionsKnown && f.HasStatics).ToList();

        if (facets.Count == 0)
        {
            var keep = worlds.Active ?? (worlds.Worlds.Count > 0 ? worlds.Worlds[0] : worlds.CreateWorld(new Client.CentrEDClient()));
            foreach (var w in worlds.Worlds.ToList())
                if (w != keep)
                    CloseWorld(w);
            ResetToPlain(keep);
            worlds.RequestFocus(keep);
            return;
        }

        foreach (var facet in facets)
        {
            if (IsOpen(facet.Index))
                continue;
            var world = worlds.FindIdleWorld() ?? worlds.CreateWorld(new Client.CentrEDClient());
            world.Name = facet.Name;
            world.FacetIndex = facet.Index;
            _worldsByFacet[facet.Index] = world;
        }
        foreach (var w in worlds.Worlds.ToList())
        {
            var isCurrentFacet = w.FacetIndex >= 0 && facets.Exists(f => f.Index == w.FacetIndex);
            if (!isCurrentFacet)
                CloseWorld(w);
        }
        if (_worldsByFacet.TryGetValue(facets[0].Index, out var first))
            worlds.RequestFocus(first);
    }

    private void ResetToPlain(World world)
    {
        try
        {
            if (world.Client.Running)
                world.Client.Disconnect();
        }
        catch { }
        if (world.FacetIndex >= 0)
            _worldsByFacet.Remove(world.FacetIndex);
        world.FacetIndex = -1;
        world.Name = "World";
        _loadAttempted.Remove(world);
    }

    public void EnsureLoaded(World world)
    {
        if (BusyWorld != null || world.FacetIndex < 0 || world.Client.Running)
            return;
        if (!_loadAttempted.Add(world))
            return;
        if (_remoteSession != null)
        {
            var idx = _remoteSession.Facets.FindIndex(f => f.Index == world.FacetIndex);
            if (idx < 0)
                return;
            BusyWorld = world;
            Status = $"Loading {world.Name}...";
            var facet = _remoteSession.Facets[idx];
            new Task(() => ConnectRemoteWorker(facet, world)).Start();
            return;
        }
        var localFacet = Manager.Facets.Find(f => f.Index == world.FacetIndex);
        if (localFacet == null)
            return;
        BusyWorld = world;
        Status = $"Loading {world.Name}...";
        new Task(() => ConnectWorker(localFacet, world)).Start();
    }

    public void FocusFacet(int index)
    {
        if (_worldsByFacet.TryGetValue(index, out var world) && CEDGame.Worlds.Worlds.Contains(world))
        {
            _loadAttempted.Remove(world);
            CEDGame.Worlds.RequestFocus(world);
        }
    }

    public void OpenWorld(Facet facet)
    {
        if (!facet.DimensionsKnown || !facet.HasStatics)
        {
            Status = $"{facet.Name}: set its size first.";
            return;
        }
        if (_worldsByFacet.TryGetValue(facet.Index, out var existing) && CEDGame.Worlds.Worlds.Contains(existing))
        {
            _loadAttempted.Remove(existing);
            CEDGame.Worlds.RequestFocus(existing);
            return;
        }
        var world = CEDGame.Worlds.FindIdleWorld() ?? CEDGame.Worlds.CreateWorld(new Client.CentrEDClient());
        world.Name = facet.Name;
        world.FacetIndex = facet.Index;
        _worldsByFacet[facet.Index] = world;
        CEDGame.Worlds.RequestFocus(world);
    }

    private void ConnectWorker(Facet facet, World world)
    {
        try
        {
            var clientPath = ProfileManager.ActiveProfile.ClientPath;
            world.Map.Load(clientPath);

            var settings = Config.Instance.Facets;
            var validFacets = ValidFacets();
            var err = _host.EnsureStarted(validFacets, settings.BasePort, clientPath);
            if (err != null)
            {
                Status = err;
                return;
            }
            var serverIndex = validFacets.FindIndex(f => f.Index == facet.Index);
            if (serverIndex < 0 || !_host.HostedFacets.Contains(serverIndex))
            {
                Status = $"{facet.Name}: not served by the embedded server.";
                return;
            }

            world.Client.Connect("127.0.0.1", settings.BasePort, settings.AdminUsername, settings.AdminPassword, serverIndex);
            world.Map.TilePosition = new Point(facet.Width * 8 / 2, facet.Height * 8 / 2);
            Status = "";
        }
        catch (Exception e)
        {
            Status = $"Failed to open {facet.Name}: {e.Message}";
            Console.WriteLine(e);
        }
        finally
        {
            BusyWorld = null;
        }
    }

    private void ConnectRemoteWorker(Client.ServerFacet facet, World world)
    {
        try
        {
            var clientPath = ProfileManager.ActiveProfile.ClientPath;
            world.Map.Load(clientPath);
            var session = _remoteSession!;
            world.Client.Connect(session.Host, session.Port, session.User, session.Pass, facet.Index);
            world.Map.TilePosition = new Point(facet.Width * 8 / 2, facet.Height * 8 / 2);
            Status = "";
        }
        catch (Exception e)
        {
            Status = $"Failed to open {facet.Name}: {e.Message}";
            Console.WriteLine(e);
        }
        finally
        {
            BusyWorld = null;
        }
    }

    public void CloseWorld(World world)
    {
        if (world.FacetIndex >= 0)
            _worldsByFacet.Remove(world.FacetIndex);
        _loadAttempted.Remove(world);
        CEDGame.Worlds.Remove(world);
    }

    public void Shutdown() => _host.StopAll();
}
