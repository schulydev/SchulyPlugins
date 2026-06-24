# SchulyPlugins documentation

Official plugins for the [Schuly backend](https://github.com/schulydev/SchulyBackend). Each
plugin lives in its own folder under `src/`, targets **.NET 10**, and implements
`ISchulyPlugin` from [`Schuly.Plugin.Abstractions`](https://github.com/schulydev/SchulyPluginAbstractions)
(consumed as a NuGet `PackageReference`). The backend loads built plugin DLLs at startup and
drives their lifecycle (`ConfigureServices`, `ConfigureEndpoints`, `MigrateAsync`) plus any
recurring `IPluginBackgroundTask`. Plugins are built and shipped as DLLs to the `repo` branch
(Aniyomi-style distribution) and dropped into the backend's `/app/plugins/` folder by operators.

## Repository layout

| Path | Role |
|---|---|
| `src/Schuly.Plugin.Example/` | Reference / scaffolding plugin. Minimal `ISchulyPlugin` with minimal-API endpoints, anonymous + authorized routes, and a per-plugin vault demo. |
| `src/Schuly.Plugin.Schulware/` | Schulnetz integration via [SchulwareAPI](https://github.com/schulydev/SchulwareAPI). EF Core + Postgres, Kiota-generated client, background sync task, MVC controllers. |
| `src/Schuly.Plugin.OdaOrg/` | OdaOrg (ICT-BBAG apprenticeship portal) integration. HttpClient + AngleSharp scraper, EF Core + Postgres, background sync task. |
| `.github/workflows/build_push.yml` | Discovers every `src/Schuly.Plugin.*/*.csproj`, builds, and publishes DLLs + index to the `repo` branch. |
| `.github/workflows/sync-version-on-release.yml` | Version sync on release. |

A Schulware/OdaOrg plugin folder is organised as:

| Folder | Contents |
|---|---|
| `Controllers/` | ASP.NET MVC controllers — HTTP routes (host registers the assembly as an MVC ApplicationPart, so they are auto-discovered). |
| `Services/` | Background task (`IPluginBackgroundTask`) + focused scoped sync/login services. |
| `Dtos/` / `Models/` | One record per file. |
| `Data/` | EF Core entities, `DbContext`, design-time factory, and `Migrations/`. |
| `Infrastructure/` | External client factories / helpers. |
| `Client/` | Kiota-generated API client (Schulware only). |
| `config.yml` | Sample runtime config (`Schuly.Plugin.<Name>.yml` in the backend's plugins-config dir). |

## Documents

| Doc | What it covers |
|---|---|
| [setup/development.md](setup/development.md) | Prerequisites, building a plugin, running it against a live backend. |
| [adding-a-plugin.md](adding-a-plugin.md) | Scaffolding a new plugin + the `ISchulyPlugin` lifecycle. |
| [migrations.md](migrations.md) | Per-plugin EF Core migrations and the dedicated Postgres DB. |
| [setup/kiota-client.md](setup/kiota-client.md) | Regenerating the Schulware Kiota client. |
| [setup/distribution.md](setup/distribution.md) | How plugins are built and shipped to the `repo` branch. |
| [contributing.md](contributing.md) | The enforced issue → branch → PR → squash workflow. |
</content>
</invoke>
