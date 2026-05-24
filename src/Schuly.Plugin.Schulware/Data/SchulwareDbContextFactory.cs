using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Schuly.Plugin.Schulware.Data
{
    /// <summary>Lets `dotnet ef migrations add/remove` construct the DbContext at
    /// design time without going through the runtime DI pipeline. The connection
    /// string here is a placeholder — migrations only need the model, not a live
    /// database.</summary>
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
}
