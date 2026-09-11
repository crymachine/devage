namespace Devage.Llm;

public sealed class LlmRouter : ILlmRouter
{
    private readonly Dictionary<string, ILlmProvider> _providers;
    private readonly string _defaultProviderId;

    public LlmRouter(IEnumerable<ILlmProvider> providers, string defaultProviderId = OpenAiLlmProvider.Id)
    {
        _providers = providers.ToDictionary(p => p.ProviderId, StringComparer.OrdinalIgnoreCase);
        if (_providers.Count == 0)
        {
            throw new InvalidOperationException("No LLM providers registered.");
        }

        _defaultProviderId = _providers.ContainsKey(defaultProviderId)
            ? defaultProviderId
            : _providers.Keys.First();
    }

    public IReadOnlyList<string> RegisteredProviders => _providers.Keys.OrderBy(x => x).ToList();

    public ILlmProvider GetProvider(string? providerId = null)
    {
        var key = string.IsNullOrWhiteSpace(providerId) ? _defaultProviderId : providerId;
        if (!_providers.TryGetValue(key, out var provider))
        {
            throw new KeyNotFoundException(
                $"LLM provider '{key}' is not registered. Available: {string.Join(", ", RegisteredProviders)}. " +
                "Slots reserved for future: anthropic, azure, ollama.");
        }

        return provider;
    }
}
