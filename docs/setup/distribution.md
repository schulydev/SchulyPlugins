# Distribution

Plugins follow the **Aniyomi-style** two-branch model:

- `main` - the C# plugin source projects.
- `repo` - built DLLs plus a machine-readable index, all auto-generated.

## The build + publish workflow

`.github/workflows/build_push.yml` runs on pushes to `main` that touch
`src/Schuly.Plugin.*/**` (or the workflow itself), and on manual dispatch. It has three jobs:

1. **discover** - globs `src/**/Schuly.Plugin.*.csproj` and emits a build matrix. Adding a new
   plugin folder with a csproj is picked up with no workflow change.
2. **build** (per plugin) - `dotnet publish -c Release`, then stages:
   - `dll/<AssemblyName>-v<Version>.dll` - the plugin assembly, shipped standalone so
     operators can pin/swap it independently.
   - `dll/<AssemblyName>-v<Version>-deps.zip` - its third-party dependencies. Host-provided
     assemblies are dropped (ASP.NET Core, EF Core, Npgsql, Mediator, and the Schuly host
     assemblies including `Schuly.Plugin.Abstractions`); only true third-party NuGet deps
     (Kiota, AngleSharp, …) are bundled. A plugin with none gets a marker-only zip so the
     index schema stays uniform.
   - a per-plugin metadata JSON (`name`, `pkg`, `dll`, `deps`, `version`, `description`,
     `authors`), read from the csproj via `dotnet msbuild -getProperty`.
3. **publish** - merges the per-plugin JSONs into `index.json` (sorted by name) and a minified
   `index.min.json`, copies the DLLs + dep zips, and commits everything to the `repo` branch.

`AssemblyName`, `Version`, `Description`, and `Authors` therefore come straight from each
plugin's `.csproj` `PropertyGroup` - keep them current.

## Installing

Servers download plugins from this branch automatically. Operators list the plugins they want in
`plugins.yml`. See [Plugin management](/SchulyServer/plugin-management). `index.min.json` is the
catalog servers read to find plugins and versions.
