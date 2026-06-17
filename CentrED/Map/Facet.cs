using System.Text.RegularExpressions;

namespace CentrED.Map;

public class Facet
{
    public int Index;
    public string Name = "";
    public string MapPath = "";
    public string StaIdxPath = "";
    public string StaticsPath = "";
    public ushort Width;
    public ushort Height;
    public int Port;
    public bool IsUop;
    public bool DimensionsKnown;

    public bool HasStatics => StaIdxPath.Length > 0 && StaticsPath.Length > 0;
    public override string ToString() => $"{Name} ({Width}x{Height} @ :{Port})";
}

public partial class FacetManager
{
    public List<Facet> Facets { get; } = new();
    public string ScannedFolder { get; private set; } = "";

    private const int BytesPerLandBlock = 196;

    private static readonly string[] FacetNames =
        { "Felucca", "Trammel", "Ilshenar", "Malas", "Tokuno", "TerMur" };

    private static readonly (ushort W, ushort H)[] DimensionCandidates =
    {
        (768, 512), (896, 512), (288, 200), (320, 256), (160, 512), (320, 512), (181, 181), (128, 128)
    };

    private static readonly (ushort W, ushort H)[] ConventionalDimensions =
    {
        (896, 512), (896, 512), (288, 200), (320, 256), (181, 181), (160, 512)
    };

    [GeneratedRegex(@"^map(\d+)(LegacyMUL)?\.(mul|uop)$", RegexOptions.IgnoreCase)]
    private static partial Regex MapFileRegex();

    public void Scan(string folder, FacetSettings settings)
    {
        Facets.Clear();
        ScannedFolder = folder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return;

        foreach (var path in Directory.EnumerateFiles(folder))
        {
            var match = MapFileRegex().Match(Path.GetFileName(path));
            if (!match.Success)
                continue;
            if (!int.TryParse(match.Groups[1].Value, out var index))
                continue;

            var isUop = match.Groups[3].Value.Equals("uop", StringComparison.OrdinalIgnoreCase);
            var facet = new Facet
            {
                Index = index,
                Name = index < FacetNames.Length ? FacetNames[index] : $"Map {index}",
                MapPath = path,
                IsUop = isUop,
                Port = settings.BasePort,
            };
            FindStatics(folder, index, facet);
            ResolveDimensions(facet, settings);
            Facets.Add(facet);
        }
        Facets.Sort((a, b) => a.Index.CompareTo(b.Index));
    }

    private static void FindStatics(string folder, int index, Facet facet)
    {
        foreach (var candidate in new[] { $"staidx{index}.mul", $"staidx{index}LegacyMUL.uop" })
        {
            var p = Path.Combine(folder, candidate);
            if (File.Exists(p)) { facet.StaIdxPath = p; break; }
        }
        foreach (var candidate in new[] { $"statics{index}.mul", $"statics{index}LegacyMUL.uop" })
        {
            var p = Path.Combine(folder, candidate);
            if (File.Exists(p)) { facet.StaticsPath = p; break; }
        }
    }

    private static void ResolveDimensions(Facet facet, FacetSettings settings)
    {
        var ovr = settings.Overrides.Find(o => o.Index == facet.Index);
        if (ovr != null && !string.IsNullOrWhiteSpace(ovr.Name))
            facet.Name = ovr.Name;
        if (ovr != null && ovr.Width > 0 && ovr.Height > 0)
        {
            facet.Width = (ushort)ovr.Width;
            facet.Height = (ushort)ovr.Height;
            facet.DimensionsKnown = true;
            return;
        }

        var (w, h, known) = GuessDimensions(facet);
        facet.Width = w;
        facet.Height = h;
        facet.DimensionsKnown = known;
    }

    private static (ushort W, ushort H, bool Known) GuessDimensions(Facet facet)
    {
        var conventional = facet.Index < ConventionalDimensions.Length
            ? ConventionalDimensions[facet.Index]
            : ((ushort)0, (ushort)0);

        if (facet.IsUop)
            return (conventional.Item1, conventional.Item2, conventional.Item1 > 0);

        long size;
        try { size = new FileInfo(facet.MapPath).Length; }
        catch { return (conventional.Item1, conventional.Item2, false); }

        if (size <= 0 || size % BytesPerLandBlock != 0)
            return (conventional.Item1, conventional.Item2, false);

        var totalBlocks = size / BytesPerLandBlock;
        var matches = DimensionCandidates.Where(c => (long)c.W * c.H == totalBlocks).ToList();
        if (matches.Count == 1)
            return (matches[0].W, matches[0].H, true);
        if (matches.Count > 1)
        {
            var pick = matches.FirstOrDefault(m => m.W == conventional.Item1 && m.H == conventional.Item2);
            if (pick.W > 0)
                return (pick.W, pick.H, true);
            return (matches[0].W, matches[0].H, false);
        }
        return (conventional.Item1, conventional.Item2, false);
    }
}
