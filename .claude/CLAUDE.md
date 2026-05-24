# Notes for Claude (and humans) — SchulyPlugins

Official plugins for the Schuly backend. Each plugin lives in its own folder under `src/` and implements `ISchulyPlugin` from [Schuly.Plugin.Abstractions](https://github.com/schulydev/SchulyPluginAbstractions) (NuGet).

## Layout

```
src/
├── Schuly.Plugin.Example/         # reference / scaffolding plugin
│   ├── ExamplePlugin.cs
│   ├── OnSchoolUserCreatedHandler.cs
│   └── Schuly.Plugin.Example.csproj
└── Schuly.Plugin.Schulware/       # Schulnetz integration via SchulwareAPI
    ├── SchulwarePlugin.cs         # entry point (slim, like ASP.NET Program.cs)
    ├── Endpoints/                 # endpoint mapping extensions
    │   ├── StatusEndpoints.cs
    │   ├── AccountEndpoints.cs
    │   ├── OAuthEndpoints.cs
    │   └── SyncEndpoints.cs
    ├── Services/                  # background tasks
    │   └── SchulwareSyncTask.cs
    ├── Dtos/                      # one record per file
    ├── Data/                      # EF entities + DbContext + Migrations
    ├── Infrastructure/            # external client factories
    ├── Client/                    # Kiota-generated SchulwareAPI client
    └── Schuly.Plugin.Schulware.csproj
```

## Add a new plugin

1. Copy `src/Schuly.Plugin.Example/` to `src/Schuly.Plugin.<Name>/`.
2. Rename `ExamplePlugin.cs` → `<Name>Plugin.cs`. Class implements `ISchulyPlugin`.
3. Open an issue with the `new-plugin` label, then standard branch + PR + squash-merge.

The publish workflow (`build_push.yml`) auto-picks up any `src/Schuly.Plugin.*/*.csproj`.

## Plugin lifecycle

- `ConfigureServices` — register services, handlers, options
- `ConfigureEndpoints` — map endpoints (use extension methods in `Endpoints/`)
- `MigrateAsync` — run plugin-owned EF Core migrations via `db.Database.MigrateAsync()`
- `IPluginBackgroundTask` — recurring work (the backend's `PluginBackgroundTaskHost` invokes `ExecuteAsync` on `Interval`)

## EF Core migrations

Each plugin's DbContext gets a dedicated Postgres database (the backend's `PluginExtensions` mutates the connection string to `schuly_plugin_<name>`).

Add a migration:

```sh
dotnet ef migrations add <Name> --project src/Schuly.Plugin.Schulware
```

A `IDesignTimeDbContextFactory<T>` lives next to each DbContext so the `dotnet ef` tooling can construct it without going through the runtime DI pipeline.

**Don't use `EnsureCreatedAsync`** — it creates the DB on first run but does nothing on schema changes. Use `MigrateAsync()` so column/index additions actually land on existing databases.

## Kiota client (Schulware)

Always regenerate **directly from the live URL** — never commit the OpenAPI JSON locally:

```sh
cd src/Schuly.Plugin.Schulware
kiota generate \
  --openapi https://schlwr.pianonic.ch/openapi.json \
  --language CSharp \
  --output Client \
  --namespace-name Schuly.Plugin.Schulware.Client \
  --class-name SchulwareApiClient
```

`kiota update` (run from inside `Client/`) does the same once the lockfile is established.

## Release / distribution

Plugins ship as DLLs to the `repo` branch under `dll/<AssemblyName>-v<Version>.dll`, indexed in `index.min.json` (Aniyomi pattern). Operators download via `curl` into `/app/plugins/`.
