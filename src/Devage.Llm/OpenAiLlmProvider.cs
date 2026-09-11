using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace Devage.Llm;

public sealed class OpenAiLlmProvider : ILlmProvider
{
    public const string Id = "openai";

    private readonly OpenAiOptions _options;
    private readonly Lazy<IChatClient> _chatClient;

    public OpenAiLlmProvider(IOptions<OpenAiOptions> options)
    {
        _options = options.Value;
        ProviderId = Id;
        _chatClient = new Lazy<IChatClient>(CreateClient);
    }

    public string ProviderId { get; }
    public IChatClient ChatClient => _chatClient.Value;

    private IChatClient CreateClient()
    {
        var apiKey = Environment.GetEnvironmentVariable("DEVAGE_OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = _options.ApiKey;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "OpenAI API key missing. Set DEVAGE_OPENAI_API_KEY or OpenAI:ApiKey in appsettings.");
        }

        OpenAIClient client = string.IsNullOrWhiteSpace(_options.Endpoint)
            ? new OpenAIClient(apiKey)
            : new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions
            {
                Endpoint = new Uri(_options.Endpoint)
            });

        ChatClient chatClient = client.GetChatClient(_options.Model);
        return chatClient.AsIChatClient();
    }
}
