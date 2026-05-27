# Schuly plugins (auto-generated)

DLLs + index built from `main`. Each plugin ships as:

- `dll/<name>-v<version>.dll` — the plugin assembly itself
- `dll/<name>-v<version>-deps.zip` — its third-party dependencies (zipped)
- `index.min.json` — machine-readable catalog

## Install

```sh
BASE=https://raw.githubusercontent.com/schulydev/SchulyPlugins/repo
NAME=Schuly.Plugin.Schulware
VERSION=2.2.2

# 1. Drop the plugin DLL into the backend's plugins/ folder
curl -L "$BASE/dll/$NAME-v$VERSION.dll" -o /app/plugins/$NAME.dll

# 2. Extract its dependencies into the same folder
curl -L "$BASE/dll/$NAME-v$VERSION-deps.zip" -o /tmp/deps.zip
unzip -o /tmp/deps.zip -d /app/plugins/

# 3. Drop the plugin's YAML config into plugins-config/
```

Framework + host-shared assemblies are not in the deps zip; the
backend already provides them. Only the plugin's true third-party
NuGet dependencies (Kiota, etc.) are bundled.
