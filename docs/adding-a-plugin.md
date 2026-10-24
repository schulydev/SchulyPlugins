# Adding a plugin

## Scaffold

`src/Schuly.Plugin.Example/` - what you're copying - is intentionally minimal:

```
src/Schuly.Plugin.Example/
├── ExamplePlugin.cs                  # ISchulyPlugin implementation
├── Schuly.Plugin.Example.csproj
└── Schuly.Plugin.Example.slnx        # opens this plugin standalone in an IDE
```

Real plugins grow a `Controllers/`, `Data/` (a `DbContext` + EF migrations), and
`Services/` folder as they need them - see `src/Schuly.Plugin.Schulware/` or
`src/Schuly.Plugin.OdaOrg/` for what that looks like once a plugin talks to a
database and an external API. None of that is required to start.

1. Copy `src/Schuly.Plugin.Example/` to `src/Schuly.Plugin.<Name>/`:

   ```
   src/Schuly.Plugin.<Name>/
   ├── <Name>Plugin.cs
   ├── Schuly.Plugin.<Name>.csproj
   └── Schuly.Plugin.<Name>.slnx
   ```

2. Rename `ExamplePlugin.cs` → `<Name>Plugin.cs`; rename the class and namespace to match.
   The class implements `ISchulyPlugin`.
3. Rename the `.csproj` (and `.slnx`) to `Schuly.Plugin.<Name>`. Keep
   `<TargetFramework>net10.0</TargetFramework>` and the
   `Schuly.Plugin.Abstractions` `PackageReference`. Set `<Version>`, `<Description>`,
   `<Authors>` - these flow into the published distribution index.
4. Open an issue labeled `new-plugin`, then follow the
   [contributing workflow](contributing.md): branch → PR (`Closes #<issue>`) → squash-merge.

No workflow change is needed - `build_push.yml` discovers any `src/Schuly.Plugin.*/*.csproj`
automatically. See [setup/distribution.md](setup/distribution.md).

## The `ISchulyPlugin` lifecycle

The plugin class is the composition root (slim, like a `Program.cs`). The backend's plugin
host drives these in order at startup:

### `ConfigureServices(IServiceCollection services, PluginServiceContext context)`

Register your services, options, background task, and login. `context` exposes:

- `context.ConnectionString` - the plugin's dedicated Postgres connection string
  (the host mutates it to `schuly_plugin_<name>`; see [migrations.md](migrations.md)).
- `context.Configuration` - the plugin's YAML config (`Schuly.Plugin.<Name>.yml`).

Typical registrations (from Schulware/OdaOrg):

```csharp
services.AddDbContext<MyDbContext>(o => o.UseNpgsql(context.ConnectionString));

services.AddSingleton<MySyncTask>();
services.AddSingleton<IPluginBackgroundTask>(sp => sp.GetRequiredService<MySyncTask>());

// Takes the plugin's own IPluginVault, which the host registers.
services.AddScoped<MySecretStore>();

services.AddScoped<IPluginLogin, MyLogin>();
```

The host registers these contract services in every plugin's container - inject them, never
the backend's own types:

| Service | Use it to |
|---|---|
| `ISchoolData` | Provision the school and student, then sync agenda, grades, absences and documents |
| `IPluginVault` | Keep tokens and credentials, encrypted, in the plugin's own namespace |
| `IPluginFileStore` | Store a downloaded file (e.g. a report card) and get its key |
| `IPluginUserContext` | Read the current user during a login request |

Sync code builds plain records and hands them to `ISchoolData`. Give every record a stable
`ExternalId` (`Shared/ExternalIds.cs`) and convert provider wall-clock times to UTC first
(`Shared/SwissTime.cs` for Swiss systems); the backend stores UTC only.

```csharp
var records = lessons.Select(l => new AgendaRecord(ExternalIds.Of("lesson", l.Start.ToString("O"), l.Course), AgendaKind.Lesson, l.Title, l.Start, l.End, l.Room)).ToList();
await schoolData.SyncAgendaAsync(account.SchoolUserId!.Value, records, ct);
```

### `ConfigureEndpoints(IEndpointRouteBuilder endpoints)`

Map minimal-API routes here. The Example plugin uses this directly:

```csharp
endpoints.MapGet("/api/plugins/example/hello",
    (IPluginUserContext userContext) => Results.Ok(...)).RequireAuthorization();
endpoints.MapGet("/api/plugins/example/info", () => Results.Ok(...)).AllowAnonymous();
```

The Schulware and OdaOrg plugins instead leave this empty and put routes in `Controllers/`
as ASP.NET MVC controllers - the host registers the plugin assembly as an MVC
ApplicationPart, so `[ApiController]` controllers are auto-discovered. Either approach works;
controllers scale better for larger surfaces.

### `MigrateAsync(IServiceProvider serviceProvider, CancellationToken)`

Apply EF Core migrations. Resolve the `DbContext` from a scope and call
`db.Database.MigrateAsync()`. The Example plugin (no DB) returns `Task.CompletedTask`.
See [migrations.md](migrations.md) - use `MigrateAsync()`, never `EnsureCreatedAsync`.

### `IPluginBackgroundTask` (optional)

Recurring work. Implement `Name`, `Schedule`, and `ExecuteAsync`. The backend's
`PluginBackgroundTaskHost` invokes `ExecuteAsync` on the cadence declared by `Schedule`:

```csharp
public class MySyncTask : IPluginBackgroundTask
{
    public string Name => "My Data Sync";
    public PluginSchedule Schedule => PluginSchedule.Every(TimeSpan.FromMinutes(30));

    public async Task ExecuteAsync(IServiceProvider serviceProvider, CancellationToken ct)
    {
        using var scope = serviceProvider.CreateScope();
        // resolve scoped services, do work
    }
}
```
