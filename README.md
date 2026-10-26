# SharpStorage

A small .NET 10 JSON file repository for entries with integer IDs. The public API lives in the `SharpStorage` namespace. Data files contain `schemaVersion` and `data`; the repository rejects unsupported schema versions.

```csharp
using SharpStorage;

public record Note(int Id, string Text);

var storage = new StorageRepository<Note>("notes.json", note => note.Id);
Note note = await storage.CreateAsync(id => new Note(id, "Hello"));
Note? saved = await storage.GetAsync(note.Id);
```

Run `make build` and `make test` locally. This repository is consumed from Git with a project reference; it is not published to NuGet.

To use it in another Git repository after pushing SharpStorage to GitHub:

```sh
git submodule add git@github.com:rosenberg-c/sharp-storage.git dependencies/sharp-storage
```

Add `<ProjectReference Include="dependencies/sharp-storage/SharpStorage.csproj" />` to the consuming project, and exclude `dependencies/**` from its default compile glob. After cloning the consumer, run `git submodule update --init --recursive` (or its Makefile `deps` target) before building. Update the dependency by advancing and committing the submodule pointer in the consumer.
