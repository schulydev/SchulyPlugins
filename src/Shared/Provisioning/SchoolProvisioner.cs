using Microsoft.EntityFrameworkCore;
using Schuly.Domain;
using Schuly.Domain.Enums;
using Schuly.Infrastructure;

namespace Schuly.Plugin.Shared.Provisioning
{
    /// <summary>
    /// Shared main-DB provisioning used by both plugins. The School is keyed on the
    /// stable portal instance URL (stored in School.Website), NOT the user-typed
    /// display name - so two unrelated users at different schools can't be merged
    /// into one tenant by typing the same name.
    /// </summary>
    public static class SchoolProvisioner
    {
        public static async Task<School> EnsureSchoolAsync(SchulyDbContext db, string? baseUrl, string? displayName, CancellationToken ct = default)
        {
            var key = InstanceUrl.Canonical(baseUrl);
            var logo = InstanceUrl.LogoFor(baseUrl);

            var school = await db.Schools.FirstOrDefaultAsync(s => s.Website == key, ct);
            if (school is null)
            {
                school = new School
                {
                    Name = string.IsNullOrWhiteSpace(displayName) ? InstanceUrl.Host(baseUrl) : displayName!,
                    Website = key,
                    LogoUrl = logo,
                };
                db.Schools.Add(school);
                await db.SaveChangesAsync(ct);
            }
            else if (string.IsNullOrWhiteSpace(school.LogoUrl))
            {
                // Backfill a blank logo; never clobber an admin-set one.
                school.LogoUrl = logo;
                await db.SaveChangesAsync(ct);
            }
            return school;
        }

        public static async Task<SchoolUser> EnsureSchoolUserAsync(SchulyDbContext db, School school, Guid userId, ProvisionedUser p, CancellationToken ct = default)
        {
            var user = await db.SchoolUsers.FirstOrDefaultAsync(su => su.ApplicationUserId == userId && su.SchoolId == school.Id, ct);
            if (user is null)
            {
                user = new SchoolUser
                {
                    ApplicationUserId = userId,
                    SchoolId = school.Id,
                    FirstName = p.FirstName ?? "",
                    LastName = p.LastName ?? "",
                    Email = p.Email ?? "",
                    Birthday = p.Birthday ?? DateOnly.FromDateTime(DateTime.UtcNow),
                    EntryDate = p.EntryDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
                    Role = Roles.Student,
                    State = UserState.Active,
                };
                db.SchoolUsers.Add(user);
            }
            ApplyBlanks(user, p);
            await db.SaveChangesAsync(ct);
            return user;
        }

        // Fill blanks only - never clobber a manual edit or an earlier-synced value.
        // The pre-resolved profile picture is the exception (the caller decides it).
        private static void ApplyBlanks(SchoolUser u, ProvisionedUser p)
        {
            if (string.IsNullOrWhiteSpace(u.FirstName) && p.FirstName is not null) u.FirstName = p.FirstName;
            if (string.IsNullOrWhiteSpace(u.LastName) && p.LastName is not null) u.LastName = p.LastName;
            if (string.IsNullOrWhiteSpace(u.Email) && p.Email is not null) u.Email = p.Email;
            u.PrivateEmail ??= p.PrivateEmail;
            u.PhoneNumber ??= p.PhoneNumber;
            u.Street ??= p.Street;
            u.City ??= p.City;
            u.Zip ??= p.Zip;
            u.LeaveDate ??= p.LeaveDate;
            if (p.ProfilePictureUrl is not null) u.ProfilePictureUrl = p.ProfilePictureUrl;
        }
    }
}
