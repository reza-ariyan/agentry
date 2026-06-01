using System.Collections.Concurrent;

namespace Agentry;

/// <summary>
/// In-memory <see cref="IConversationStore"/> — the zero-config default. Swap for an EF Core / Redis /
/// Mongo implementation in production by registering your own <see cref="IConversationStore"/>.
/// </summary>
public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly ConcurrentDictionary<string, List<AgentMessage>> _conversations = new();

    /// <inheritdoc />
    public Task AppendAsync(string conversationId, IEnumerable<AgentMessage> messages, CancellationToken cancellationToken = default)
    {
        var list = _conversations.GetOrAdd(conversationId, _ => []);
        lock (list)
        {
            list.AddRange(messages);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentMessage>> LoadAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        if (!_conversations.TryGetValue(conversationId, out var list))
            return Task.FromResult<IReadOnlyList<AgentMessage>>([]);

        lock (list)
        {
            return Task.FromResult<IReadOnlyList<AgentMessage>>(list.ToList());
        }
    }
}
