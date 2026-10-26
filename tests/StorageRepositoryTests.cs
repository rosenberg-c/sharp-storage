using System.Text.Json;
using Xunit;

namespace SharpStorage.Tests {
  public sealed class StorageRepositoryTests : IDisposable {
    private readonly string _dir;
    private bool _disposed;

    public StorageRepositoryTests() {
      _dir = Path.Combine(
        Path.GetTempPath(), "prodlist-tests",
        Guid.NewGuid().ToString("N")
      );
      _ = Directory.CreateDirectory(_dir);
    }

    private string NewFile(string name) {
      return Path.Combine(_dir, name);
    }

    public void Dispose() {
      if (_disposed) {
        return;
      }

      _disposed = true;
      GC.SuppressFinalize(this);
      try {
        Directory.Delete(_dir, recursive: true);
      }
      catch (IOException) {
        // Best effort cleanup of temp files.
      }
    }

    [Fact]
    public async Task AddAsync_PersistsEntry_ToFile() {
      string path = NewFile("add.json");
      StorageRepository<Entry> repo = new(path, static e => e.Id);

      await repo.AddAsync(new Entry(1, "hello"));

      Assert.True(File.Exists(path));
      List<Entry> all = await repo.GetAllAsync();
      Assert.Equal([new Entry(1, "hello")], all);
    }

    [Fact]
    public async Task AddThenLoad_NewInstance_ReadsBack_PersistedEntries() {
      string path = NewFile("persist.json");
      StorageRepository<Entry> writer = new(path, static e => e.Id);
      await writer.AddAsync(new Entry(1, "one"));
      await writer.AddAsync(new Entry(2, "two"));

      StorageRepository<Entry> reader = new(path, static e => e.Id);
      List<Entry> all = await reader.GetAllAsync();

      Assert.Equal([new Entry(1, "one"), new Entry(2, "two")], all);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsEmptyList_WhenFileMissing() {
      string path = NewFile("missing.json");
      StorageRepository<Entry> repo = new(path, static e => e.Id);

      Assert.Empty(await repo.GetAllAsync());
    }

    [Fact]
    public async Task GetAllAsync_RejectsUnexpectedSchemaVersion() {
      string path = NewFile("unsupported-version.json");
      string json = JsonSerializer.Serialize(new {
        schemaVersion = 2,
        data = Array.Empty<Entry>()
      });
      await File.WriteAllTextAsync(path, json);
      StorageRepository<Entry> repo = new(path, static e => e.Id);

      InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
        () => repo.GetAllAsync()
      );

      Assert.Contains("version 2", error.Message);
      Assert.Contains("version 1", error.Message);
    }

    [Fact]
    public async Task GetAsync_ReturnsEntry_MatchingSelectorId() {
      string path = NewFile("get.json");
      StorageRepository<Entry> repo = new(path, static e => e.Id);
      await repo.AddAsync(new Entry(1, "one"));
      await repo.AddAsync(new Entry(2, "two"));

      Assert.Equal(new Entry(2, "two"), await repo.GetAsync(2));
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenIdNotPresent() {
      string path = NewFile("get-missing.json");
      StorageRepository<Entry> repo = new(path, static e => e.Id);
      await repo.AddAsync(new Entry(1, "one"));

      Assert.Null(await repo.GetAsync(99));
    }

    [Fact]
    public async Task UpdateAsync_ReplacesEntry_MatchedBySelectorId() {
      string path = NewFile("update.json");
      StorageRepository<Entry> repo = new(path, static e => e.Id);
      await repo.AddAsync(new Entry(1, "one"));
      await repo.AddAsync(new Entry(2, "two"));

      bool updated = await repo.UpdateAsync(new Entry(2, "TWO"));

      Assert.True(updated);
      List<Entry> all =
        await new StorageRepository<Entry>(path, static e => e.Id)
          .GetAllAsync();
      Assert.Equal([new Entry(1, "one"), new Entry(2, "TWO")], all);
    }

    [Fact]
    public async Task UpdateAsync_ReturnsFalse_WhenIdNotPresent() {
      string path = NewFile("update-missing.json");
      StorageRepository<Entry> repo = new(path, static e => e.Id);
      await repo.AddAsync(new Entry(1, "one"));

      Assert.False(await repo.UpdateAsync(new Entry(99, "ghost")));
    }

    [Fact]
    public async Task
      UpdateAsync_ThrowsWithoutWriting_WhenIdMatchesMultipleEntries() {
      string path = NewFile("update-duplicate-id.json");
      string original = SerializeDocument([
          new Entry(1, "one"),
          new Entry(1, "another")
        ]
      );
      await File.WriteAllTextAsync(path, original);
      StorageRepository<Entry> repo = new(path, static e => e.Id);

      InvalidOperationException error =
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
          repo.UpdateAsync(new Entry(1, "updated"))
        );

      Assert.Equal("ID 1 matches multiple entries.", error.Message);
      Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task DeleteAsync_RemovesEntries_MatchedBySelectorId() {
      string path = NewFile("delete.json");
      StorageRepository<Entry> repo = new(path, static e => e.Id);
      await repo.AddAsync(new Entry(1, "one"));
      await repo.AddAsync(new Entry(2, "two"));

      bool deleted = await repo.DeleteAsync(1);

      Assert.True(deleted);
      List<Entry> all =
        await new StorageRepository<Entry>(path, static e => e.Id)
          .GetAllAsync();
      Assert.Equal([new Entry(2, "two")], all);
    }

    [Fact]
    public async Task DeleteAsync_ReturnsFalse_WhenIdNotPresent() {
      string path = NewFile("delete-missing.json");
      StorageRepository<Entry> repo = new(path, static e => e.Id);
      await repo.AddAsync(new Entry(1, "one"));

      Assert.False(await repo.DeleteAsync(99));
    }

    [Fact]
    public async Task Repository_IsGeneric_AnyEntryTypeWithIdSelector() {
      string path = NewFile("orders.json");
      StorageRepository<Order> repo = new(path, static o => o.Number);
      await repo.AddAsync(new Order(7, "seven"));

      Assert.Equal(new Order(7, "seven"), await repo.GetAsync(7));
      Assert.True(await repo.UpdateAsync(new Order(7, "SEVEN")));
      StorageRepository<Order> fresh = new(path, static o => o.Number);
      Assert.Equal(new Order(7, "SEVEN"), await fresh.GetAsync(7));
      Assert.True(await repo.DeleteAsync(7));
      Assert.Empty(
        await new StorageRepository<Order>(path, static o => o.Number)
          .GetAllAsync()
      );
    }

    [Fact]
    public void Constructor_Throws_WhenFilenameNullOrEmpty() {
      Assert.Throws<ArgumentNullException>(() =>
        new StorageRepository<Entry>((string?)null!, static e => e.Id)
      );
      Assert.Throws<ArgumentException>(() =>
        new StorageRepository<Entry>("", static e => e.Id)
      );
      Assert.Throws<ArgumentException>(() =>
        new StorageRepository<Entry>("  ", static e => e.Id)
      );
      Assert.Throws<ArgumentOutOfRangeException>(() =>
        new StorageRepository<Entry>("file.json", static e => e.Id, 0)
      );
    }

    [Fact]
    public void Constructor_Throws_WhenGetIdNull() {
      string path = NewFile("ctor.json");
      Assert.Throws<ArgumentNullException>(() =>
        new StorageRepository<Entry>(path, null!)
      );
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenFactoryNull() {
      string path = NewFile("create-nullfactory.json");
      StorageRepository<Entry> repo = new(path, static e => e.Id);

      await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        await repo.CreateAsync(null!)
      );
      Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task
      AddAsync_Throws_DoesNotWriteFile_WhenIdAlreadyExists() {
      string path = NewFile("dup-add.json");
      StorageRepository<Entry> repo = new(path, static e => e.Id);
      await repo.AddAsync(new Entry(1, "one"));
      string before = await File.ReadAllTextAsync(path);

      await Assert.ThrowsAsync<InvalidOperationException>(() =>
        repo.AddAsync(new Entry(1, "again"))
      );

      Assert.Equal(before, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task CreateAsync_AssignsFirstId_ToNewFile() {
      string path = NewFile("create-new.json");
      StorageRepository<Entry> repo = new(path, static e => e.Id);

      Entry created =
        await repo.CreateAsync(id => new Entry(id, "hello"));

      Assert.Equal(1, created.Id);
      Assert.Equal([new Entry(1, "hello")], await repo.GetAllAsync());
    }

    [Fact]
    public async Task
      CreateAsync_AssignsMaxPositivePlusOne_WhenEntriesExist() {
      string path = NewFile("create-existing.json");
      StorageRepository<Entry> seed = new(path, static e => e.Id);
      await seed.AddAsync(new Entry(1, "one"));
      await seed.AddAsync(new Entry(3, "three"));

      StorageRepository<Entry> repo = new(path, static e => e.Id);
      Entry created = await repo.CreateAsync(id => new Entry(id, "next"));

      Assert.Equal(4, created.Id);
    }

    [Fact]
    public async Task CreateAsync_IgnoresNonPositiveIds_WhenAllocating() {
      string path = NewFile("create-nonpositive.json");
      await File.WriteAllTextAsync(path,
        SerializeDocument([new Entry(-5, "neg"), new Entry(0, "zero")])
      );

      StorageRepository<Entry> repo = new(path, static e => e.Id);
      Entry created = await repo.CreateAsync(id => new Entry(id, "next"));

      Assert.Equal(1, created.Id);
    }

    [Fact]
    public async Task
      CreateAsync_Throws_DoesNotWriteFile_WhenFactoryIgnoresAssignedId() {
      string path = NewFile("create-badid.json");
      StorageRepository<Entry> repo = new(path, static e => e.Id);

      await Assert.ThrowsAsync<InvalidOperationException>(() =>
        repo.CreateAsync(id => new Entry(99, "bad"))
      );

      Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task
      CreateAsync_Throws_DoesNotWriteFile_WhenFactoryReturnsNull() {
      string path = NewFile("create-null.json");
      StorageRepository<Entry> repo = new(path, static e => e.Id);

      await Assert.ThrowsAsync<InvalidOperationException>(() =>
        repo.CreateAsync(_ => null!)
      );

      Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task
      CreateAsync_ThrowsOverflow_DoesNotWriteFile_AtMaxIntValue() {
      string path = NewFile("create-max.json");
      string json = SerializeDocument([
          new Entry(int.MaxValue, "max")
        ]
      );

      await File.WriteAllTextAsync(path, json);

      StorageRepository<Entry> repo = new(path, static e => e.Id);
      await Assert.ThrowsAsync<OverflowException>(async () =>
        await repo.CreateAsync(_ => new Entry(0, "next"))
      );

      Assert.Equal(json, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task CreateAsync_WorksForGenericImmutableEntry() {
      string path = NewFile("orders-create.json");
      StorageRepository<Order> seed = new(path, static o => o.Number);
      await seed.AddAsync(new Order(7, "seven"));

      StorageRepository<Order> repo = new(path, static o => o.Number);
      Order created = await repo.CreateAsync(n => new Order(n, "next"));

      Assert.Equal(8, created.Number);
    }

    [Fact]
    public async Task
      CreateAsync_AllocatesNewId_WithoutRepairingDuplicates() {
      string path = NewFile("legacy-dup-create.json");
      string json = SerializeDocument([
          new Entry(1, "first"), new Entry(1, "second")
        ]
      );
      await File.WriteAllTextAsync(path, json);

      StorageRepository<Entry> repo = new(path, static e => e.Id);
      Entry created =
        await repo.CreateAsync(id => new Entry(id, "third"));

      Assert.Equal(2, created.Id);
      List<Entry> all = await repo.GetAllAsync();
      Assert.Equal(
        [
          new Entry(1, "first"), new Entry(1, "second"),
          new Entry(2, "third")
        ], all
      );
    }

    [Fact]
    public async Task CreateAsync_ReusesId_WhenHighestEntryDeleted() {
      string path = NewFile("reuse.json");
      StorageRepository<Entry> repo = new(path, static e => e.Id);

      Entry first = await repo.CreateAsync(id => new Entry(id, "one"));
      Entry second = await repo.CreateAsync(id => new Entry(id, "two"));
      Assert.True(await repo.DeleteAsync(second.Id));

      Entry third = await repo.CreateAsync(id => new Entry(id, "three"));

      Assert.Equal(1, first.Id);
      Assert.Equal(second.Id, third.Id);
    }

    [Fact]
    public async Task
      CreateAsync_TwoInstancesOneFile_AllocateDistinctIds() {
      string path = NewFile("concurrent.json");
      StorageRepository<Entry> first = new(path, static e => e.Id);
      StorageRepository<Entry> second = new(path, static e => e.Id);

      Entry[] created = await Task.WhenAll(
        first.CreateAsync(id => new Entry(id, "a")),
        second.CreateAsync(id => new Entry(id, "b"))
      );
      List<int> ids = created.Select(e => e.Id).OrderBy(i => i).ToList();

      Assert.Equal([1, 2], ids);
    }

    [Fact]
    public async Task TryCreateAsync_RejectsDuplicateName_WithoutWriting() {
      string path = NewFile("duplicate-name.json");
      StorageRepository<NamedEntry> repo = new(path, static e => e.Id);

      Assert.Equal(CreateStatus.Created, await repo.TryCreateAsync(
          id => new NamedEntry(id, "AB", 300),
          e => IsDuplicate(e, "AB", 300)
        )
      );
      Assert.Equal(CreateStatus.Duplicate, await repo.TryCreateAsync(
          id => new NamedEntry(id, "ab", 300),
          e => IsDuplicate(e, "ab", 300)
        )
      );
      Assert.Equal([new NamedEntry(1, "AB", 300)], await repo.GetAllAsync());
    }

    [Fact]
    public async Task TryCreateAsync_TwoInstances_OnlyOneCreatesSameName() {
      string path = NewFile("concurrent-duplicate-name.json");
      StorageRepository<NamedEntry> first = new(path, static e => e.Id);
      StorageRepository<NamedEntry> second = new(path, static e => e.Id);

      Task<CreateStatus> a = first.TryCreateAsync(
        id => new NamedEntry(id, "AB", 300),
        e => IsDuplicate(e, "AB", 300)
      );
      Task<CreateStatus> b = second.TryCreateAsync(
        id => new NamedEntry(id, "ab", 300),
        e => IsDuplicate(e, "ab", 300)
      );

      CreateStatus[] results = await Task.WhenAll(a, b);
      Assert.Equal(1, results.Count(s => s == CreateStatus.Created));
      Assert.Equal(1, results.Count(s => s == CreateStatus.Duplicate));
      Assert.Single(await first.GetAllAsync());
    }

    private record Entry(int Id, string Name);
    private record Order(int Number, string Label);
    private record NamedEntry(int Id, string Name, int Group);

    private static bool IsDuplicate(NamedEntry entry, string name, int group) {
      return entry.Group == group && string.Equals(entry.Name, name,
        StringComparison.OrdinalIgnoreCase
      );
    }

    private static string SerializeDocument(List<Entry> entries) {
      return JsonSerializer.Serialize(new {
        schemaVersion = 1,
        data = entries
      });
    }
  }
}
