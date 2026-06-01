using Microsoft.Extensions.DependencyInjection;

namespace Agentry;

/// <summary>DI helpers for the Anthropic provider.</summary>
public static class AnthropicServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="AnthropicChatModel"/> as the <see cref="IChatModel"/> for the agent loop.
    /// Uses <c>IHttpClientFactory</c> for the underlying <see cref="HttpClient"/>.
    /// Throws if <see cref="AnthropicOptions.ApiKey"/> is not set.
    /// </summary>
    public static IServiceCollection AddAnthropicChatModel(this IServiceCollection services, Action<AnthropicOptions> configure)
    {
        var options = new AnthropicOptions();
        configure(options);

        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new ArgumentException("AnthropicOptions.ApiKey is required (e.g. set it from the ANTHROPIC_API_KEY environment variable).", nameof(configure));

        services.AddSingleton(options);
        services.AddHttpClient<IChatModel, AnthropicChatModel>();
        return services;
    }
}
