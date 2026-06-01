namespace Agentry;

/// <summary>
/// Pluggable conversation persistence. The agent loop appends new messages as they happen and
/// loads prior history to resume a run after a process restart. Ship an in-memory default;
/// bring your own (EF Core, Mongo, Redis, ...) by implementing this interface.
/// </summary>
public interface IConversationStore
{
    /// <summary>Append new messages to a conversation, in order.</summary>
    Task AppendAsync(string conversationId, IEnumerable<AgentMessage> messages, CancellationToken cancellationToken = default);

    /// <summary>Load the full message history for a conversation, in order. Empty if unknown.</summary>
    Task<IReadOnlyList<AgentMessage>> LoadAsync(string conversationId, CancellationToken cancellationToken = default);
}
