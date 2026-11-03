using System.Reflection;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.OdaOrg.Models;
using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Dtos;
using Schuly.Plugin.Schulware.Services;
using Xunit;

namespace Schuly.Plugins.Linking.Tests
{
    public class ProfileMinimizationTests
    {
        private static readonly string[] Dropped = ["Gender", "Birthday", "PrivateEmail", "PhoneNumber", "Street", "City", "Zip"];

        [Fact]
        public void Schulnetz_provisioning_passes_on_no_private_contact_data()
        {
            var info = new UserInfoDto { FirstName = "Mia", LastName = "Muster", Email = "mia@school.ch", EmailPrivate = "mia@private.ch", Phone = "+41", Mobile = "+41", Street = "Weg 1", City = "Bern", Zip = "3000", Birthday = "2008-01-01" };

            var record = (StudentRecord)typeof(SchoolProvisioningService).GetMethod("Map", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [info])!;

            Assert.Equal("Mia", record.FirstName);
            Assert.Equal("mia@school.ch", record.Email);
            Assert.Null(record.PrivateEmail);
            Assert.Null(record.PhoneNumber);
            Assert.Null(record.Street);
            Assert.Null(record.City);
            Assert.Null(record.Zip);
            Assert.Null(record.Birthday);
        }

        [Fact]
        public void Oda_org_scraping_keeps_no_private_contact_fields()
        {
            Assert.Empty(typeof(OdaProfile).GetProperties().Select(p => p.Name).Intersect(Dropped));
        }

        [Fact]
        public void Private_mode_gets_no_birthday()
        {
            Assert.Empty(typeof(StatelessUserInfoDto).GetProperties().Select(p => p.Name).Intersect(Dropped));
            Assert.Empty(typeof(Schuly.Plugin.OdaOrg.Controllers.OdaorgStatelessUserInfo).GetProperties().Select(p => p.Name).Intersect(Dropped));
        }
    }
}
