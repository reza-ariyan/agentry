# Architecture notes

## The loop

The model is called with the conversation so far plus the available tool definitions.
If the model requests tool calls, the executor runs each tool and feeds the results
back as a new turn. This repeats until the model ends the turn (or a max-iteration
limit is hit). Transient provider errors are retried with exponential backoff.

## Persistence and resume

Persistence is pluggable behind `IConversationStore`. An in-memory store ships by
default; you can bring your own (EF Core, Redis, MongoDB, ...). Because messages are
appended to the store as they happen, a fresh runner with the same run id resumes the
conversation — useful after a process restart.

## Providers

The engine talks to an `IChatModel`, never a vendor SDK directly. An Anthropic adapter
is included; any other provider is one adapter away.
