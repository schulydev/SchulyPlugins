# Add a plugin

This guide takes you from a copy of the example plugin to a plugin your local server loads, then
ships it. Before you start, [set up your environment](setup/development.md).

## 1. Copy the example

[`src/Schuly.Plugin.Example/`](https://github.com/schulydev/SchulyPlugins/tree/main/src/Schuly.Plugin.Example)
is minimal on purpose:

```
src/Schuly.Plugin.Example/
├── ExamplePlugin.cs                  # ISchulyPlugin implementation
├── Schuly.Plugin.Example.csproj
└── Schuly.Plugin.Example.slnx        # opens this plugin standalone in an IDE
```

1. Copy the folder to `src/Schuly.Plugin.<Name>/`.
2. Rename `ExamplePlugin.cs` to `<Name>Plugin.cs`. Rename the class and namespace to match. The class
   implements `ISchulyPlugin`.
3. Rename the `.csproj` and `.slnx` to `Schuly.Plugin.<Name>`, and update the project path inside the
   `.slnx`.

You get this:

```
src/Schuly.Plugin.<Name>/
├── <Name>Plugin.cs
├── Schuly.Plugin.<Name>.csproj
└── Schuly.Plugin.<Name>.slnx
```

Real plugins add `Controllers/`, `Data/` (a `DbContext` and EF migrations), and `Services/` as they
need them. None of that is required to start. To see what it looks like, read
[`Schuly.Plugin.Schulware`](https://github.com/schulydev/SchulyPlugins/tree/main/src/Schuly.Plugin.Schulware)
or [`Schuly.Plugin.OdaOrg`](https://github.com/schulydev/SchulyPlugins/tree/main/src/Schuly.Plugin.OdaOrg).

## 2. Set the project properties

In the `.csproj`:

1. Keep `<TargetFramework>net10.0</TargetFramework>` and the `Schuly.Plugin.Abstractions`
   `PackageReference`.
2. Set `<Version>`, `<Description>`, and `<Authors>`. They go into the published index.
3. To use the shared helpers in `src/Shared/` (namespace `Schuly.Plugin.Shared`), compile them in:

   ```xml
   <ItemGroup>
     <Compile Include="..\Shared\**\*.cs" />
   </ItemGroup>
   ```

## 3. Implement the plugin class

The plugin class is the composition root, slim like a `Program.cs`. The host creates it with
`Activator.CreateInstance`, so it needs a public parameterless constructor.

Choose `Name` carefully and keep it stable. The host derives the plugin's database name, vault
namespace, and `SCHULY_PLUGIN_<NAME>_` environment prefix from it.

When it loads a plugin, the host calls these members in this order:

1. `ConfigureServices`
2. `MigrateAsync`
3. `ConfigureEndpoints`

It then registers your controllers, schedules your background tasks, and adds your school system to
the catalog.

### `ConfigureServices(IServiceCollection services, PluginServiceContext context)`

Register your services, options, background task, and login. Each plugin has its own service
container. `context` gives you:

- `context.ConnectionString`: the plugin's own Postgres database. See [migrations](migrations.md).
- `context.Configuration`: the host configuration with the plugin's `Schuly.Plugin.<Name>.yml` and
  `SCHULY_PLUGIN_<NAME>_` environment variables on top.

Typical registrations, from Schulware and OdaOrg:

```csharp
services.AddDbContext<MyDbContext>(o => o.UseNpgsql(context.ConnectionString));

services.AddSingleton<MySyncTask>();
services.AddSingleton<IPluginBackgroundTask>(sp => sp.GetRequiredService<MySyncTask>());

// Takes the plugin's own IPluginVault, which the host registers.
services.AddScoped<MySecretStore>();

services.AddScoped<IPluginLogin, MyLogin>();
```

The host registers these contract services in each plugin's container. Inject them, never the
backend's own types. The host refuses to load a plugin that references a backend assembly.

| Service | Use it to |
|---|---|
| `ISchoolData` | Provision the school and student, then sync agenda, grades, exams, absences, documents, and semester reports |
| `IPluginVault` | Keep tokens and credentials, encrypted, in the plugin's own namespace |
| `IPluginFileStore` | Store a downloaded file, such as a report card, and get its key |
| `IPluginUserContext` | Read the current user during a login request |
| `ILinkedAccounts` | Read and save accounts a user linked with an outside provider, shared by all plugins |

The host also provides `ILogger<T>`, `IHttpClientFactory`, `IHttpContextAccessor`, and `IConfiguration`.

### `MigrateAsync(IServiceProvider serviceProvider, CancellationToken)`

Apply EF Core migrations. Resolve the `DbContext` from a scope and call
`db.Database.MigrateAsync()`. Use `MigrateAsync()`, never `EnsureCreatedAsync`. See
[migrations](migrations.md).

A plugin without a database, such as the Example plugin, returns `Task.CompletedTask`.

### `ConfigureEndpoints(IEndpointRouteBuilder endpoints)`

Map minimal-API routes here. The Example plugin does this:

```csharp
endpoints.MapGet("/api/plugins/example/hello", () =>
{
    return Results.Ok(new { Message = "Hello from the Example Plugin!", Plugin = Name, Version });
}).RequireAuthorization();

endpoints.MapGet("/api/plugins/example/info", () =>
{
    return Results.Ok(new { Name, Version, Description = "A sample plugin demonstrating the Schuly plugin system." });
}).AllowAnonymous();
```

Or leave this method empty and put your routes in `Controllers/`, as the Schulware, OdaOrg, and
Microsoft plugins do. The host registers the plugin assembly as an MVC ApplicationPart, so it finds
`[ApiController]` controllers automatically. Both approaches work. Controllers scale better for larger
surfaces.

## 4. Add features

Add the parts your plugin needs. The [contract reference](/SchulyPluginAbstractions/contract) lists
every interface and record.

### Let users connect an account

Implement `IPluginLogin` and register it. Its `SchoolSystem` descriptor is what makes your school system
appear in the app.

### Sync school data

Build plain records and hand them to `ISchoolData`:

- Give each record a stable `ExternalId` (`src/Shared/ExternalIds.cs`).
- Convert provider wall-clock times to UTC first (`src/Shared/SwissTime.cs` for Swiss systems). The
  backend stores UTC only.

```csharp
var records = lessons.Select(l => new AgendaRecord(ExternalIds.Of("lesson", l.Start.ToString("O"), l.Course), AgendaKind.Lesson, l.Title, l.Start, l.End, l.Room)).ToList();
await schoolData.SyncAgendaAsync(account.SchoolUserId!.Value, records, ct);
```

### Delete a user's data

If your plugin stores anything per user, implement `IPluginEventHandler<PluginUserPurgeEvent>` and
register it, or deleted accounts leave that data behind:

- Remove everything the user owns, in the plugin's database and its vault.
- Remove vault secrets before the database rows. The vault can't be searched, so a retry finds secrets
  only through the row.
- Make it safe to run twice, and let exceptions reach the host. The host counts a thrown exception as a
  failed purge and retries it daily. It also redelivers the event for any `ApplicationUserId` column in
  your database whose owner no longer exists, so name the column that and make it a `uuid`.

### Run recurring work

Implement `IPluginBackgroundTask`: `Name`, `Schedule`, and `ExecuteAsync`. The host runs `ExecuteAsync`
on the `Schedule`. Operators override a schedule by the task's `Name`, so keep the name stable.

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

## 5. Load it on your local server

1. Follow [Run a plugin on a local server](setup/development.md#run-a-plugin-on-a-local-server).
2. Look for `Loaded plugin <your plugin name> v<version>` in the server log. If the load fails, the log
   has `Failed to load plugin` and the reason.
3. Call one of your routes to confirm it responds.

## 6. Ship it

1. Open an issue labeled `new-plugin`, then follow the [shared workflow](/contribute/).
2. Merge to `main`. `build_push.yml` finds any `src/Schuly.Plugin.*/*.csproj` automatically, so you
   don't change a workflow. See [distribution](setup/distribution.md).
3. To install the published plugin, add it to the server's `plugins.yml`. See
   [Plugin management](/SchulyServer/plugin-management).
