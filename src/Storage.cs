using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpStorage {
  public enum CreateStatus {
    Created,
    Duplicate
  }
}

namespace SharpStorage {
  internal sealed record StorageDocument<TEntry>(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("data")] List<TEntry>? Data
  );
}

namespace SharpStorage {
  public class StorageRepository<TEntry> {
    private readonly string _path;
    private readonly Func<TEntry, int> _getId;
    private readonly int _schemaVersion;

    public StorageRepository(
      string filename,
      Func<TEntry, int> getId,
      int schemaVersion = 1
    ) {
      ArgumentException.ThrowIfNullOrWhiteSpace(filename);
      ArgumentNullException.ThrowIfNull(getId);
      ArgumentOutOfRangeException.ThrowIfNegativeOrZero(schemaVersion);

      _path = filename;
      _getId = getId;
      _schemaVersion = schemaVersion;
    }

    private readonly JsonSerializerOptions options = new() {
      WriteIndented = true
    };

    private async Task<List<TEntry>> LoadAsync() {
      if (!File.Exists(_path)) {
        return [];
      }

      string json = await File.ReadAllTextAsync(_path);
      using JsonDocument parsed = JsonDocument.Parse(json);
      JsonElement root = parsed.RootElement;
      if (root.ValueKind != JsonValueKind.Object
        || !root.TryGetProperty("schemaVersion", out JsonElement versionElement)
        || !versionElement.TryGetInt32(out int version)) {
        throw new JsonException(
          "Storage document must contain a numeric schemaVersion."
        );
      }

      if (version != _schemaVersion) {
        throw new InvalidDataException(
          $"Storage schema version {version} is not supported. "
            + $"Expected version {_schemaVersion}."
        );
      }

      StorageDocument<TEntry>? document =
        root.Deserialize<StorageDocument<TEntry>>();
      if (document is null || document.Data is null) {
        throw new JsonException("Storage document must contain data.");
      }

      return document.Data;
    }

    private async Task SaveAsync(List<TEntry> entry) {
      StorageDocument<TEntry> document = new(_schemaVersion, entry);
      string json = JsonSerializer.Serialize(document, options);

      await File.WriteAllTextAsync(_path, json);
    }

    public async Task AddAsync(TEntry entry) {
      SemaphoreSlim gate = StorageFileLocks.For(_path);
      await gate.WaitAsync();
      try {
        List<TEntry> entries = await LoadAsync();
        int id = _getId(entry);
        if (entries.Any(existing => _getId(existing) == id)) {
          throw new InvalidOperationException(
            $"An entry with ID {id} already exists."
          );
        }

        entries.Add(entry);
        await SaveAsync(entries);
      }
      finally {
        _ = gate.Release();
      }
    }

    public async Task<TEntry> CreateAsync(Func<int, TEntry> factory) {
      ArgumentNullException.ThrowIfNull(factory);

      SemaphoreSlim gate = StorageFileLocks.For(_path);
      await gate.WaitAsync();
      try {
        List<TEntry> entries = await LoadAsync();
        int maxId = entries.Select(_getId).Where(static id => id > 0)
          .DefaultIfEmpty(0).Max();
        int nextId = checked(maxId + 1);

        TEntry assigned = factory(nextId);
        if (assigned is null || _getId(assigned) != nextId) {
          throw new InvalidOperationException(
            $"The entry factory must return an entry with ID {nextId}."
          );
        }

        entries.Add(assigned);
        await SaveAsync(entries);
        return assigned;
      }
      finally {
        _ = gate.Release();
      }
    }

    public async Task<CreateStatus> TryCreateAsync(
      Func<int, TEntry> factory,
      Func<TEntry, bool> isDuplicate
    ) {
      ArgumentNullException.ThrowIfNull(factory);
      ArgumentNullException.ThrowIfNull(isDuplicate);

      SemaphoreSlim gate = StorageFileLocks.For(_path);
      await gate.WaitAsync();

      try {
        List<TEntry> entries = await LoadAsync();

        if (entries.Any(isDuplicate)) {
          return CreateStatus.Duplicate;
        }

        int maxId = entries.Select(_getId).Where(static id => id > 0)
          .DefaultIfEmpty(0).Max();
        int nextId = checked(maxId + 1);
        TEntry created = factory(nextId);

        if (created is null || _getId(created) != nextId) {
          throw new InvalidOperationException(
            $"The entry factory must return an entry with ID {nextId}."
          );
        }

        entries.Add(created);
        await SaveAsync(entries);
        return CreateStatus.Created;
      }
      finally {
        _ = gate.Release();
      }
    }

    public async Task<List<TEntry>> GetAllAsync() {
      SemaphoreSlim gate = StorageFileLocks.For(_path);
      await gate.WaitAsync();
      try {
        return await LoadAsync();
      }
      finally {
        _ = gate.Release();
      }
    }

    public async Task<TEntry?> GetAsync(int id) {
      SemaphoreSlim gate = StorageFileLocks.For(_path);
      await gate.WaitAsync();
      try {
        List<TEntry> entries = await LoadAsync();
        var matches = entries.Where(e => _getId(e) == id).Take(2).ToList();

        if (matches.Count > 1) {
          throw new InvalidOperationException(
            $"ID {id} matches multiple entries."
          );
        }

        return matches.Count == 0 ? default : matches[0];
      }
      finally {
        _ = gate.Release();
      }
    }

    public async Task<bool> UpdateAsync(TEntry entry) {
      SemaphoreSlim gate = StorageFileLocks.For(_path);
      await gate.WaitAsync();
      try {
        List<TEntry> entries = await LoadAsync();
        int id = _getId(entry);
        int singleIndex = -1;
        for (int i = 0; i < entries.Count; i++) {
          if (_getId(entries[i]) != id) {
            continue;
          }

          if (singleIndex != -1) {
            throw new InvalidOperationException(
              $"ID {id} matches multiple entries."
            );
          }

          singleIndex = i;
        }

        if (singleIndex == -1) {
          return false;
        }

        entries[singleIndex] = entry;
        await SaveAsync(entries);
        return true;
      }
      finally {
        _ = gate.Release();
      }
    }

    public async Task<bool> DeleteAsync(int id) {
      SemaphoreSlim gate = StorageFileLocks.For(_path);
      await gate.WaitAsync();
      try {
        List<TEntry> entries = await LoadAsync();

        int removed = entries.RemoveAll(e => _getId(e) == id);
        if (removed == 0) {
          return false;
        }

        await SaveAsync(entries);
        return true;
      }
      finally {
        _ = gate.Release();
      }
    }
  }

  internal static class StorageFileLocks {
    private static readonly ConcurrentDictionary<string, SemaphoreSlim>
      _gates = new(StringComparer.OrdinalIgnoreCase);

    public static SemaphoreSlim For(string path) {
      return _gates.GetOrAdd(Path.GetFullPath(path),
        static _ => new SemaphoreSlim(1, 1)
      );
    }
  }
}
