namespace Microsoft.AspNetCore.Mvc.Api.Analyzers
{
    [ApiController]
    public class NoDiagnosticsAreReturned_ForApiController_IfStatusCodesCannotBeInferred : ControllerBase
    {
        [ProducesResponseType(201)]
        public IActionResult Method()
        {
            return MethodImpl();
        }

        private IActionResult MethodImpl()
        {
            return Ok();
        }
    }
}
