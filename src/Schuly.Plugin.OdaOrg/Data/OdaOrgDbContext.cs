using Microsoft.EntityFrameworkCore;

namespace Schuly.Plugin.OdaOrg.Data
{
    public class OdaOrgDbContext(DbContextOptions<OdaOrgDbContext> options) : DbContext(options)
    {
        public DbSet<OdaOrgAccount> Accounts { get; set; }
        public DbSet<SyncState> SyncStates { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<OdaOrgAccount>(entity =>
            {
                entity.HasKey(a => a.Id);
                entity.HasIndex(a => new { a.ApplicationUserId, a.BaseUrl }).IsUnique();
            });

            modelBuilder.Entity<SyncState>(entity =>
            {
                entity.HasKey(s => s.Id);
                entity.HasIndex(s => s.AccountId).IsUnique();
                entity.HasOne(s => s.Account).WithOne().HasForeignKey<SyncState>(s => s.AccountId);
            });
        }
    }
}
