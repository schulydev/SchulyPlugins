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

## Installing (operators)

Prebuilt artifacts are served from `raw.githubusercontent.com/schulydev/SchulyPlugins/repo`.
Download into the backend's `/app/plugins/` folder:

```sh
BASE=https://raw.githubusercontent.com/schulydev/SchulyPlugins/repo
NAME=Schuly.Plugin.Schulware
VERSION=2.4.2

# 1. Plugin DLL
curl -L "$BASE/dll/$NAME-v$VERSION.dll" -o /app/plugins/$NAME.dll

# 2. Its third-party dependencies
curl -L "$BASE/dll/$NAME-v$VERSION-deps.zip" -o /tmp/deps.zip
unzip -o /tmp/deps.zip -d /app/plugins/

# 3. Drop the plugin's Schuly.Plugin.<Name>.yml into the backend's plugins-config/
```

The backend already provides framework + host-shared assemblies, so only the plugin DLL and
its bundled third-party deps need to land in `plugins/`. `index.min.json` is the catalog
clients read to discover available plugins and versions.
