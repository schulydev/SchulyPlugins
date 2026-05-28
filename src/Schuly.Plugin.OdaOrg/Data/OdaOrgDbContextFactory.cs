using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Schuly.Plugin.OdaOrg.Data
{
    /// <summary>Design-time factory so `dotnet ef migrations` can build the model
    /// without the runtime DI pipeline. The connection string is a placeholder —
    /// migrations only need the model shape, not a live database.</summary>
    internal sealed class OdaOrgDbContextFactory : IDesignTimeDbContextFactory<OdaOrgDbContext>
    {
        public OdaOrgDbContext CreateDbContext(string[] args)
        {
            var options = new DbContextOptionsBuilder<OdaOrgDbContext>()
                .UseNpgsql("Host=localhost;Database=odaorg_design;Username=postgres;Password=postgres")
                .Options;
            return new OdaOrgDbContext(options);
        }
    }
}
