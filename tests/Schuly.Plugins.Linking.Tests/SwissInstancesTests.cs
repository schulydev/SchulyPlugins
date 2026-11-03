using Microsoft.Extensions.Configuration;
using Schuly.Plugin.Schulware.Services;
using Schuly.Plugin.Shared;
using Xunit;

namespace Schuly.Plugins.Linking.Tests
{
    public class SwissInstancesTests
    {
        private static IConfiguration Config(string? value) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["SignUp:SwitzerlandOnly"] = value }).Build();

        [Theory]
        [InlineData("https://schulnetz.bbzf.ch", true)]
        [InlineData("https://SCHULNETZ.LU.CH/bbzb", true)]
        [InlineData("https://www.schul-netz.com/stans", true)]
        [InlineData("https://odaorg.ict-bbag.ch", true)]
        [InlineData("https://schulnetz.example.de", false)]
        [InlineData("https://schulnetz.example.at", false)]
        [InlineData("https://schul-netz.com.evil.example", false)]
        [InlineData("https://evil-ch", false)]
        [InlineData("https://example.com/.ch", false)]
        [InlineData("not a url", false)]
        public void Only_swiss_hosts_count(string url, bool swiss) => Assert.Equal(swiss, SwissInstances.IsSwiss(url));

        [Fact]
        public void Every_school_on_the_built_in_list_passes()
        {
            Assert.NotEmpty(SchulnetzSchools.All);
            Assert.All(SchulnetzSchools.All, school => Assert.True(SwissInstances.IsSwiss(school.Value), school.Value));
        }

        [Fact]
        public void The_limit_only_applies_with_the_setting_on()
        {
            Assert.True(SwissInstances.Allows(null, "https://schulnetz.example.de"));
            Assert.True(SwissInstances.Allows(Config(null), "https://schulnetz.example.de"));
            Assert.True(SwissInstances.Allows(Config("false"), "https://schulnetz.example.de"));
            Assert.False(SwissInstances.Allows(Config("true"), "https://schulnetz.example.de"));
            Assert.False(SwissInstances.Allows(Config("True"), "https://schulnetz.example.de"));
            Assert.True(SwissInstances.Allows(Config("true"), "https://schulnetz.example.ch"));
        }
    }
}
