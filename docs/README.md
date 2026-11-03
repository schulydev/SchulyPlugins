# SchulyPlugins

Use these pages to build, test, and ship plugins for the
[Schuly server](https://github.com/schulydev/SchulyServer).

| Page | Use it to |
|---|---|
| [Add a plugin](adding-a-plugin.md) | Go from a copy of the example plugin to a loaded plugin |
| [Development setup](setup/development.md) | Install the tools, build a plugin, and run it on a local server |
| [EF Core migrations](migrations.md) | Give a plugin its own database and evolve its schema |
| [Kiota client](setup/kiota-client.md) | Regenerate the Schulware API client |
| [Microsoft response cache](microsoft-response-cache.md) | See what the Microsoft plugin keeps in memory, and set its times |
| [Distribution](setup/distribution.md) | See how plugins get built and shipped to the `repo` branch |
| [Contributing](contributing.md) | Follow this repo's own rules |

## How plugins work

Each plugin lives in its own folder under `src/`, targets .NET 10, and implements `ISchulyPlugin` from
[`Schuly.Plugin.Abstractions`](https://github.com/schulydev/SchulyPluginAbstractions), a NuGet
`PackageReference`.

CI builds each plugin and publishes the DLLs to the `repo` branch (Aniyomi-style distribution). The
server downloads the plugins listed in its `plugins.yml` and loads them at startup. It calls
`ConfigureServices`, `MigrateAsync`, and `ConfigureEndpoints`, then schedules any recurring
`IPluginBackgroundTask`.

## Repository layout

| Path | Role |
|---|---|
| `src/Schuly.Plugin.Example/` | Reference plugin to copy. A minimal `ISchulyPlugin` with minimal-API endpoints, anonymous and authorized routes, and a per-plugin vault demo. |
| `src/Schuly.Plugin.Schulware/` | Schulnetz integration through [SchulwareAPI](https://github.com/PianoNic/SchulwareAPI). EF Core and Postgres, a Kiota-generated client, a background sync task, and MVC controllers. |
| `src/Schuly.Plugin.OdaOrg/` | OdaOrg (ICT-BBAG apprenticeship portal) integration. An HttpClient and AngleSharp scraper, EF Core and Postgres, and a background sync task. |
| `src/Schuly.Plugin.Microsoft/` | Microsoft 365 through the [SchulyMicrosoft](https://github.com/schulydev/SchulyMicrosoft) service. Links a Microsoft account, and reads and writes mail, calendar, OneDrive, Teams, and OneNote data. It has no database of its own. |
| `src/Shared/` | Helpers compiled into plugins that opt in: `ExternalIds`, `SwissTime`, `DataUri`, and `Provisioning/InstanceUrl`. |
| `tests/` | `Schuly.Plugin.Microsoft.Tests` and `Schuly.Plugins.Linking.Tests`. |
| `.github/workflows/build_push.yml` | Finds each plugin project, builds it, and publishes DLLs and the index to the `repo` branch. |
| `.github/workflows/sync-docs.yml` | Copies `docs/` to the docs site. |

## Inside a plugin folder

The Schulware and OdaOrg plugins use these folders. The Microsoft plugin uses `Controllers/`,
`Services/`, and `Infrastructure/`.

| Folder | Contents |
|---|---|
| `Controllers/` | ASP.NET MVC controllers with the HTTP routes. The host registers the assembly as an MVC ApplicationPart, so it finds them automatically. |
| `Services/` | The background task (`IPluginBackgroundTask`) and small scoped sync and login services. |
| `Dtos/` (Schulware), `Models/` (OdaOrg) | Records. In `Dtos/`, one record per file. |
| `Data/` | EF Core entities, `DbContext`, design-time factory, and `Migrations/`. |
| `Infrastructure/` | External client factories and helpers. |
| `Client/` | Kiota-generated API client (Schulware only). |
| `config.yml` | Sample runtime config. Deploy it as `Schuly.Plugin.<Name>.yml` in the server's plugins-config folder. |
