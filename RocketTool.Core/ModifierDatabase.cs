using System.Reflection;

namespace RocketTool.Core;

public sealed class ModifierDatabase
{
    private readonly string _dbDirectory;
    private readonly Assembly? _resourceAssembly;
    private readonly Dictionary<string, Dictionary<int, string>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ModifierDatabase(string dbDirectory, Assembly? resourceAssembly = null)
    {
        _dbDirectory = dbDirectory;
        _resourceAssembly = resourceAssembly;
    }

    public IReadOnlyDictionary<int, string> Table(string name)
    {
        if (_cache.TryGetValue(name, out var cached)) return cached;
        var path = Path.Combine(_dbDirectory, name + ".tsv");
        var table = new Dictionary<int, string>();
        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path))
                AddLine(table, line);
        }
        else if (OpenResource(name) is { } stream)
        {
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
                AddLine(table, line);
        }
        _cache[name] = table;
        return table;
    }

    public string NameOf(string table, int id)
        => Table(table).TryGetValue(id, out var name) ? name : $"#{id}";

    private Stream? OpenResource(string name)
    {
        if (_resourceAssembly is null) return null;
        var expected = $"modifier_db.{name}.tsv";
        var resourceName = _resourceAssembly.GetManifestResourceNames()
                               .FirstOrDefault(r => string.Equals(r, expected, StringComparison.OrdinalIgnoreCase))
                           ?? _resourceAssembly.GetManifestResourceNames()
                               .FirstOrDefault(r => r.EndsWith("." + expected, StringComparison.OrdinalIgnoreCase));
        return resourceName is null ? null : _resourceAssembly.GetManifestResourceStream(resourceName);
    }

    private static void AddLine(Dictionary<int, string> table, string line)
    {
        var tab = line.IndexOf('\t');
        if (tab <= 0) return;
        if (int.TryParse(line[..tab], out var id)) table[id] = line[(tab + 1)..];
    }
}
