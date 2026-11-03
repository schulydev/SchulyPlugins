# Distribution

This page explains how plugins get from `main` to a server. Plugins use the Aniyomi-style two-branch
model:

- `main` holds the C# plugin source projects.
- `repo` holds built DLLs and a machine-readable index, all auto-generated.

## The build and publish workflow

[`build_push.yml`](https://github.com/schulydev/SchulyPlugins/blob/main/.github/workflows/build_push.yml)
runs on pushes to `main` that touch `src/Schuly.Plugin.*/**` or the workflow itself, and on manual
dispatch. A change only in `src/Shared/` doesn't trigger it.

It has three jobs:

1. **discover** finds each `src/<folder>/Schuly.Plugin.*.csproj` and emits a build matrix. A new plugin
   folder with a csproj needs no workflow change.
2. **build** runs `dotnet publish -c Release` for each plugin, then stages:
   - `dll/<AssemblyName>-v<Version>.dll`: the plugin assembly.
   - `dll/<AssemblyName>-v<Version>-deps.zip`: its third-party dependencies, such as Kiota or AngleSharp.
     The zip leaves out `Microsoft.AspNetCore.*`, `Microsoft.Extensions.*`,
     `Microsoft.EntityFrameworkCore.*`, `Npgsql.*`, `Mediator.*`, and `Schuly.Plugin.Abstractions.dll`,
     which the server provides. A plugin with no dependencies gets a marker-only zip, so the index schema
     stays uniform.
   - A metadata JSON for the plugin with `name`, `pkg`, `dll`, `deps`, `version`, `contract`,
     `description`, and `authors`. `contract` is the MAJOR.MINOR of `Schuly.Plugin.Abstractions` the
     plugin resolved. `name`, `version`, `description`, and `authors` come from the csproj through
     `dotnet msbuild -getProperty`.
3. **publish** merges the JSON files into `index.json`, sorted by name, and a minified `index.min.json`.
   It then replaces `dll/` and both index files on the `repo` branch and commits. The branch holds only
   the builds from the latest run.

`AssemblyName`, `Version`, `Description`, and `Authors` come from each plugin's csproj, so keep them
current.

## Install a plugin

Servers download plugins from the `repo` branch automatically. Operators list the plugins they want in
`plugins.yml`. See [Plugin management](/SchulyServer/plugin-management). `index.min.json` is the catalog
servers read to find plugins and versions.
