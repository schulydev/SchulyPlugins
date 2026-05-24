using Microsoft.EntityFrameworkCore;

namespace Schuly.Plugin.Schulware.Data
{
    public class SchulwareDbContext(DbContextOptions<SchulwareDbContext> options) : DbContext(options)
    {
        public DbSet<SchulwareAccount> Accounts { get; set; }
        public DbSet<SyncState> SyncStates { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SchulwareAccount>(entity =>
            {
                entity.HasKey(a => a.Id);
                entity.HasIndex(a => new { a.ApplicationUserId, a.SchulnetzBaseUrl }).IsUnique();
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
