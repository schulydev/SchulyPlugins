using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Services;
using Xunit;

namespace Schuly.Plugins.Linking.Tests
{
    public class SchulwareConnectorTests
    {
        [Fact]
        public void A_login_with_only_the_web_session_still_connects()
        {
            Assert.True(SchulwareConnector.Connected(new LoginResponseDto { Success = true, SessionId = "php" }));
            Assert.True(SchulwareConnector.Connected(new LoginResponseDto { Success = true, AccessToken = "token", SessionId = "php" }));
        }

        [Fact]
        public void A_failed_or_empty_login_does_not_connect()
        {
            Assert.False(SchulwareConnector.Connected(null));
            Assert.False(SchulwareConnector.Connected(new LoginResponseDto { Success = false, SessionId = "php" }));
            Assert.False(SchulwareConnector.Connected(new LoginResponseDto { Success = true }));
        }
    }
}
