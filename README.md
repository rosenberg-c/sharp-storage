# SharpStorage

.NET 10 JSON file storage for GUID-keyed entries (`SharpStorage.src`). Creates unique IDs, rejects ID collisions and unsupported schema versions.

```csharp
using SharpStorage.src;

public record Note(Guid Id, string Text);

var storage = new StorageRepository<Note>("notes.json", note => note.Id);
Note note = await storage.CreateAsync(id => new Note(id, "Hello"));
Note? saved = await storage.GetAsync(note.Id);
await storage.UpdateAsync(note.Id, current => current with { Text = "Updated" });
```

Callbacks run under an in-process file lock: do not call repository methods inside them. Writes replace the file only after serialization succeeds; locks do not coordinate across processes.

Build and test with `make build` and `make test`. To consume from Git, add it as a submodule and reference `dependencies/sharp-storage/SharpStorage.csproj`:

```sh
git submodule add git@github.com:rosenberg-c/sharp-storage.git dependencies/sharp-storage
```

Exclude `dependencies/**` from the consumer's compile glob. After cloning, run `git submodule update --init --recursive` before building.
