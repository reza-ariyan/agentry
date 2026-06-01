using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentry;

/// <summary>DI entry point for Agentry.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the Agentry engine for a given per-run state type <typeparamref name="TContext"/>.
    /// Add tools via the returned builder, and register an <see cref="IChatModel"/>
    /// (e.g. <c>services.AddAnthropicChatModel(...)</c>).
    /// </summary>
    public static AgentryBuilder<TContext> AddAgentry<TContext>(
        this IServiceCollection services, Action<AgentryBuilder<TContext>>? configure = null)
    {
        services.TryAddSingleton<IConversationStore, InMemoryConversationStore>();
        services.TryAddSingleton<IToolExecutor<TContext>, ToolExecutor<TContext>>();
        services.TryAddSingleton<IAgentRunner<TContext>, AgentRunner<TContext>>();

        var builder = new AgentryBuilder<TContext>(services);
        configure?.Invoke(builder);
        return builder;
    }
}

/// <summary>Fluent registration of tools and options for a <typeparamref name="TContext"/> agent.</summary>
public sealed class AgentryBuilder<TContext>(IServiceCollection services)
{
    /// <summary>The underlying service collection.</summary>
    public IServiceCollection Services { get; } = services;

    /// <summary>Register a tool type (resolved from DI, so it can take constructor dependencies).</summary>
    public AgentryBuilder<TContext> AddTool<TTool>() where TTool : class, ITool<TContext>
    {
        Services.AddSingleton<ITool<TContext>, TTool>();
        return this;
    }

    /// <summary>Register a pre-built tool instance.</summary>
    public AgentryBuilder<TContext> AddTool(ITool<TContext> tool)
    {
        // Register under ITool<TContext> so the executor (which resolves IEnumerable<ITool<TContext>>) sees it.
        Services.AddSingleton<ITool<TContext>>(tool);
        return this;
    }

    /// <summary>Configure loop options (retries, backoff).</summary>
    public AgentryBuilder<TContext> Configure(Action<AgentryOptions> configure)
    {
        var options = new AgentryOptions();
        configure(options);
        Services.AddSingleton(options);
        return this;
    }
}
