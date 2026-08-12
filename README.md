# <p align="center">SchulyPlugins</p>
<p align="center">
  <img src="./assets/app_icon.png" width="160" alt="Schuly Logo">
</p>
<p align="center">
  <strong>Official plugins for the Schuly backend - monorepo</strong>
</p>
<p align="center">
  <a href="https://github.com/schulydev/SchulyPlugins/stargazers"><img src="https://img.shields.io/github/stars/schulydev/SchulyPlugins?style=flat&color=3da8ff" alt="GitHub stars"/></a>
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-10.0-3da8ff" alt=".NET"/></a>
  <a href="https://docs.schuly.dev/SchulyPlugins/"><img src="https://img.shields.io/badge/docs-docs.schuly.dev-3da8ff" alt="Documentation"/></a>
  <a href="https://schuly.dev"><img src="https://img.shields.io/badge/site-schuly.dev-3da8ff" alt="Website"/></a>
</p>

Plugins built against the [SchulyPluginAbstractions](https://github.com/schulydev/SchulyPluginAbstractions) contract. Each plugin extends [SchulyBackend](https://github.com/schulydev/SchulyBackend) with background tasks, event handlers, and integrations with external school-management systems - and contributes its own entry to the app's school-system catalog.

## Plugins

| Plugin | Purpose |
|---|---|
| `Schuly.Plugin.Example` | Reference / scaffolding plugin for new authors |
| `Schuly.Plugin.Schulware` | Integration with [SchulwareAPI](https://github.com/PianoNic/SchulwareAPI) |
| `Schuly.Plugin.OdaOrg` | Integration with OdA Org |

## Add a new plugin

1. Copy `Schuly.Plugin.Example/` to `Schuly.Plugin.<Name>/`
2. Implement `ISchulyPlugin` from the [PluginAbstractions](https://github.com/schulydev/SchulyPluginAbstractions) NuGet package
3. Open an issue with the `new-plugin` label
4. Open a PR

[Adding a plugin](https://docs.schuly.dev/SchulyPlugins/adding-a-plugin) walks through it in full.

## Documentation

Full documentation lives at **[docs.schuly.dev/SchulyPlugins](https://docs.schuly.dev/SchulyPlugins/)**.

| Guide | What it covers |
|---|---|
| [Adding a plugin](https://docs.schuly.dev/SchulyPlugins/adding-a-plugin) | Scaffold, implement, and register a new plugin. |
| [Development setup](https://docs.schuly.dev/SchulyPlugins/setup/development) | Build and run plugins against a local backend. |
| [Distribution](https://docs.schuly.dev/SchulyPlugins/setup/distribution) | How built plugins are packaged and served to the backend. |
| [Kiota client](https://docs.schuly.dev/SchulyPlugins/setup/kiota-client) | Regenerate a plugin's HTTP client from an upstream OpenAPI spec. |
| [Migrations](https://docs.schuly.dev/SchulyPlugins/migrations) | Per-plugin EF Core migrations. |
| [Contributing](https://docs.schuly.dev/SchulyPlugins/contributing) | Workflow, branch and PR conventions. |

The contract itself is documented at [the plugin contract](https://docs.schuly.dev/SchulyPluginAbstractions/contract).

## The Schuly ecosystem

| Repo | Purpose |
|---|---|
| [**Schuly**](https://github.com/schulydev/Schuly) | Flutter mobile app |
| [**SchulyBackend**](https://github.com/schulydev/SchulyBackend) | ASP.NET Core API backend |
| [**SchulyKeycloak**](https://github.com/schulydev/SchulyKeycloak) | Keycloak image + the `schuly` realm |
| [**SchulyPluginAbstractions**](https://github.com/schulydev/SchulyPluginAbstractions) | Plugin contract (NuGet) |
| [**SchulyPlugins**](https://github.com/schulydev/SchulyPlugins) | Official plugins monorepo *(this repo)* |
| [**SchulyWebsite**](https://github.com/schulydev/SchulyWebsite) | Landing site ([schuly.dev](https://schuly.dev)) |
| [**SchulyDocs**](https://github.com/schulydev/SchulyDocs) | Documentation site ([docs.schuly.dev](https://docs.schuly.dev)) |
