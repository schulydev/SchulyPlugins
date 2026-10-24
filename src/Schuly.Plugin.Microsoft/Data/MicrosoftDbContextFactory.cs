using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Schuly.Plugin.Microsoft.Data
{
    internal sealed class MicrosoftDbContextFactory : IDesignTimeDbContextFactory<MicrosoftDbContext>
    {
        public MicrosoftDbContext CreateDbContext(string[] args)
        {
            var options = new DbContextOptionsBuilder<MicrosoftDbContext>()
                .UseNpgsql("Host=localhost;Database=microsoft_design;Username=postgres;Password=postgres")
                .Options;
            return new MicrosoftDbContext(options);
        }
    }
}
