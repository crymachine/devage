using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Devage.Llm;

public static class LlmServiceCollectionExtensions
{
    /// <summary>
    /// Registers OpenAI now; additional providers (anthropic/azure/ollama) plug in as new ILlmProvider implementations.
    /// </summary>
    public static IServiceCollection AddDevageLlm(this IServiceCollection services, Action<OpenAiOptions>? configure = null)
    {
        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            services.AddOptions<OpenAiOptions>();
        }

        services.AddSingleton<ILlmProvider, OpenAiLlmProvider>();
        services.AddSingleton<ILlmRouter>(sp =>
        {
            var providers = sp.GetServices<ILlmProvider>();
            return new LlmRouter(providers);
        });

        return services;
    }
}
