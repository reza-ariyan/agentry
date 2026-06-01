// NewsAgent — a real, runnable Agentry app backed by a database.
// An interactive agent that manages a news database: create, list, get, update,
// improve SEO, and delete articles — using a real LLM (Anthropic) + EF Core SQLite.
// You just set config (API key + optional model/db path) and chat with it.

using Agentry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NewsAgent;

// ── Config ──────────────────────────────────────────────────────────────────
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var dbPath = Path.GetFullPath(config["Agent:DbPath"] ?? "newsagent.db", AppContext.BaseDirectory);
var dbOptions = new DbContextOptionsBuilder<NewsDbContext>().UseSqlite($"Data Source={dbPath}").Options;
Func<NewsDbContext> newDb = () => new NewsDbContext(dbOptions);

// ── Create + seed the database (runs regardless of API key) ─────────────────
await using (var db = newDb())
{
    await db.Database.EnsureCreatedAsync();
    if (!await db.News.AnyAsync())
    {
        db.News.AddRange(
            new News { Title = "Agentry reaches v0.1", Category = "release", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
                       Body = "A lightweight, provider-agnostic agentic tool-use loop for .NET is now open source under MIT." },
            new News { Title = "Lessons from migrating a hand-rolled agent", Category = "engineering", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
                       Body = "What changed when we moved an 800-line custom agent loop onto a maintained framework." });
        await db.SaveChangesAsync();
    }
}
Console.WriteLine($"News database ready: {dbPath}");

var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.WriteLine("\nSet ANTHROPIC_API_KEY to chat with the agent:");
    Console.WriteLine("  ANTHROPIC_API_KEY=sk-ant-... dotnet run --project samples/NewsAgent");
    return;
}

// ── Wire up Agentry: the news tools + a model provider ──────────────────────
var model = config["Agent:Model"] ?? "claude-haiku-4-5";
var services = new ServiceCollection();
services.AddAgentry<NewsAgentContext>(a => a
    .AddTool<CreateNewsTool>()
    .AddTool<ListNewsTool>()
    .AddTool<GetNewsTool>()
    .AddTool<UpdateNewsTool>()
    .AddTool<SetSeoTool>()
    .AddTool<DeleteNewsTool>());
services.AddAnthropicChatModel(o => { o.ApiKey = apiKey; o.Model = model; });

using var provider = services.BuildServiceProvider();
var runner = provider.GetRequiredService<IAgentRunner<NewsAgentContext>>();

var ctx = new NewsAgentContext { NewDb = newDb };
var runId = "news-" + Guid.NewGuid().ToString("n")[..8];

const string system =
    "You manage a news database through tools: create_news, list_news, get_news, update_news, set_seo, delete_news. " +
    "To improve an article's SEO: call get_news first, then write a meta title (<=60 chars), a meta description " +
    "(<=160 chars), a lowercase hyphenated slug, and comma-separated keywords, and save them with set_seo. " +
    "Always confirm what changed and cite the article ids you touched.";

Console.WriteLine($"\nNews agent (model: {model}). Try:");
Console.WriteLine("  • list the news");
Console.WriteLine("  • add a news article titled \"...\" about ...");
Console.WriteLine("  • improve the SEO of article 1");
Console.WriteLine("  • update article 2's category to \"product\"");
Console.WriteLine("  • delete article 2");
Console.WriteLine("  (type 'exit' to quit)\n");

// ── Interactive loop ─────────────────────────────────────────────────────────
while (true)
{
    Console.Write("you> ");
    var input = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(input)) continue;
    if (input.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase)) break;

    await foreach (var e in runner.RunAsync(
        new AgentRequest { RunId = runId, System = system, Prompt = input, Model = model }, ctx))
    {
        switch (e)
        {
            case AgentEvent.ToolStarted t:   Console.WriteLine($"   …{t.ToolName}"); break;
            case AgentEvent.AssistantText a: Console.WriteLine($"\nagent> {a.Text}\n"); break;
            case AgentEvent.Failed f:        Console.WriteLine($"   ! {f.Error}"); break;
        }
    }
}
