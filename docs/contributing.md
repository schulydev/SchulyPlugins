# Contributing

These rules apply to this repo. For branches, commits, pull requests, and labels, follow the
[shared workflow](/contribute/).

## Labels

This repo adds these labels to the shared ones:

| Label | Use for |
|---|---|
| `new-plugin` | A proposal or scaffolding for a new plugin |
| `plugin: example`, `plugin: schulware`, `plugin: odaorg`, `plugin: microsoft` | Changes to that plugin |

## Rules

- Use a CLI generator where one exists: `gh issue create`, `gh pr create`,
  `dotnet ef migrations add`, `kiota`.
- Bump `<Version>` in the plugin's csproj when you change the plugin's behavior. The published index
  reads `Version`, `Description`, and `Authors` from each csproj, and servers compare the version to
  decide whether to update.

## Related pages

- [Add a plugin](adding-a-plugin.md)
- [EF Core migrations](migrations.md)
- [Kiota client](setup/kiota-client.md)
- [Distribution](setup/distribution.md): how merges to `main` ship
