# <p align="center">SchulyPlugins</p>
<p align="center">
  <img src="https://raw.githubusercontent.com/schulydev/Schuly/main/assets/app_icon.png" width="160" alt="Schuly Logo">
</p>
<p align="center">
  <strong>Official plugins for the Schuly backend — monorepo</strong>
</p>
<p align="center">
  <a href="https://github.com/schulydev/SchulyPlugins/stargazers"><img src="https://img.shields.io/github/stars/schulydev/SchulyPlugins?style=flat&color=3da8ff" alt="GitHub stars"/></a>
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-9.0-3da8ff" alt=".NET"/></a>
  <a href="https://schuly.dev"><img src="https://img.shields.io/badge/site-schuly.dev-3da8ff" alt="Website"/></a>
</p>

Plugins built against the [SchulyPluginAbstractions](https://github.com/schulydev/SchulyPluginAbstractions) contract. Each plugin extends [SchulyBackend](https://github.com/schulydev/SchulyBackend) with background tasks, event handlers, and integrations with external school-management systems.

## Plugins

| Plugin | Purpose |
|---|---|
| `Schuly.Plugin.Example` | Reference / scaffolding plugin for new authors |
| `Schuly.Plugin.Schulware` | Integration with [SchulwareAPI](https://github.com/PianoNic/SchulwareAPI) |

## The Schuly ecosystem

| Repo | Purpose |
|---|---|
| [**Schuly**](https://github.com/schulydev/Schuly) | Flutter mobile app |
| [**SchulyBackend**](https://github.com/schulydev/SchulyBackend) | ASP.NET Core API backend |
| [**SchulyPluginAbstractions**](https://github.com/schulydev/SchulyPluginAbstractions) | Plugin contract (NuGet) |
| [**SchulyPlugins**](https://github.com/schulydev/SchulyPlugins) | Official plugins monorepo *(this repo)* |
| [**SchulyWebsite**](https://github.com/schulydev/SchulyWebsite) | Landing site ([schuly.dev](https://schuly.dev)) |

## Add a new plugin

1. Copy `Schuly.Plugin.Example/` to `Schuly.Plugin.<Name>/`
2. Implement `ISchulyPlugin` from the [PluginAbstractions](https://github.com/schulydev/SchulyPluginAbstractions) NuGet package
3. Open an issue with the `new-plugin` label
4. Open a PR
