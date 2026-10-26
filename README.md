# SharpStorage

A small .NET 10 JSON file repository for entries with GUID IDs. The public API lives in the `SharpStorage.src` namespace. `CreateAsync` generates a new GUID independently of entry content, so identical titles remain separate entries. Storage rejects ID collisions but does not apply application-specific duplicate rules. Data files contain `schemaVersion` and `data`; the repository rejects unsupported schema versions.

```csharp
using SharpStorage.src;

public record Note(Guid Id, string Text);

var storage = new StorageRepository<Note>("notes.json", note => note.Id);
Note note = await storage.CreateAsync(id => new Note(id, "Hello"));
Note? saved = await storage.GetAsync(note.Id);
await storage.UpdateAsync(note.Id, current => current with { Text = "Updated" });
```

The callback runs synchronously while storage holds its in-process file lock. Do not call repository methods from inside it. Writes use a temporary file and replace the original after serialization succeeds; locks do not coordinate separate processes.

Run `make build` and `make test` locally. This repository is consumed from Git with a project reference; it is not published to NuGet.

To use it in another Git repository after pushing SharpStorage to GitHub:

```sh
git submodule add git@github.com:rosenberg-c/sharp-storage.git dependencies/sharp-storage
```

Add `<ProjectReference Include="dependencies/sharp-storage/SharpStorage.csproj" />` to the consuming project, and exclude `dependencies/**` from its default compile glob. After cloning the consumer, run `git submodule update --init --recursive` (or its Makefile `deps` target) before building. Update the dependency by advancing and committing the submodule pointer in the consumer.
