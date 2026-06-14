namespace RocketTool.Core;

public sealed record MapInfo(
    int Group,
    int Map,
    string Name,
    int Width,
    int Height,
    int WarpCount,
    int Region,
    int MapType,
    string HeaderOffset,
    string LayoutOffset,
    string EventsOffset);

public sealed record MapWarpInfo(
    int Group,
    int Map,
    int Index,
    string Label,
    int X,
    int Y,
    int Elevation,
    int DestGroup,
    int DestMap,
    int DestWarp);

public sealed class MapDatabase
{
    public IReadOnlyList<MapInfo> Maps { get; }
    public IReadOnlyList<MapWarpInfo> Warps { get; }

    public MapDatabase(ModifierDatabase db)
    {
        Maps = LoadMaps(db).ToArray();
        Warps = LoadWarps(db).ToArray();
    }

    public IEnumerable<IGrouping<int, MapInfo>> Groups()
        => Maps.GroupBy(m => m.Group).OrderBy(g => g.Key);

    public IReadOnlyList<MapInfo> MapsInGroup(int group)
        => Maps.Where(m => m.Group == group).OrderBy(m => m.Map).ToArray();

    public IReadOnlyList<MapWarpInfo> WarpsFor(int group, int map)
        => Warps.Where(w => w.Group == group && w.Map == map).OrderBy(w => w.Index).ToArray();

    private static IEnumerable<MapInfo> LoadMaps(ModifierDatabase db)
    {
        var sectionNames = db.Table("map_sections");
        foreach (var line in db.Lines("maps").Skip(1))
        {
            var p = line.Split('\t');
            if (p.Length < 11) continue;
            if (!int.TryParse(p[0], out var group) || !int.TryParse(p[1], out var map)) continue;
            var region = ParseInt(p[6]);
            var name = sectionNames.TryGetValue(region, out var sectionName) && !string.IsNullOrWhiteSpace(sectionName)
                ? sectionName
                : p[2];
            yield return new MapInfo(
                group,
                map,
                name,
                ParseInt(p[3]),
                ParseInt(p[4]),
                ParseInt(p[5]),
                region,
                ParseInt(p[7]),
                p[8],
                p[9],
                p[10]);
        }
    }

    private static IEnumerable<MapWarpInfo> LoadWarps(ModifierDatabase db)
    {
        foreach (var line in db.Lines("map_warps").Skip(1))
        {
            var p = line.Split('\t');
            if (p.Length < 10) continue;
            if (!int.TryParse(p[0], out var group) || !int.TryParse(p[1], out var map)) continue;
            yield return new MapWarpInfo(
                group,
                map,
                ParseInt(p[2]),
                p[3],
                ParseInt(p[4]),
                ParseInt(p[5]),
                ParseInt(p[6]),
                ParseInt(p[7]),
                ParseInt(p[8]),
                ParseInt(p[9]));
        }
    }

    private static int ParseInt(string text)
        => int.TryParse(text, out var value) ? value : 0;
}
