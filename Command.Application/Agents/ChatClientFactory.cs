using System.ClientModel;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OpenAgentOrchestrator.Command.Domain.Model.Configuration;

namespace OpenAgentOrchestrator.Command.Application.Agents
{
    /// <summary>
    /// Creates IChatClient instances based on provider configuration. Only the azure-openai
    /// provider type is supported.
    /// </summary>
    public sealed class ChatClientFactory : IChatClientFactory
    {
        private static readonly HashSet<string> SupportedTypes = ["azure-openai"];

        /// <summary>
        /// Default network timeout applied to SDK chat clients. The SDK's own default (100s)
        /// is too short for tool-heavy agents whose individual completion calls can legitimately
        /// take longer.
        /// </summary>
        private static readonly TimeSpan DefaultNetworkTimeout = TimeSpan.FromMinutes(10);

        public IChatClient Create(ProviderDefinition provider, string model)
        {
            if (!SupportedTypes.Contains(provider.Type))
                throw new InvalidOperationException(
                    $"Unsupported provider type '{provider.Type}'. Supported: {string.Join(", ", SupportedTypes)}.");

            return provider.Type switch
            {
                "azure-openai" => CreateAzureOpenAI(provider, model),
                _ => throw new InvalidOperationException($"Unknown provider type: '{provider.Type}'.")
            };
        }

        private static IChatClient CreateAzureOpenAI(ProviderDefinition provider, string model)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(provider.Endpoint);
            ArgumentException.ThrowIfNullOrWhiteSpace(provider.ApiKey);

            var options = new AzureOpenAIClientOptions { NetworkTimeout = DefaultNetworkTimeout };
            var client = new AzureOpenAIClient(new Uri(provider.Endpoint), new ApiKeyCredential(provider.ApiKey), options);
            return client.GetChatClient(model).AsIChatClient();
        }
    }
}
