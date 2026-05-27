using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Schuly.Plugin.Schulware.Controllers
{
    [ApiController]
    [Route("api/plugins/schulware/status")]
    public class StatusController : ControllerBase
    {
        [HttpGet]
        [AllowAnonymous]
        public IActionResult Get() => Ok(new
        {
            Status = "Active",
            Plugin = SchulwarePlugin.PluginName,
            Version = SchulwarePlugin.PluginVersion,
        });
    }
}
