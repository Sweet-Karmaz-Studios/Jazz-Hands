using JazzHands.Engine.Logging;
using JazzHands.Mcp;
using Serilog;

// stdout carries the MCP protocol, so nothing else may write to it: logging goes to file only.
LogSetup.ConfigureForMcp();
JazzHands.Engine.Effects.EffectCatalog.LoadUserTransitions();

try
{
    if (args.Contains("--list-tools", StringComparer.Ordinal))
    {
        await Console.Out.WriteAsync(McpHost.ListTools(args.Contains("--json", StringComparer.Ordinal))).ConfigureAwait(false);
        return 0;
    }

    McpHostOptions options = McpCommandLine.Parse(args);
    Log.ForContext("SourceContext", "mcp").Information("jazz-mcp starting, {Mode}", options.Attach ? "attached" : "headless");
    await using EditorLink link = await McpHost.LinkAsync(options).ConfigureAwait(false);
    await McpHost.RunAsync(link).ConfigureAwait(false);
    return 0;
}
catch (Exception error) when (error is JazzHands.Control.AttachException or JazzHands.Core.Commands.CommandException or ArgumentException)
{
    await Console.Error.WriteLineAsync($"jazz-mcp: {error.Message}").ConfigureAwait(false);
    return 4;
}
finally
{
    LogSetup.Shutdown();
}
