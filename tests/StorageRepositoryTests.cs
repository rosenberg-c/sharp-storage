using System.Text.Json;
using Xunit;
using SharpStorage.src;

namespace SharpStorage.Tests {
  public sealed class StorageRepositoryTests : IDisposable {
    private static readonly Guid FirstId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid SecondId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private readonly string _dir;
    private bool _disposed;

    public StorageRepositoryTests() {
      _dir = Path.Combine(Path.GetTempPath(), "sharp-storage-tests",
        Guid.NewGuid().ToString("N")
      );
      _ = Directory.CreateDirectory(_dir);
    }

    private string NewFile(string name) => Path.Combine(_dir, name);

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

      await repo.AddAsync(new Entry(FirstId, "hello"));

      Assert.True(File.Exists(path));
      Assert.Equal([new Entry(FirstId, "hello")], await repo.GetAllAsync());
    }

    [Fact]
    public async Task AddThenLoad_NewInstance_ReadsBack_PersistedEntries() {
      string path = NewFile("persist.json");
      StorageRepository<Entry> writer = new(path, static e => e.Id);
      await writer.AddAsync(new Entry(FirstId, "one"));
      await writer.AddAsync(new Entry(SecondId, "two"));

      StorageRepository<Entry> reader = new(path, static e => e.Id);
      Assert.Equal([
        new Entry(FirstId, "one"), new Entry(SecondId, "two")
      ], await reader.GetAllAsync());
    }

    [Fact]
    public async Task GetAllAsync_ReturnsEmptyList_WhenFileMissing() {
      StorageRepository<Entry> repo = new(NewFile("missing.json"), static e => e.Id);
      Assert.Empty(await repo.GetAllAsync());
    }

    [Fact]
    public async Task GetAllAsync_RejectsUnexpectedSchemaVersion() {
      string path = NewFile("unsupported-version.json");
      await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new {
        schemaVersion = 2,
        data = Array.Empty<Entry>()
      }));
      StorageRepository<Entry> repo = new(path, static e => e.Id);

      InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
        () => repo.GetAllAsync()
      );
      Assert.Contains("version 2", error.Message);
      Assert.Contains("version 1", error.Message);
    }

    [Fact]
    public async Task GetAsync_ReturnsMatchingEntry_OrNull() {
      string path = NewFile("get.json");
      StorageRepository<Entry> repo = new(path, static e => e.Id);
      await repo.AddAsync(new Entry(FirstId, "one"));
      await repo.AddAsync(new Entry(SecondId, "two"));

      Assert.Equal(new Entry(SecondId, "two"), await repo.GetAsync(SecondId));
      Assert.Null(await repo.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetAsync_RejectsDuplicateIds_InExistingFile() {
      string path = NewFile("get-duplicates.json");
      await File.WriteAllTextAsync(path, SerializeDocument([
        new Entry(FirstId, "one"), new Entry(FirstId, "two")
      ]));
      StorageRepository<Entry> repo = new(path, static e => e.Id);

      await Assert.ThrowsAsync<InvalidOperationException>(() => repo.GetAsync(FirstId));
    }

    [Fact]
    public async Task UpdateAsync_ReplacesEntry_AndReturnsFalse_WhenMissing() {
      string path = NewFile("update.json");
      StorageRepository<Entry> repo = new(path, static e => e.Id);
      await repo.AddAsync(new Entry(FirstId, "one"));
      await repo.AddAsync(new Entry(SecondId, "two"));

      Assert.True(await repo.UpdateAsync(new Entry(SecondId, "TWO")));
      Assert.False(await repo.UpdateAsync(new Entry(Guid.NewGuid(), "ghost")));
      Assert.Equal([
        new Entry(FirstId, "one"), new Entry(SecondId, "TWO")
      ], await new StorageRepository<Entry>(path, static e => e.Id).GetAllAsync());
    }

    [Fact]
    public async Task UpdateAsync_RejectsDuplicateIds_WithoutWriting() {
      string path = NewFile("update-duplicate-id.json");
      string original = SerializeDocument([
        new Entry(FirstId, "one"), new Entry(FirstId, "another")
      ]);
      await File.WriteAllTextAsync(path, original);
      StorageRepository<Entry> repo = new(path, static e => e.Id);

      InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
        () => repo.UpdateAsync(new Entry(FirstId, "updated"))
      );
      Assert.Equal($"ID {FirstId} matches multiple entries.", error.Message);
      Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task DeleteAsync_RemovesEntry_AndReturnsFalse_WhenMissing() {
      string path = NewFile("delete.json");
      StorageRepository<Entry> repo = new(path, static e => e.Id);
      await repo.AddAsync(new Entry(FirstId, "one"));
      await repo.AddAsync(new Entry(SecondId, "two"));

      Assert.True(await repo.DeleteAsync(FirstId));
      Assert.False(await repo.DeleteAsync(FirstId));
      Assert.Equal([new Entry(SecondId, "two")], await repo.GetAllAsync());
    }

    [Fact]
    public async Task DeleteAsync_RemovesAllLegacyEntries_WithSameId() {
      string path = NewFile("delete-duplicates.json");
      await File.WriteAllTextAsync(path, SerializeDocument([
        new Entry(FirstId, "one"), new Entry(FirstId, "two")
      ]));
      StorageRepository<Entry> repo = new(path, static e => e.Id);

      Assert.True(await repo.DeleteAsync(FirstId));
      Assert.Empty(await repo.GetAllAsync());
    }

    [Fact]
    public async Task Repository_SupportsAnyEntryTypeWithGuidSelector() {
      string path = NewFile("orders.json");
      StorageRepository<Order> repo = new(path, static o => o.Key);
      await repo.AddAsync(new Order(FirstId, "one"));

      Assert.Equal(new Order(FirstId, "one"), await repo.GetAsync(FirstId));
      Assert.True(await repo.UpdateAsync(new Order(FirstId, "ONE")));
      Assert.Equal(new Order(FirstId, "ONE"),
        await new StorageRepository<Order>(path, static o => o.Key).GetAsync(FirstId)
      );
      Assert.True(await repo.DeleteAsync(FirstId));
    }

    [Fact]
    public void Constructor_ValidatesArguments() {
      Assert.Throws<ArgumentNullException>(() =>
        new StorageRepository<Entry>((string?)null!, static e => e.Id)
      );
      Assert.Throws<ArgumentException>(() =>
        new StorageRepository<Entry>("", static e => e.Id)
      );
      Assert.Throws<ArgumentException>(() =>
        new StorageRepository<Entry>("  ", static e => e.Id)
      );
      Assert.Throws<ArgumentNullException>(() =>
        new StorageRepository<Entry>("file.json", null!)
      );
      Assert.Throws<ArgumentOutOfRangeException>(() =>
        new StorageRepository<Entry>("file.json", static e => e.Id, 0)
      );
    }

    [Fact]
    public async Task AddAsync_RejectsIdCollision_WithoutWriting() {
      string path = NewFile("dup-add.json");
      StorageRepository<Entry> repo = new(path, static e => e.Id);
      await repo.AddAsync(new Entry(FirstId, "one"));
      string before = await File.ReadAllTextAsync(path);

      await Assert.ThrowsAsync<InvalidOperationException>(() =>
        repo.AddAsync(new Entry(FirstId, "again"))
      );
      Assert.Equal(before, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task CreateAsync_GeneratesDistinctIds_ForIdenticalTitles() {
      string path = NewFile("create-same-title.json");
      StorageRepository<Entry> repo = new(path, static e => e.Id);

      Entry first = await repo.CreateAsync(id => new Entry(id, "hello"));
      Entry second = await repo.CreateAsync(id => new Entry(id, "hello"));

      Assert.NotEqual(Guid.Empty, first.Id);
      Assert.NotEqual(first.Id, second.Id);
      Assert.Equal([first, second], await repo.GetAllAsync());
      Assert.Equal(second, await new StorageRepository<Entry>(path, static e => e.Id)
        .GetAsync(second.Id));
    }

    [Fact]
    public async Task CreateAsync_DoesNotReuseId_AfterDelete() {
      string path = NewFile("create-after-delete.json");
      StorageRepository<Entry> repo = new(path, static e => e.Id);
      Entry first = await repo.CreateAsync(id => new Entry(id, "one"));
      Assert.True(await repo.DeleteAsync(first.Id));

      Entry second = await repo.CreateAsync(id => new Entry(id, "two"));
      Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task CreateAsync_ValidatesFactory_WithoutWriting() {
      string path = NewFile("create-invalid.json");
      StorageRepository<Entry> repo = new(path, static e => e.Id);

      await Assert.ThrowsAsync<ArgumentNullException>(() => repo.CreateAsync(null!));
      await Assert.ThrowsAsync<InvalidOperationException>(() =>
        repo.CreateAsync(_ => new Entry(FirstId, "wrong ID"))
      );
      await Assert.ThrowsAsync<InvalidOperationException>(() =>
        repo.CreateAsync(_ => null!)
      );
      Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task CreateAsync_TwoInstancesOneFile_GenerateDistinctIds() {
      string path = NewFile("concurrent.json");
      StorageRepository<Entry> first = new(path, static e => e.Id);
      StorageRepository<Entry> second = new(path, static e => e.Id);

      Entry[] created = await Task.WhenAll(
        first.CreateAsync(id => new Entry(id, "hello")),
        second.CreateAsync(id => new Entry(id, "hello"))
      );

      Assert.NotEqual(created[0].Id, created[1].Id);
      Assert.Equal(2, (await first.GetAllAsync()).Count);
    }

    [Fact]
    public async Task UpdateAsync_ChangesNestedItems_AndPersistsTheirOrder() {
      string path = NewFile("workspace.json");
      StorageRepository<Workspace> repo = new(path, static w => w.Id);
      await repo.AddAsync(new Workspace(FirstId, []));

      Assert.True(await repo.UpdateAsync(FirstId, workspace => workspace with {
        Items = [new WorkItem(SecondId, "first"), new WorkItem(Guid.NewGuid(), "second")]
      }));

      Workspace? loaded = await new StorageRepository<Workspace>(path,
        static w => w.Id).GetAsync(FirstId);
      Assert.NotNull(loaded);
      Assert.Equal(["first", "second"], loaded.Items.Select(item => item.Name));
    }

    [Fact]
    public async Task UpdateAsync_ConcurrentInstances_DoNotLoseUpdates() {
      string path = NewFile("concurrent-update.json");
      StorageRepository<Workspace> first = new(path, static w => w.Id);
      StorageRepository<Workspace> second = new(path, static w => w.Id);
      await first.AddAsync(new Workspace(FirstId, []));

      bool[] results = await Task.WhenAll(
        first.UpdateAsync(FirstId, workspace => workspace with {
          Items = [.. workspace.Items, new WorkItem(Guid.NewGuid(), "one")]
        }),
        second.UpdateAsync(FirstId, workspace => workspace with {
          Items = [.. workspace.Items, new WorkItem(Guid.NewGuid(), "two")]
        })
      );

      Assert.All(results, Assert.True);
      Workspace? loaded = await first.GetAsync(FirstId);
      Assert.NotNull(loaded);
      Assert.Equal(["one", "two"], loaded.Items.Select(item => item.Name)
        .OrderBy(name => name));
    }

    [Fact]
    public async Task UpdateAsync_RejectsMissingAndInvalidUpdates_WithoutWriting() {
      string path = NewFile("invalid-update.json");
      StorageRepository<Entry> repo = new(path, static e => e.Id);
      await repo.AddAsync(new Entry(FirstId, "original"));
      string before = await File.ReadAllTextAsync(path);

      Assert.False(await repo.UpdateAsync(SecondId, _ =>
        throw new InvalidOperationException("Callback should not run")
      ));
      await Assert.ThrowsAsync<ArgumentNullException>(() =>
        repo.UpdateAsync(FirstId, null!)
      );
      await Assert.ThrowsAsync<InvalidOperationException>(() =>
        repo.UpdateAsync(FirstId, _ => new Entry(SecondId, "wrong ID"))
      );
      await Assert.ThrowsAsync<InvalidOperationException>(() =>
        repo.UpdateAsync(FirstId, _ => null!)
      );
      await Assert.ThrowsAsync<InvalidOperationException>(() =>
        repo.UpdateAsync(FirstId, _ => throw new InvalidOperationException("failed"))
      );

      Assert.Equal(before, await File.ReadAllTextAsync(path));
      Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task UpdateAsync_PreservesExistingFilePermissions() {
      if (OperatingSystem.IsWindows()) {
        return;
      }

      string path = NewFile("private.json");
      StorageRepository<Entry> repo = new(path, static e => e.Id);
      await repo.AddAsync(new Entry(FirstId, "original"));
      UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
      File.SetUnixFileMode(path, privateMode);

      Assert.True(await repo.UpdateAsync(FirstId,
        entry => entry with { Name = "updated" }));

      Assert.Equal(privateMode, File.GetUnixFileMode(path));
      Assert.Equal(new Entry(FirstId, "updated"), await repo.GetAsync(FirstId));
    }

    [Fact]
    public async Task UpdateAsync_RejectsLegacyDuplicateIds_WithoutWriting() {
      string path = NewFile("duplicate-update.json");
      string before = SerializeDocument([
        new Entry(FirstId, "one"), new Entry(FirstId, "two")
      ]);
      await File.WriteAllTextAsync(path, before);
      StorageRepository<Entry> repo = new(path, static e => e.Id);

      await Assert.ThrowsAsync<InvalidOperationException>(() =>
        repo.UpdateAsync(FirstId, entry => entry with { Name = "updated" })
      );
      Assert.Equal(before, await File.ReadAllTextAsync(path));
    }

    private record Entry(Guid Id, string Name);
    private record Order(Guid Key, string Label);
    private record Workspace(Guid Id, List<WorkItem> Items);
    private record WorkItem(Guid Id, string Name);

    private static string SerializeDocument(List<Entry> entries) {
      return JsonSerializer.Serialize(new {
        schemaVersion = 1,
        data = entries
      });
    }
  }
}
