# EF Core migrations

Use EF Core migrations to give a plugin its own database and change its schema. The Schulware and OdaOrg
plugins do this.

## Each plugin gets its own database

The host passes each plugin a dedicated connection string in `PluginServiceContext.ConnectionString`.
It takes the main connection string and changes the database name to `schuly_plugin_<name>`, where
`<name>` is `ISchulyPlugin.Name` in lowercase with spaces replaced by underscores. For example,
`Schulware Integration` becomes `schuly_plugin_schulware_integration`.

Wire it into your `DbContext`:

```csharp
services.AddDbContext<SchulwareDbContext>(options => options.UseNpgsql(context.ConnectionString));
```

Your migrations never touch the main Schuly database.

## Add the packages

A plugin with a database references `Microsoft.EntityFrameworkCore`,
`Microsoft.EntityFrameworkCore.Design`, and `Npgsql.EntityFrameworkCore.PostgreSQL`. Copy the
references from
[`Schuly.Plugin.OdaOrg.csproj`](https://github.com/schulydev/SchulyPlugins/blob/main/src/Schuly.Plugin.OdaOrg/Schuly.Plugin.OdaOrg.csproj).

## Add a migration

You need the `dotnet-ef` tool. See [development setup](setup/development.md).

```sh
dotnet ef migrations add <Name> --project src/Schuly.Plugin.Schulware
```

Swap the project path for your plugin. The command writes the migration to the plugin's
`Data/Migrations/` folder and updates the model snapshot. For a plugin's first migration, add
`--output-dir Data/Migrations` to use the same folder.

## Add a design-time factory

`dotnet ef` builds the `DbContext` at design time without the runtime DI pipeline. Ship an
`IDesignTimeDbContextFactory<T>` next to your `DbContext`, such as `Data/SchulwareDbContextFactory.cs`.
Its connection string is a placeholder, because migrations need only the model, not a live database:

```csharp
internal sealed class SchulwareDbContextFactory : IDesignTimeDbContextFactory<SchulwareDbContext>
{
    public SchulwareDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SchulwareDbContext>()
            .UseNpgsql("Host=localhost;Database=schulware_design;Username=postgres;Password=postgres")
            .Options;
        return new SchulwareDbContext(options);
    }
}
```

## Apply migrations at runtime

Apply pending migrations from `MigrateAsync`. The host calls it each time it loads the plugin.

```csharp
public async Task MigrateAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
{
    using var scope = serviceProvider.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<SchulwareDbContext>();
    await db.Database.MigrateAsync(cancellationToken);
}
```

**Use `MigrateAsync()`, never `EnsureCreatedAsync`.** `EnsureCreatedAsync` creates the database on the
first run but does nothing on later schema changes, so new columns and indexes never reach an existing
database. `MigrateAsync()` creates the database on the first run and applies each schema change after
that.
