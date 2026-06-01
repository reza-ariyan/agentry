using System.ComponentModel;
using Agentry;
using Microsoft.EntityFrameworkCore;

namespace NewsAgent;

/// <summary>Per-run state: a factory for the news database. Threaded through every tool.</summary>
public sealed class NewsAgentContext
{
    public required Func<NewsDbContext> NewDb { get; init; }
}

public sealed class CreateNewsTool : Tool<CreateNewsTool.Input, NewsAgentContext>
{
    public override string Name => "create_news";
    public override string Description => "Insert a new news article. Returns its id.";

    protected override async Task<ToolResult> ExecuteAsync(Input i, NewsAgentContext ctx, ToolCall call, CancellationToken ct)
    {
        await using var db = ctx.NewDb();
        var n = new News
        {
            Title = i.Title,
            Body = i.Body,
            Category = string.IsNullOrWhiteSpace(i.Category) ? "general" : i.Category!,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        db.News.Add(n);
        await db.SaveChangesAsync(ct);
        return ToolResult.Ok(call, $"Created news #{n.Id}: {n.Title}", new { id = n.Id });
    }

    public sealed class Input
    {
        [Description("Headline / title")] public string Title { get; set; } = "";
        [Description("Article body text")] public string Body { get; set; } = "";
        [Description("Optional category, e.g. release, engineering")] public string? Category { get; set; }
    }
}

public sealed class ListNewsTool : Tool<ListNewsTool.Input, NewsAgentContext>
{
    public override string Name => "list_news";
    public override string Description => "List news articles (id, category, title). Optional title filter.";

    protected override async Task<ToolResult> ExecuteAsync(Input i, NewsAgentContext ctx, ToolCall call, CancellationToken ct)
    {
        await using var db = ctx.NewDb();
        IQueryable<News> q = db.News;
        if (!string.IsNullOrWhiteSpace(i.Query))
            q = q.Where(n => n.Title.Contains(i.Query!));

        var items = await q.OrderBy(n => n.Id)
            .Take(50)
            .Select(n => new { n.Id, n.Category, n.Title })
            .ToListAsync(ct);

        var body = items.Count == 0
            ? "(no matching news)"
            : string.Join("\n", items.Select(x => $"#{x.Id} [{x.Category}] {x.Title}"));
        return ToolResult.Ok(call, body, new { count = items.Count });
    }

    public sealed class Input
    {
        [Description("Optional text to filter titles by")] public string? Query { get; set; }
    }
}

public sealed class GetNewsTool : Tool<GetNewsTool.Input, NewsAgentContext>
{
    public override string Name => "get_news";
    public override string Description => "Get a full news article by id, including its current SEO fields.";

    protected override async Task<ToolResult> ExecuteAsync(Input i, NewsAgentContext ctx, ToolCall call, CancellationToken ct)
    {
        await using var db = ctx.NewDb();
        var n = await db.News.FindAsync([(object)i.Id], ct);
        if (n is null) return ToolResult.Fail(call, $"No news with id {i.Id}.");

        var body =
            $"#{n.Id} {n.Title}\n" +
            $"Category: {n.Category}\n" +
            $"Slug: {n.Slug ?? "(none)"}\n" +
            $"MetaTitle: {n.MetaTitle ?? "(none)"}\n" +
            $"MetaDescription: {n.MetaDescription ?? "(none)"}\n" +
            $"Keywords: {n.Keywords ?? "(none)"}\n\n" +
            n.Body;
        return ToolResult.Ok(call, body, new { n.Id });
    }

    public sealed class Input
    {
        [Description("Article id")] public int Id { get; set; }
    }
}

public sealed class UpdateNewsTool : Tool<UpdateNewsTool.Input, NewsAgentContext>
{
    public override string Name => "update_news";
    public override string Description => "Update an article's title, body, or category by id. Only provided fields change.";

    protected override async Task<ToolResult> ExecuteAsync(Input i, NewsAgentContext ctx, ToolCall call, CancellationToken ct)
    {
        await using var db = ctx.NewDb();
        var n = await db.News.FindAsync([(object)i.Id], ct);
        if (n is null) return ToolResult.Fail(call, $"No news with id {i.Id}.");

        if (i.Title is not null) n.Title = i.Title;
        if (i.Body is not null) n.Body = i.Body;
        if (i.Category is not null) n.Category = i.Category;
        n.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToolResult.Ok(call, $"Updated news #{n.Id}.", new { n.Id });
    }

    public sealed class Input
    {
        [Description("Article id")] public int Id { get; set; }
        [Description("New title (optional)")] public string? Title { get; set; }
        [Description("New body (optional)")] public string? Body { get; set; }
        [Description("New category (optional)")] public string? Category { get; set; }
    }
}

public sealed class SetSeoTool : Tool<SetSeoTool.Input, NewsAgentContext>
{
    public override string Name => "set_seo";
    public override string Description =>
        "Save SEO metadata for an article. YOU generate the values: a meta title (<=60 chars), " +
        "a meta description (<=160 chars), a URL slug (lowercase, hyphenated), and comma-separated keywords.";

    protected override async Task<ToolResult> ExecuteAsync(Input i, NewsAgentContext ctx, ToolCall call, CancellationToken ct)
    {
        await using var db = ctx.NewDb();
        var n = await db.News.FindAsync([(object)i.Id], ct);
        if (n is null) return ToolResult.Fail(call, $"No news with id {i.Id}.");

        n.MetaTitle = i.MetaTitle;
        n.MetaDescription = i.MetaDescription;
        n.Slug = i.Slug;
        n.Keywords = i.Keywords;
        n.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToolResult.Ok(call, $"SEO updated for #{n.Id} (slug: {n.Slug}).", new { n.Id });
    }

    public sealed class Input
    {
        [Description("Article id")] public int Id { get; set; }
        [Description("Meta title, <= 60 characters")] public string MetaTitle { get; set; } = "";
        [Description("Meta description, <= 160 characters")] public string MetaDescription { get; set; } = "";
        [Description("URL slug, lowercase and hyphenated")] public string Slug { get; set; } = "";
        [Description("Comma-separated keywords")] public string Keywords { get; set; } = "";
    }
}

public sealed class DeleteNewsTool : Tool<DeleteNewsTool.Input, NewsAgentContext>
{
    public override string Name => "delete_news";
    public override string Description => "Delete a news article by id.";

    protected override async Task<ToolResult> ExecuteAsync(Input i, NewsAgentContext ctx, ToolCall call, CancellationToken ct)
    {
        await using var db = ctx.NewDb();
        var n = await db.News.FindAsync([(object)i.Id], ct);
        if (n is null) return ToolResult.Fail(call, $"No news with id {i.Id}.");

        db.News.Remove(n);
        await db.SaveChangesAsync(ct);
        return ToolResult.Ok(call, $"Deleted news #{i.Id}.", new { id = i.Id });
    }

    public sealed class Input
    {
        [Description("Article id")] public int Id { get; set; }
    }
}
