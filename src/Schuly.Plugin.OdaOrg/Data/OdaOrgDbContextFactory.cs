using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Schuly.Plugin.OdaOrg.Data
{
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
