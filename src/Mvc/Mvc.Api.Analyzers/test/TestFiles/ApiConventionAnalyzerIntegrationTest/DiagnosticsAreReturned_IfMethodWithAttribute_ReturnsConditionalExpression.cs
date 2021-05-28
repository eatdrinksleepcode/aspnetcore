namespace Microsoft.AspNetCore.Mvc.Api.Analyzers
{
    [ApiController]
    public class DiagnosticsAreReturned_IfMethodWithAttribute_ReturnsConditionalExpression : ControllerBase
    {
        [ProducesResponseType(typeof(string), 200)]
        public IActionResult Method(int id)
        {
            return id == 0 ? /*MM*/NotFound() : Ok();
        }
    }
}
