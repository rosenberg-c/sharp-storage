using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpStorage.src {
  internal sealed record StorageDocument<TEntry>(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("data")] List<TEntry>? Data
  );
}

namespace SharpStorage.src {
  public class StorageRepository<TEntry> {
    private readonly string _path;
    private readonly Func<TEntry, Guid> _getId;
    private readonly int _schemaVersion;

    public StorageRepository(
      string filename,
      Func<TEntry, Guid> getId,
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

    private async Task SaveAsync(List<TEntry> entries) {
      StorageDocument<TEntry> document = new(_schemaVersion, entries);
      string json = JsonSerializer.Serialize(document, options);
      string path = Path.GetFullPath(_path);
      string tempPath = Path.Combine(Path.GetDirectoryName(path)!,
        $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp"
      );

      try {
        await File.WriteAllTextAsync(tempPath, json);
        if (File.Exists(path)) {
          if (!OperatingSystem.IsWindows()) {
            File.SetUnixFileMode(tempPath, File.GetUnixFileMode(path));
          }

          File.Replace(tempPath, path, null);
        } else {
          File.Move(tempPath, path);
        }
      }
      finally {
        if (File.Exists(tempPath)) {
          File.Delete(tempPath);
        }
      }
    }

    public async Task AddAsync(TEntry entry) {
      SemaphoreSlim gate = StorageFileLocks.For(_path);
      await gate.WaitAsync();
      try {
        List<TEntry> entries = await LoadAsync();
        Guid id = _getId(entry);
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

    public async Task<TEntry> CreateAsync(Func<Guid, TEntry> factory) {
      ArgumentNullException.ThrowIfNull(factory);

      SemaphoreSlim gate = StorageFileLocks.For(_path);
      await gate.WaitAsync();
      try {
        List<TEntry> entries = await LoadAsync();
        Guid id;
        do {
          id = Guid.NewGuid();
        } while (entries.Any(existing => _getId(existing) == id));

        TEntry assigned = factory(id);
        if (assigned is null || _getId(assigned) != id) {
          throw new InvalidOperationException(
            $"The entry factory must return an entry with ID {id}."
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

    public async Task<TEntry?> GetAsync(Guid id) {
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

    public async Task<bool> UpdateAsync(Guid id, Func<TEntry, TEntry> update) {
      ArgumentNullException.ThrowIfNull(update);

      SemaphoreSlim gate = StorageFileLocks.For(_path);
      await gate.WaitAsync();
      try {
        List<TEntry> entries = await LoadAsync();
        int index = -1;
        for (int i = 0; i < entries.Count; i++) {
          if (_getId(entries[i]) != id) {
            continue;
          }

          if (index != -1) {
            throw new InvalidOperationException(
              $"ID {id} matches multiple entries."
            );
          }

          index = i;
        }

        if (index == -1) {
          return false;
        }

        TEntry updated = update(entries[index]);
        if (updated is null || _getId(updated) != id) {
          throw new InvalidOperationException(
            $"The update must return an entry with ID {id}."
          );
        }

        entries[index] = updated;
        await SaveAsync(entries);
        return true;
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
        Guid id = _getId(entry);
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

    public async Task<bool> DeleteAsync(Guid id) {
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
