using OpenAgentOrchestrator.Command.Application.Tools.WebSearch;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Command.Application.ToolBinding
{
    /// <summary>
    /// Binds the custom web-search tool ("web-search" tool type), backed by a real third-party
    /// search-provider REST API. Delegates the actual construction to
    /// <see cref="IWebSearchToolFactory"/>.
    /// </summary>
    public sealed class WebSearchToolBinder : IToolBinder
    {
        private readonly IWebSearchToolFactory _webSearchToolFactory;

        public WebSearchToolBinder(IWebSearchToolFactory webSearchToolFactory)
        {
            _webSearchToolFactory = webSearchToolFactory;
        }

        public string SupportedType => "web-search";

        public Task<ToolBindingResult> BindAsync(ToolDefinition definition, CancellationToken cancellationToken = default)
        {
            var tool = _webSearchToolFactory.Create(definition);
            return Task.FromResult(new ToolBindingResult([tool]));
        }
    }
}
