// FileQaAgent — a real, runnable Agentry app.
// An interactive agent that answers questions about a folder of files, using a real
// LLM (Anthropic) and real filesystem tools. You just set config (API key + optional
// model/folder) and chat with it.

using Agentry;
using FileQaAgent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// ── Config: appsettings.json + environment variables ────────────────────────
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.WriteLine("This sample uses a real model. Set ANTHROPIC_API_KEY and run again:");
    Console.WriteLine("  ANTHROPIC_API_KEY=sk-ant-... dotnet run --project samples/FileQaAgent");
    return;
}

var model = config["Agent:Model"] ?? "claude-haiku-4-5";
var folder = Path.GetFullPath(config["Agent:Folder"] ?? "workspace", AppContext.BaseDirectory);
if (!Directory.Exists(folder))
{
    Console.WriteLine($"Workspace folder not found: {folder}");
    return;
}

// ── Wire up Agentry: tools + a model provider ───────────────────────────────
var services = new ServiceCollection();
services.AddAgentry<Workspace>(a => a
    .AddTool<ListFilesTool>()
    .AddTool<SearchFilesTool>()
    .AddTool<ReadFileTool>());
services.AddAnthropicChatModel(o => { o.ApiKey = apiKey; o.Model = model; });

using var provider = services.BuildServiceProvider();
var runner = provider.GetRequiredService<IAgentRunner<Workspace>>();

var workspace = new Workspace { Root = folder };
var runId = "file-qa-" + Guid.NewGuid().ToString("n")[..8];   // one conversation for this session

const string system =
    "You answer questions about the files in the user's workspace. " +
    "Use list_files, search_files and read_file to ground every answer in the actual files — " +
    "never guess. Cite the file names you used. If something isn't in the files, say so.";

Console.WriteLine($"File Q&A agent  ·  workspace: {folder}");
Console.WriteLine($"model: {model}   —   ask a question, or type 'exit' to quit.\n");

// ── Interactive loop ─────────────────────────────────────────────────────────
while (true)
{
    Console.Write("you> ");
    var question = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(question)) continue;
    if (question.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase)) break;

    await foreach (var e in runner.RunAsync(
        new AgentRequest { RunId = runId, System = system, Prompt = question, Model = model }, workspace))
    {
        switch (e)
        {
            case AgentEvent.ToolStarted t:  Console.WriteLine($"   …{t.ToolName}"); break;
            case AgentEvent.AssistantText a: Console.WriteLine($"\nagent> {a.Text}\n"); break;
            case AgentEvent.Failed f:        Console.WriteLine($"   ! {f.Error}"); break;
        }
    }
}
