using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Schuly.Plugin.Schulware.Data
{
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
