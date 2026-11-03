# Kiota client

Use this page to regenerate the client the Schulware plugin uses to call
[SchulwareAPI](https://github.com/schulydev/SchulwareAPI). It's a
[Kiota](https://learn.microsoft.com/openapi/kiota/)-generated client in
`src/Schuly.Plugin.Schulware/Client/`.

The generated code is committed. The OpenAPI JSON is not, so you always regenerate from the live URL.

## Update after the API changes

Run this from inside `Client/`:

```sh
cd src/Schuly.Plugin.Schulware/Client
kiota update --output .
```

`kiota update` reads `kiota-lock.json` and regenerates against the same `descriptionLocation`, the live
OpenAPI URL, with the recorded options. It gives the same result as running `kiota generate` again.
Without `--output .`, it looks in `./output` and fails.

## Generate from scratch

```sh
cd src/Schuly.Plugin.Schulware
kiota generate \
  --openapi https://schlwr.pianonic.ch/openapi.json \
  --language CSharp \
  --output Client \
  --namespace-name Schuly.Plugin.Schulware.Client \
  --class-name SchulwareApiClient
```

This produces `Client/SchulwareApiClient.cs`, the request builders in `Client/Api/`, the DTOs in
`Client/Models/`, and `Client/kiota-lock.json`. The lock file records the source URL, the generator
version, and the serializer and deserializer set.

## Rules

- **Never commit the OpenAPI JSON.** Generation always reads it from the live URL, so the client tracks
  the deployed API.
- Keep the Kiota runtime packages (`Microsoft.Kiota.*`) compatible with the generator. They're
  `PackageReference`s in `Schuly.Plugin.Schulware.csproj`.
- Treat regeneration as a code change. Follow the [shared workflow](/contribute/).
