using System.Net;
using CentrED.Server;
using ServerConfigRoot = CentrED.Server.Config.ConfigRoot;
using ServerMap = CentrED.Server.Config.Map;
using Account = CentrED.Server.Config.Account;
using AccessLevel = CentrED.AccessLevel;

namespace CentrED.Map;

public class FacetServerHost
{
    private CEDServer? _server;
    private Task? _task;

    public bool Running => _server is { Running: true };
    public bool IsRunning(int index) => Running;

    public IReadOnlyList<int> HostedFacets =>
        _server?.Landscapes.Select(l => l.FacetIndex).ToList() ?? (IReadOnlyList<int>)Array.Empty<int>();

    public string? EnsureStarted(IReadOnlyList<Facet> facets, int port, string assetFallbackFolder)
    {
        if (Running)
            return null;
        if (facets.Count == 0)
            return "No facets to serve.";
        foreach (var facet in facets)
        {
            if (!facet.DimensionsKnown)
                return $"{facet.Name}: map dimensions unknown - set them in CentrED > Options > Facets first.";
            if (!facet.HasStatics)
                return $"{facet.Name}: matching staidx/statics files were not found.";
        }

        Stop();
        try
        {
            var config = BuildConfig(facets, port, assetFallbackFolder);
            _server = new CEDServer(config);
            _task = new Task(_server.Run);
            _task.Start();
            return null;
        }
        catch (Exception e)
        {
            _server = null;
            _task = null;
            return e.Message;
        }
    }

    public void Stop()
    {
        if (_server == null)
            return;
        try
        {
            _server.Quit = true;
            _task?.Wait(2000);
        }
        catch
        {
        }
        try { _server.Dispose(); }
        catch { }
        _server = null;
        _task = null;
    }

    public void StopAll() => Stop();

    private static ServerConfigRoot BuildConfig(IReadOnlyList<Facet> facets, int port, string assetFallbackFolder)
    {
        var mapDir = Path.GetDirectoryName(facets[0].MapPath) ?? "";
        var config = new ServerConfigRoot
        {
            CentrEdPlus = false,
            Port = port,
            BindAddress = IPAddress.Loopback,
            Facets = facets.Select(f => new ServerMap
            {
                Name = f.Name,
                MapPath = f.MapPath,
                StaIdx = f.StaIdxPath,
                Statics = f.StaticsPath,
                Width = f.Width,
                Height = f.Height,
            }).ToList(),
            Tiledata = ResolveAsset("tiledata.mul", mapDir, assetFallbackFolder),
            Radarcol = ResolveAsset("radarcol.mul", mapDir, assetFallbackFolder),
            Hues = ResolveAsset("hues.mul", mapDir, assetFallbackFolder),
        };
        var settings = Config.Instance.Facets;
        config.Accounts.Add(new Account(settings.AdminUsername, settings.AdminPassword, AccessLevel.Administrator, []));
        return config;
    }

    private static string ResolveAsset(string fileName, string mapDir, string fallbackFolder)
    {
        var primary = Path.Combine(mapDir, fileName);
        if (File.Exists(primary))
            return primary;
        if (!string.IsNullOrEmpty(fallbackFolder))
        {
            var fallback = Path.Combine(fallbackFolder, fileName);
            if (File.Exists(fallback))
                return fallback;
        }
        return fileName;
    }
}
