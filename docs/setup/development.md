# Development setup

Use this page to install the tools, build a plugin, and run it on a local server.

## Prerequisites

- **.NET 10 SDK.** Every plugin csproj targets `net10.0`.
- **EF Core CLI tools** (`dotnet-ef`), for plugins with a `DbContext` (Schulware, OdaOrg). See
  [migrations](../migrations.md).

  ```sh
  dotnet tool install --global dotnet-ef
  dotnet tool update --global dotnet-ef
  ```

- **[Kiota](https://learn.microsoft.com/openapi/kiota/install)**, to regenerate the Schulware API
  client. See [Kiota client](kiota-client.md).
- A running **[SchulyServer](/SchulyServer/setup/development)** with PostgreSQL, to load and test a
  plugin end to end.

## The abstractions dependency

`Schuly.Plugin.Abstractions` is a NuGet `PackageReference`, not a project reference:

```xml
<PackageReference Include="Schuly.Plugin.Abstractions" Version="0.11.*" />
```

It supplies the whole contract, including `ISchulyPlugin`, `IPluginBackgroundTask`,
`IPluginUserContext`, `IPluginLogin`, `ISchoolData` and its records, `IPluginVault`, `IPluginFileStore`,
`ILinkedAccounts`, `ILinkProvider`, and `PluginServiceContext`. See the
[contract reference](/SchulyPluginAbstractions/contract).

Plugins reference nothing from the backend. The host registers the service implementations in each
plugin's container at runtime.

The backend refuses to load a plugin built against an incompatible contract version. Before 1.0, that
means a different MAJOR.MINOR. The publish workflow records the version as `contract` in `index.json`, so
the backend never updates to a build it can't load. See
[versioning](/SchulyPluginAbstractions/versioning).

## How a plugin is structured

A plugin is a class library with one `ISchulyPlugin` implementation, the composition root. The larger
plugins keep HTTP routes in `Controllers/`, discovered as an MVC ApplicationPart, instead of mapping
them in `ConfigureEndpoints`. They split sync logic across small scoped services driven by one
`IPluginBackgroundTask`.

See [Add a plugin](../adding-a-plugin.md) for the full lifecycle.

## Build a plugin

```sh
# Restore and build one plugin
dotnet build src/Schuly.Plugin.Schulware/Schuly.Plugin.Schulware.csproj -c Release

# Produce the loadable output (the same command CI uses)
dotnet publish src/Schuly.Plugin.Schulware/Schuly.Plugin.Schulware.csproj -c Release -o ./out
```

The Example and Schulware plugins also have a `.slnx` file, which opens the plugin standalone in an IDE.

## Run a plugin on a local server

The server loads a plugin only if the plugin is listed in `plugins.yml` and has a manifest in the plugins
folder. It removes installed plugins that aren't listed.

These paths are relative to the server's app folder, `<app>`. With `dotnet run`, that's the build output
folder of `Schuly.API`. In the container, it's `/app`.

| File | Default | Setting |
|---|---|---|
| Plugin list | `<app>/plugins.yml` | `Plugins:File` |
| Plugin DLLs and manifests | `<app>/plugins/` | `Plugins:Directory` |
| Plugin config | `<app>/plugins-config/` | `Plugins:ConfigDirectory` |

1. Start SchulyServer and Postgres. See the server's
   [development setup](/SchulyServer/setup/development).
2. Run `dotnet publish` as shown above.
3. Copy `out/Schuly.Plugin.<Name>.dll` and its third-party dependency DLLs, such as Kiota or AngleSharp,
   to the plugins folder. Leave out what the server provides: `Microsoft.AspNetCore.*`,
   `Microsoft.Extensions.*`, `Microsoft.EntityFrameworkCore.*`, `Npgsql.*`, `Mediator.*`, and
   `Schuly.Plugin.Abstractions.dll`.
4. In the plugins folder, create the manifest `Schuly.Plugin.<Name>.plugin.json`. List every file you
   copied in `files`:

   ```json
   {
     "name": "Schuly.Plugin.<Name>",
     "version": "1.0.0",
     "dll": "Schuly.Plugin.<Name>.dll",
     "files": ["Schuly.Plugin.<Name>.dll"]
   }
   ```

5. Add the plugin to `plugins.yml` with the same version as the manifest. If the version differs and the
   plugin is also in the registry, the server replaces your build with the registry's.

   ```yaml
   plugins:
     - name: Schuly.Plugin.<Name>
       version: 1.0.0
   ```

6. If the plugin needs config, save it as `Schuly.Plugin.<Name>.yml` (or `.yaml`) in the plugins-config
   folder.
   - Schulware requires `SchulwareApi.BaseUrl`. See
     [`config.yml`](https://github.com/schulydev/SchulyPlugins/blob/main/src/Schuly.Plugin.Schulware/config.yml)
     for the schema.
   - Microsoft requires `MicrosoftApi.BaseUrl`.

   Without its required value, the plugin's `ConfigureServices` throws and the plugin doesn't load.
7. Restart the server.

On startup, the host calls `ConfigureServices`, then `MigrateAsync`, which creates or upgrades the
plugin's Postgres database, then `ConfigureEndpoints`. It then schedules any `IPluginBackgroundTask` on
its `Schedule`.

To see how plugins get published, read [distribution](distribution.md).
