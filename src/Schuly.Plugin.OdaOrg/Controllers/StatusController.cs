using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Schuly.Plugin.OdaOrg.Controllers
{
    [ApiController]
    [Route("api/plugins/odaorg/status")]
    public class StatusController : ControllerBase
    {
        [HttpGet]
        [AllowAnonymous]
        public IActionResult Get() => Ok(new
        {
            Status = "Active",
            Plugin = OdaOrgPlugin.PluginName,
            Version = OdaOrgPlugin.PluginVersion,
        });
    }
}
