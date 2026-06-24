# Regenerating the Schulware Kiota client

The Schulware plugin talks to [SchulwareAPI](https://github.com/schulydev/SchulwareAPI) through
a [Kiota](https://learn.microsoft.com/openapi/kiota/)-generated client under
`src/Schuly.Plugin.Schulware/Client/`. The generated code is committed; the **OpenAPI JSON is
not** — always regenerate directly from the live URL.

## First generation

```sh
cd src/Schuly.Plugin.Schulware
kiota generate \
  --openapi https://schlwr.pianonic.ch/openapi.json \
  --language CSharp \
  --output Client \
  --namespace-name Schuly.Plugin.Schulware.Client \
  --class-name SchulwareApiClient
```

This produces `Client/SchulwareApiClient.cs`, the request builders under `Client/Api/`, the
DTOs under `Client/Models/`, and a `Client/kiota-lock.json` recording the source URL, the
generator version, and the serializer/deserializer set.

## Updating after the API changes

Once the lockfile exists, re-run from inside `Client/`:

```sh
cd src/Schuly.Plugin.Schulware/Client
kiota update
```

`kiota update` reads `kiota-lock.json` and regenerates against the same `descriptionLocation`
(the live OpenAPI URL) with the recorded options — equivalent to re-running `kiota generate`.

## Rules

- **Never commit the OpenAPI JSON** locally. Generation always reads it from the live URL so
  the client tracks the deployed API.
- The Kiota runtime packages (`Microsoft.Kiota.*`) are `PackageReference`s in
  `Schuly.Plugin.Schulware.csproj`; keep their versions compatible with the generator.
- Regeneration is a code change like any other — follow the [contributing](../contributing.md)
  workflow.
</content>
