# EF Core migrations

Plugins that persist data (Schulware, OdaOrg) own their schema via EF Core migrations.

## Each plugin gets its own database

The backend's plugin host hands the plugin a dedicated connection string via
`PluginServiceContext.ConnectionString` - it mutates the host connection string so the
database name becomes `schuly_plugin_<name>`. The plugin wires it into its `DbContext`:

```csharp
services.AddDbContext<SchulwareDbContext>(o => o.UseNpgsql(context.ConnectionString));
```

Migrations therefore never touch the main Schuly database; each plugin's schema is isolated.

## Adding a migration

```sh
dotnet ef migrations add <Name> --project src/Schuly.Plugin.Schulware
```

(Swap the project path for the plugin you're working on.) This writes the migration into the
plugin's `Data/Migrations/` folder and updates the model snapshot. Requires the `dotnet-ef`
tool - see [setup/development.md](setup/development.md).

## Design-time factory

`dotnet ef` constructs the `DbContext` at design time without the runtime DI pipeline, so each
plugin ships an `IDesignTimeDbContextFactory<T>` next to its `DbContext`
(e.g. `Data/SchulwareDbContextFactory.cs`). The connection string in the factory is a
throwaway placeholder - migrations only need the model, not a live database:

```csharp
internal sealed class SchulwareDbContextFactory : IDesignTimeDbContextFactory<SchulwareDbContext>
{
    public SchulwareDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SchulwareDbContext>()
            .UseNpgsql("Host=localhost;Database=schulware_design;Username=postgres;Password=postgres")
            .Options);
}
```

## Apply migrations at runtime, not `EnsureCreated`

Apply pending migrations from `MigrateAsync`:

```csharp
public async Task MigrateAsync(IServiceProvider sp, CancellationToken ct = default)
{
    using var scope = sp.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<SchulwareDbContext>();
    await db.Database.MigrateAsync(ct);
}
```

**Use `MigrateAsync()`, never `EnsureCreatedAsync`.** `EnsureCreatedAsync` creates the
database on first run but does nothing on later schema changes, so column/index additions
would never land on an existing database. `MigrateAsync()` creates the DB on first run **and**
applies every schema delta afterwards.
