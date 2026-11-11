using Microsoft.Extensions.Configuration;

namespace Schuly.Plugin.Demo.Services
{
    /// <summary>The Schuly accounts the operator chose to fill with demo data. Nobody else ever gets any.</summary>
    public static class DemoAccounts
    {
        public const string Key = "Demo:UserIds";

        /// <summary>Reads <see cref="Key"/> as a YAML list or a comma separated string. Anything that is not a user id is ignored.</summary>
        public static IReadOnlyList<Guid> UserIds(IConfiguration configuration)
        {
            var section = configuration.GetSection(Key);
            var values = section.GetChildren().Select(c => c.Value).ToList();
            if (values.Count == 0 && section.Value is { } single)
                values = single.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).Select(v => (string?)v).ToList();

            return values
                .Select(v => Guid.TryParse(v?.Trim(), out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();
        }
    }
}
