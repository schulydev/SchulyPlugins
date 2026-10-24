# Development environment

## Prerequisites

- **.NET 10 SDK** - every plugin csproj targets `net10.0` (`<TargetFramework>net10.0</TargetFramework>`).
- **EF Core CLI tools** (`dotnet-ef`) - for plugins with a `DbContext` (Schulware, OdaOrg).
  Install/update with `dotnet tool install --global dotnet-ef` (or `dotnet tool update`).
  See [migrations.md](../migrations.md).
- **[Kiota](https://learn.microsoft.com/openapi/kiota/install)** - only needed to regenerate
  the Schulware API client. See [setup/kiota-client.md](kiota-client.md).
- A running **[SchulyBackend](https://github.com/schulydev/SchulyBackend)** with PostgreSQL to
  load and test a plugin end-to-end.

## The abstractions dependency

`Schuly.Plugin.Abstractions` is a NuGet **`PackageReference`**, not a project reference:

```xml
<PackageReference Include="Schuly.Plugin.Abstractions" Version="0.4.*" />
```

It supplies the whole contract: `ISchulyPlugin`, `IPluginBackgroundTask`, `IPluginUserContext`,
`IPluginLogin`, `ISchoolData` (with its records), `IPluginVault`, `IPluginFileStore` and
`PluginServiceContext`. Plugins reference nothing from the backend; the host registers the
service implementations in each plugin's container at runtime.

The backend refuses to load a plugin built against an incompatible contract version (before
1.0: a different MAJOR.MINOR), and the publish workflow records that version as `contract` in
`index.json` so the backend never auto-updates to a build it can't load.

## How a plugin is structured

A plugin is a class library exposing one `ISchulyPlugin` implementation (the composition root,
slim like an ASP.NET `Program.cs`). The richer plugins keep HTTP routes in `Controllers/`
(discovered as an MVC ApplicationPart) rather than mapping them in `ConfigureEndpoints`, and
split sync logic across small scoped services driven by a single `IPluginBackgroundTask`.

See [adding-a-plugin.md](../adding-a-plugin.md) for the full lifecycle.

## Building a plugin

```sh
# Restore + build a single plugin
dotnet build src/Schuly.Plugin.Schulware/Schuly.Plugin.Schulware.csproj -c Release

# Produce the loadable output (DLL + non-host dependencies) - same command CI uses
dotnet publish src/Schuly.Plugin.Schulware/Schuly.Plugin.Schulware.csproj -c Release -o ./out
```

Each plugin also has a `.slnx` solution file for opening it standalone in an IDE.

## Running a plugin against a live backend

The backend's plugin host loads plugin DLLs from its `plugins/` directory (`/app/plugins/` in
the container) and reads each plugin's YAML config from its plugins-config directory.

1. Bring up SchulyBackend + Postgres (see the backend's README).
2. `dotnet publish` the plugin (above) and copy the plugin DLL plus its third-party
   dependency DLLs into the backend's `plugins/` folder. Host-shared assemblies
   (ASP.NET Core, EF Core, Npgsql, the abstractions, Schuly host assemblies) are already
   provided by the backend - only true third-party deps (e.g. Kiota, AngleSharp) need to
   ship alongside the plugin.
3. Drop the plugin's runtime config as `Schuly.Plugin.<Name>.yml` into the backend's
   plugins-config directory. For Schulware this **must** contain at least
   `SchulwareApi.BaseUrl` - `ConfigureServices` throws and refuses to load otherwise
   (see `src/Schuly.Plugin.Schulware/config.yml` for the schema).
4. Restart the backend. On startup the host calls `ConfigureServices` → `ConfigureEndpoints`
   → `MigrateAsync` (which runs `db.Database.MigrateAsync()` to create/upgrade the plugin's
   dedicated Postgres database), then schedules any `IPluginBackgroundTask` on its `Schedule`.

For real distribution (downloading prebuilt DLLs via `curl`) see
[setup/distribution.md](distribution.md).
