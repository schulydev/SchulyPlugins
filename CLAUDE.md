# Notes for Claude (and humans) — SchulyPlugins

Official plugins for the Schuly backend. Each plugin lives in its own folder and implements `ISchulyPlugin` from [Schuly.Plugin.Abstractions](https://github.com/schulydev/SchulyPluginAbstractions) (NuGet).

## Layout

```
.
├── application.properties          # version source
├── Directory.Build.props           # propagates version to all plugin csprojs
├── Schuly.Plugin.Example/          # reference / scaffolding plugin
│   ├── ExamplePlugin.cs            # implements ISchulyPlugin
│   ├── OnSchoolUserCreatedHandler.cs
│   └── Schuly.Plugin.Example.csproj
└── Schuly.Plugin.Schulware/        # Schulnetz integration via SchulwareAPI
    ├── SchulwarePlugin.cs
    ├── SchulwareSyncTask.cs        # IPluginBackgroundTask
    ├── Client/                     # SchulwareAPI client
    ├── Data/                       # entities / DTOs
    ├── refresher/
    ├── config.json
    └── Schuly.Plugin.Schulware.csproj
```

## ⚠ Build is currently broken (cross-repo refs)

Each plugin csproj has:

```xml
<ProjectReference Include="..\..\src\Schuly.Plugin.Abstractions\..." />
<ProjectReference Include="..\..\src\Schuly.Application\..." />
```

Those paths point to the old monorepo. Tracked in [#5](https://github.com/schulydev/SchulyPlugins/issues/5). Resolution: swap to `<PackageReference Include="Schuly.Plugin.Abstractions" />` and either publish `Schuly.Application.Contracts` as NuGet or refactor plugins not to reference Application command types directly.

Until resolved: **don't try to `dotnet build` this repo standalone — it will fail.** You can edit plugin source for review/refactor purposes.

## Add a new plugin

1. Copy `Schuly.Plugin.Example/` to `Schuly.Plugin.<Name>/`.
2. Rename `ExamplePlugin.cs` → `<Name>Plugin.cs`. Class implements `ISchulyPlugin`:
   ```csharp
   public string Name => "<Name>";
   public string Version => "1.0.0";
   public void ConfigureServices(IServiceCollection services, PluginServiceContext ctx) { … }
   public void ConfigureEndpoints(IEndpointRouteBuilder endpoints) { … }
   public Task MigrateAsync(IServiceProvider sp, CancellationToken ct = default) => Task.CompletedTask;
   ```
3. Open an issue with the `new-plugin` label.
4. Follow the standard branch + PR + squash-merge flow.

## Plugin lifecycle (when the backend loads it)

- `ConfigureServices` — register your services, handlers, options
- `ConfigureEndpoints` — map any custom endpoints
- `MigrateAsync` — run plugin-owned EF Core migrations
- `IPluginBackgroundTask` — recurring work (the backend's `PluginBackgroundTaskHost` invokes `ExecuteAsync` on `Interval`)
- `IPluginEventHandler<TCommand>` — listen to backend commands and react

## Release / versioning

Single version for the whole repo, driven by `application.properties`. Cut a release and the sync workflow updates the file. Plugins don't (yet) publish anywhere — they ship inside the backend Docker image at deploy time (see [SchulyBackend#7](https://github.com/schulydev/SchulyBackend/issues/7)).
