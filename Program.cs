using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Transformers;
using LLamaSharp.SemanticKernel;
using LLamaSharp.SemanticKernel.ChatCompletion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Spectre.Console;
using ChatHistory = Microsoft.SemanticKernel.ChatCompletion.ChatHistory;
using BoxOfYellow.ConsoleMarkdownRenderer.Spectre;

namespace RunLLM;

internal class Program
{
    private const int ExitSuccess = 0;
    private const int ExitFailure = 0;

    private const string DirectoryName = "models";
    private async static Task Main(string[] args)
    {
        using var tokenSource = new CancellationTokenSource();
        Action<PosixSignalContext> quit = context =>
                {
                    Console.WriteLine();
                    context.Cancel = true;
                    tokenSource.Cancel();
                };

        // Register hooks for both Ctrl+C (SIGINT) and termination signals (SIGTERM)
        using var _1 = PosixSignalRegistration.Create(PosixSignal.SIGINT, quit);
        using var _2 = PosixSignalRegistration.Create(PosixSignal.SIGTERM, quit);

        Welcome();

        var token = tokenSource.Token;
        var modelSettings = ConfigureModel();
        var llmFilePath = await SetUpAsync(modelSettings, token);
        await StartChatAsync(llmFilePath, modelSettings.Name, token);

        Environment.Exit(ExitSuccess);
    }

    private static async Task StartChatAsync(string modelFilePath, string modelName, CancellationToken token)
    {
        NativeLibraryConfig.All.WithLogCallback((level, message) =>
        {
            if (level == LLamaLogLevel.Error)
                Console.Error.Write(message);
        });

        AnsiConsole.MarkupLine($"[green]Loading LLM into memory.[/]");

        const int contextSize = 32768;
        var modelParams = new ModelParams(modelFilePath)
        {
            ContextSize = contextSize,
            GpuLayerCount = 999, // offload everything to Metal on Apple Silicon
        };

        using var weights = await LLamaWeights.LoadFromFileAsync(modelParams, token);
        if (token.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine($"[red]Loading cancelled.[/]");
            Environment.Exit(ExitSuccess);
            return;
        }

        var executor = new StatelessExecutor(weights, modelParams);
        var executionSettings = new LLamaSharpPromptExecutionSettings
        {
            MaxTokens = 1024,       // hard cap on reply length, in tokens
            Temperature = 0.1,      // higher = more random/creative, lower = more deterministic
        };
        var promptTransformer = new PromptTemplateTransformer(weights, withAssistant: true);

        var chatCompletionService = new LLamaSharpChatCompletion(
            executor,
            executionSettings,
            promptTransformer);

        var kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.Services.AddSingleton<Microsoft.SemanticKernel.ChatCompletion.IChatCompletionService>(chatCompletionService);
        var kernel = kernelBuilder.Build();

        var history = new ChatHistory();
        history.AddSystemMessage("You are a helpful assistant with IT-related topics with software development in particular. You answer in a short, technical, concise manner to address the user's questions.");

        var markdownRenderer = new MarkdownRenderer();
        var renderOptions = new SpectreDisplayOptions
        {
            CodeBlock = new Style(Color.GreenYellow),
            CodeInLine = new Style(Color.DarkGreen),
            Bold = new Style(Color.Green, decoration: Decoration.Bold),
            Italic = new Style(Color.Green, decoration: Decoration.Italic),
        };

        while (!token.IsCancellationRequested)
        {
            var prompt = await AskOrCancelAsync("[greenyellow]You:[/]", token);
            if (!prompt.Item1)
            {
                break;
            }

            history.AddUserMessage(prompt.Item2);

            var responseBuilder = new StringBuilder();

            IReadOnlyList<ChatMessageContent> messages = null;

            try
            {
                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .SpinnerStyle(Style.Parse("greenyellow"))
                    .StartAsync("[green]Processing input...[/]", async ctx =>
                    {
                        messages = await chatCompletionService.GetChatMessageContentsAsync(history, executionSettings, kernel, token);
                    });
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var buffer = new StringBuilder();
            foreach (var msg in messages)
            {
                if (!string.IsNullOrWhiteSpace(msg.Content))
                {
                    buffer.Append(msg.Content);
                }
            }

            const string close = "</think>";
            var result = buffer.ToString();
            var end = result.LastIndexOf(close, StringComparison.OrdinalIgnoreCase);
            if (end >= 0)
            {
                //cut off before </think>
                result = result[(end + close.Length)..];
            }

            var md = markdownRenderer.Render(result, renderOptions);

            AnsiConsole.Markup($"[greenyellow]{Markup.Escape(modelName)}: [/]");
            AnsiConsole.Write(md.Root ?? Text.Empty);
        }
    }

    static async Task<(bool, string)> AskOrCancelAsync(string prompt, CancellationToken token)
    {
        try
        {
            var output = await AnsiConsole.AskAsync<string>(prompt, token);
            return (true, output);
        }
        catch (OperationCanceledException)
        {
            return (false, null);
        }
    }

    private static async Task DownloadLlmModelAsync(string url, string targetFilePath, CancellationToken token)
    {
        var tempFilePath = $"{targetFilePath}-tmp";
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        var currentBytes = File.Exists(tempFilePath) ? new FileInfo(tempFilePath).Length : 0L;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (currentBytes > 0L)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(currentBytes, null);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var isPartialContent = response.StatusCode == HttpStatusCode.PartialContent;
        if (currentBytes > 0L && !isPartialContent)
        {
            currentBytes = 0L;
        }

        var totalBytes = response.Content.Headers.ContentRange?.Length
            ?? (isPartialContent ? null : response.Content.Headers.ContentLength) ?? 0L;

        const int oneMegaByte = 1 << 20;
        await using (var httpStream = await response.Content.ReadAsStreamAsync())
        await using (var fileStream = new FileStream(
            tempFilePath,
            isPartialContent ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            oneMegaByte,
            true))
        {
            var buffer = new byte[oneMegaByte];

            try
            {
                await AnsiConsole.Progress()
                    .Columns(
                        new TaskDescriptionColumn(), new ProgressBarColumn(),
                        new DownloadedColumn(), new TransferSpeedColumn())
                   .StartAsync(async ctx =>
                   {
                       var downloadTask = ctx.AddTask(new FileInfo(targetFilePath).Name, maxValue: totalBytes);
                       downloadTask.Increment(currentBytes);

                       int read;
                       while ((read = await httpStream.ReadAsync(buffer, token)) > 0)
                       {
                           await fileStream.WriteAsync(buffer.AsMemory(0, read));
                           downloadTask.Increment(read);
                       }
                   });
            }
            catch (OperationCanceledException) { }
        }

        if (token.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine($"[red]File download cancelled.[/]");
            Environment.Exit(ExitFailure);
            return;
        }

        File.Move(tempFilePath, targetFilePath);
        AnsiConsole.MarkupLine($"[green]File {Markup.Escape(targetFilePath)} downloaded.[/]");
        AnsiConsole.WriteLine();
    }

    private static async Task<string> SetUpAsync(ModelSettings modelSettings, CancellationToken token)
    {
        var currentDirectory = AppContext.BaseDirectory;
        var modelDirectoryPath = Path.Combine(currentDirectory, DirectoryName);
        Directory.CreateDirectory(modelDirectoryPath);

        var fi = new FileInfo(modelSettings.Url);
        var targetFilePath = Path.Combine(modelDirectoryPath, fi.Name);

        if (File.Exists(targetFilePath))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[green]File already present at {Markup.Escape(targetFilePath)}, skipping download.[/]");
        }
        else
        {
            var url = modelSettings.Url;
            if (url.Contains("huggingface", StringComparison.InvariantCultureIgnoreCase))
            {
                url = $"{url}?download=true";
            }

            await DownloadLlmModelAsync(url, targetFilePath, token);
        }

        return targetFilePath;
    }

    private static void Welcome()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;

        var panel = new Panel(Align.Center(new Markup($"LLM Runner v{version}")));
        panel.Expand = true;
        panel.AsciiBorder();
        panel.BorderColor(Color.GreenYellow);
        AnsiConsole.Write(panel);
    }

    private static ModelSettings ConfigureModel()
    {
        var config = new ConfigurationBuilder()
          .SetBasePath(AppContext.BaseDirectory)
          .AddJsonFile("llm.json", optional: false)
          .Build();

        var modelSettings = config.GetSection("Model").Get<ModelSettings>();
        if (modelSettings is null || modelSettings.Url is null || modelSettings.Name is null)
        {
            AnsiConsole.MarkupLine($"[red] Model settings are not configured.[/]");
            Environment.Exit(ExitFailure);
        }

        AnsiConsole.MarkupLine($"[green]{Markup.Escape(modelSettings.Name)} name configured.[/]");
        AnsiConsole.MarkupLine($"[green]{Markup.Escape(modelSettings.Url)} url configured.[/]");

        return modelSettings;
    }
}
