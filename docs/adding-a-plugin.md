# Adding a plugin

## Scaffold

1. Copy `src/Schuly.Plugin.Example/` to `src/Schuly.Plugin.<Name>/`.
2. Rename `ExamplePlugin.cs` → `<Name>Plugin.cs`; rename the class and namespace to match.
   The class implements `ISchulyPlugin`.
3. Rename the `.csproj` (and `.slnx`) to `Schuly.Plugin.<Name>`. Keep
   `<TargetFramework>net10.0</TargetFramework>` and the
   `Schuly.Plugin.Abstractions` `PackageReference`. Set `<Version>`, `<Description>`,
   `<Authors>` — these flow into the published distribution index.
4. Open an issue labeled `new-plugin`, then follow the
   [contributing workflow](contributing.md): branch → PR (`Closes #<issue>`) → squash-merge.

No workflow change is needed — `build_push.yml` discovers any `src/Schuly.Plugin.*/*.csproj`
automatically. See [setup/distribution.md](setup/distribution.md).

## The `ISchulyPlugin` lifecycle

The plugin class is the composition root (slim, like a `Program.cs`). The backend's plugin
host drives these in order at startup:

### `ConfigureServices(IServiceCollection services, PluginServiceContext context)`

Register your services, options, background task, and login. `context` exposes:

- `context.ConnectionString` — the plugin's dedicated Postgres connection string
  (the host mutates it to `schuly_plugin_<name>`; see [migrations.md](migrations.md)).
- `context.Configuration` — the plugin's YAML config (`Schuly.Plugin.<Name>.yml`).

Typical registrations (from Schulware/OdaOrg):

```csharp
services.AddDbContext<MyDbContext>(o => o.UseNpgsql(context.ConnectionString));

services.AddSingleton<MySyncTask>();
services.AddSingleton<IPluginBackgroundTask>(sp => sp.GetRequiredService<MySyncTask>());

// Per-plugin isolated, in-memory secret vault, keyed by the plugin name by the host.
services.AddScoped(sp => new MySecretStore(
    sp.GetRequiredKeyedService<IPluginVault>(MyPlugin.PluginName)));

services.AddScoped<IPluginLogin, MyLogin>();
```

> The vault key must be a constant (used with `[FromKeyedServices(PluginName)]`), so expose
> `public const string PluginName`. The vault is in-memory only — secrets do **not** survive
> a backend restart, and sync code must handle the empty-vault ("needs reconnect") case.

### `ConfigureEndpoints(IEndpointRouteBuilder endpoints)`

Map minimal-API routes here. The Example plugin uses this directly:

```csharp
endpoints.MapGet("/api/plugins/example/hello",
    (IPluginUserContext userContext) => Results.Ok(...)).RequireAuthorization();
endpoints.MapGet("/api/plugins/example/info", () => Results.Ok(...)).AllowAnonymous();
```

The Schulware and OdaOrg plugins instead leave this empty and put routes in `Controllers/`
as ASP.NET MVC controllers — the host registers the plugin assembly as an MVC
ApplicationPart, so `[ApiController]` controllers are auto-discovered. Either approach works;
controllers scale better for larger surfaces.

### `MigrateAsync(IServiceProvider serviceProvider, CancellationToken)`

Apply EF Core migrations. Resolve the `DbContext` from a scope and call
`db.Database.MigrateAsync()`. The Example plugin (no DB) returns `Task.CompletedTask`.
See [migrations.md](migrations.md) — use `MigrateAsync()`, never `EnsureCreatedAsync`.

### `IPluginBackgroundTask` (optional)

Recurring work. Implement `Name`, `Interval`, and `ExecuteAsync`. The backend's
`PluginBackgroundTaskHost` invokes `ExecuteAsync` on each `Interval` tick:

```csharp
public class MySyncTask : IPluginBackgroundTask
{
    public string Name => "My Data Sync";
    public TimeSpan Interval => TimeSpan.FromMinutes(30);

    public async Task ExecuteAsync(IServiceProvider serviceProvider, CancellationToken ct)
    {
        using var scope = serviceProvider.CreateScope();
        // resolve scoped services, do work
    }
}
```
</content>
