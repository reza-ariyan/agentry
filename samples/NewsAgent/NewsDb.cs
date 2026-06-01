using Microsoft.EntityFrameworkCore;

namespace NewsAgent;

/// <summary>A news article row, including SEO metadata the agent can improve.</summary>
public sealed class News
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Category { get; set; } = "general";

    // SEO fields — written by the agent via the set_seo tool.
    public string? Slug { get; set; }
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
    public string? Keywords { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class NewsDbContext(DbContextOptions<NewsDbContext> options) : DbContext(options)
{
    public DbSet<News> News => Set<News>();
}
