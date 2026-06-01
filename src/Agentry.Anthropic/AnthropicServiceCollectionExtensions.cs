using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentry;

/// <summary>DI helpers for the Anthropic provider.</summary>
public static class AnthropicServiceCollectionExtensions
{
    /// <summary>Register <see cref="AnthropicChatModel"/> as the <see cref="IChatModel"/> for the agent loop.</summary>
    public static IServiceCollection AddAnthropicChatModel(this IServiceCollection services, Action<AnthropicOptions> configure)
    {
        var options = new AnthropicOptions();
        configure(options);
        services.AddSingleton(options);
        services.TryAddSingleton<IChatModel>(_ => new AnthropicChatModel(options));
        return services;
    }
}
