using Microsoft.EntityFrameworkCore;

namespace Schuly.Plugin.Microsoft.Data
{
    public class MicrosoftDbContext(DbContextOptions<MicrosoftDbContext> options) : DbContext(options)
    {
        public DbSet<MicrosoftAccount> Accounts { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<MicrosoftAccount>(entity =>
            {
                entity.HasKey(a => a.Id);
                entity.Property(a => a.Email).HasMaxLength(320);
                entity.Property(a => a.DisplayName).HasMaxLength(200);
                entity.Property(a => a.MicrosoftUserId).HasMaxLength(100);
                entity.HasIndex(a => new { a.ApplicationUserId, a.Email }).IsUnique();
            });
        }
    }
}
