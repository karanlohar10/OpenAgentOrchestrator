using Microsoft.AspNetCore.Http;

namespace OpenAgentOrchestrator.Service.Models
{
    /// <summary>
    /// ASP.NET Core model-binding shape used only by Swashbuckle to document the
    /// multipart/form-data alternative for the Execute endpoint (see
    /// <see cref="Swagger.ExecuteRequestOperationFilter"/>). The action itself parses the form
    /// manually, so this type is never bound directly.
    /// </summary>
    public sealed class ExecuteFormRequest
    {
        public string? Input { get; set; }
        public IFormFile? File { get; set; }
        public Dictionary<string, string>? Context { get; set; }
        public string? SessionId { get; set; }
    }
}
