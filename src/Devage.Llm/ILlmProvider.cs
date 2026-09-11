using Microsoft.Extensions.AI;

namespace Devage.Llm;

public interface ILlmProvider
{
    string ProviderId { get; }
    IChatClient ChatClient { get; }
}

public interface ILlmRouter
{
    ILlmProvider GetProvider(string? providerId = null);
    IReadOnlyList<string> RegisteredProviders { get; }
}
