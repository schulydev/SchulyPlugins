# Contributing

The workflow below is **enforced** - never commit straight to `main`.

## Workflow

1. **Open a labeled issue** describing the change. Pick the right label
   (e.g. `new-plugin` for a new plugin, plus `bug` / `enhancement` / `documentation` / etc.).
2. **Branch** off `main`: `feature/<issue#>_PascalCase` or `fix/<issue#>_PascalCase`.
3. **Commit** with a short, imperative subject (e.g. `Add OdaOrg vacation sync`).
4. **Open a PR** that links the issue. The PR body is **Summary + `Closes #<issue>` only** -
   no test plans.
5. **Squash-merge**, then delete the branch.

## Hard rules

- **No AI / Claude attribution anywhere - ever.** Not in commit messages, PR titles/bodies,
  or issue text. No `Co-Authored-By` trailers, no "Generated with" lines.
- Use CLI generators where one exists (`gh issue create`, `gh pr create`, `dotnet ef migrations add`, `kiota`, …).
- Keep changes scoped: the published distribution index reads `Version` / `Description` /
  `Authors` from each plugin csproj, so bump `<Version>` when you change a plugin's behavior.

## See also

- [adding-a-plugin.md](adding-a-plugin.md) - scaffolding + lifecycle.
- [migrations.md](migrations.md) - EF Core migrations.
- [setup/kiota-client.md](setup/kiota-client.md) - regenerating the Schulware client.
- [setup/distribution.md](setup/distribution.md) - how merges to `main` ship.
