using System.ComponentModel;
using Agentry;

namespace FileQaAgent;

/// <summary>Per-run state: the folder the agent is allowed to read. Threaded through every tool.</summary>
public sealed class Workspace
{
    public required string Root { get; init; }

    /// <summary>Resolve a user/model-supplied relative path safely under <see cref="Root"/> (no traversal).</summary>
    public string? Resolve(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        var rel = Path.GetRelativePath(Root, full);
        var escapes = rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(rel);
        return escapes ? null : full;
    }
}

public sealed class ListFilesTool : Tool<ListFilesTool.Input, Workspace>
{
    public override string Name => "list_files";
    public override string Description => "List files in the workspace. Optionally filter by a glob like '*.md' or '*.cs'.";

    protected override Task<ToolResult> ExecuteAsync(Input input, Workspace ws, ToolCall call, CancellationToken ct)
    {
        var pattern = string.IsNullOrWhiteSpace(input.Pattern) ? "*" : input.Pattern!;
        var files = Directory.EnumerateFiles(ws.Root, pattern, SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(ws.Root, f))
            .Take(200)
            .ToList();
        var body = files.Count == 0 ? "(no matching files)" : string.Join("\n", files);
        return Task.FromResult(ToolResult.Ok(call, body, new { count = files.Count }));
    }

    public sealed class Input
    {
        [Description("Optional glob pattern, e.g. *.md or *.cs. Omit to list everything.")]
        public string? Pattern { get; set; }
    }
}

public sealed class SearchFilesTool : Tool<SearchFilesTool.Input, Workspace>
{
    public override string Name => "search_files";
    public override string Description => "Search file contents in the workspace for a text query (case-insensitive). Returns matching files with line snippets.";

    protected override Task<ToolResult> ExecuteAsync(Input input, Workspace ws, ToolCall call, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Query))
            return Task.FromResult(ToolResult.Fail(call, "query is required"));

        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(ws.Root, "*", SearchOption.AllDirectories))
        {
            if (hits.Count >= 40) break;
            string[] lines;
            try { lines = File.ReadAllLines(file); } catch { continue; }
            for (var i = 0; i < lines.Length && hits.Count < 40; i++)
                if (lines[i].Contains(input.Query, StringComparison.OrdinalIgnoreCase))
                    hits.Add($"{Path.GetRelativePath(ws.Root, file)}:{i + 1}: {lines[i].Trim()}");
        }

        var body = hits.Count == 0 ? $"No matches for '{input.Query}'." : string.Join("\n", hits);
        return Task.FromResult(ToolResult.Ok(call, body, new { matches = hits.Count }));
    }

    public sealed class Input
    {
        [Description("The text to search for (case-insensitive)")]
        public string Query { get; set; } = "";
    }
}

public sealed class ReadFileTool : Tool<ReadFileTool.Input, Workspace>
{
    public override string Name => "read_file";
    public override string Description => "Read a file from the workspace by its relative path (e.g. about.md).";

    protected override async Task<ToolResult> ExecuteAsync(Input input, Workspace ws, ToolCall call, CancellationToken ct)
    {
        var path = ws.Resolve(input.Path);
        if (path is null || !File.Exists(path))
            return ToolResult.Fail(call, $"File not found in workspace: '{input.Path}'");

        var text = await File.ReadAllTextAsync(path, ct);
        if (text.Length > 8000) text = text[..8000] + "\n... (truncated)";
        return ToolResult.Ok(call, text, new { path = input.Path });
    }

    public sealed class Input
    {
        [Description("Relative path of the file to read, e.g. about.md")]
        public string Path { get; set; } = "";
    }
}
