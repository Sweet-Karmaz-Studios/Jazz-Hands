using JazzHands.Engine.Logging;
using Serilog;

// stdout carries the MCP protocol, so nothing else may write to it: logging goes to file only.
LogSetup.ConfigureForMcp();

try
{
    Log.ForContext("SourceContext", "mcp").Information("jazz-mcp starting");

    // Phase 26 builds the real server: every CommandRegistry entry becomes a tool, plus
    // render_frame, describe_timeline, probe_media and the jazz:// resources.
    await Console.Error.WriteLineAsync("jazz-mcp: the MCP server is implemented in Phase 26.").ConfigureAwait(false);
    return 0;
}
finally
{
    LogSetup.Shutdown();
}
