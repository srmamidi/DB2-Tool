using System.Text.Json;
using IbmTurnover.Core.Models;

namespace IbmTurnover.Core.Workflow;

public interface ITurnoverRepository
{
    IReadOnlyList<TurnoverPackage> List();
    TurnoverPackage? Get(int number);
    TurnoverPackage Create(Action<TurnoverPackage> init);
    void Save(TurnoverPackage package);
}

/// <summary>
/// One JSON file, written atomically (temp file + replace). Enough for a team tool; swap for a database
/// implementation of <see cref="ITurnoverRepository"/> when you integrate into a larger app.
/// </summary>
public sealed class JsonFileTurnoverRepository : ITurnoverRepository
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();
    private List<TurnoverPackage> _items;

    public JsonFileTurnoverRepository(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "turnover-packages.json");
        _items = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<TurnoverPackage>>(File.ReadAllText(_path), Json)
              ?? throw new TurnoverException($"{_path} is not a valid package store.")
            : new();
    }

    public IReadOnlyList<TurnoverPackage> List()
    {
        lock (_gate) return _items.OrderByDescending(p => p.Number).Select(Clone).ToList();
    }

    public TurnoverPackage? Get(int number)
    {
        lock (_gate) return _items.FirstOrDefault(p => p.Number == number) is { } p ? Clone(p) : null;
    }

    public TurnoverPackage Create(Action<TurnoverPackage> init)
    {
        lock (_gate)
        {
            var p = new TurnoverPackage { Number = _items.Count == 0 ? 1 : _items.Max(x => x.Number) + 1 };
            init(p);
            _items.Add(p);
            try { Flush(); }
            catch { _items.Remove(p); throw; }
            return Clone(p);
        }
    }

    public void Save(TurnoverPackage package)
    {
        lock (_gate)
        {
            var i = _items.FindIndex(p => p.Number == package.Number);
            if (i < 0) throw new TurnoverException($"Package {package.Number} does not exist.");
            // Optimistic concurrency: two people acting on one package must not silently overwrite each other.
            if (_items[i].Version != package.Version)
                throw new PackageConflictException($"{package.Code} was changed by someone else a moment ago. Reload the page and try again.");
            var old = _items[i];
            var saved = Clone(package);
            saved.Version++;
            _items[i] = saved;
            try { Flush(); }
            catch { _items[i] = old; throw; }   // memory must not run ahead of the file
            package.Version = saved.Version;
        }
    }

    private void Flush()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_items, Json));
        File.Move(tmp, _path, overwrite: true);
    }

    // Callers get copies, so a half-finished change in a request never leaks into the store.
    private static TurnoverPackage Clone(TurnoverPackage p) =>
        JsonSerializer.Deserialize<TurnoverPackage>(JsonSerializer.Serialize(p, Json), Json)!;
}


public sealed class PackageConflictException(string message) : TurnoverException(message);
