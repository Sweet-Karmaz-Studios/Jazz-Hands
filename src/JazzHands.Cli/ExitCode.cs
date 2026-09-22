namespace JazzHands.Cli;

/// <summary>
/// The exit codes jazz returns. Scripts and Claude Code branch on these, so they are part of the
/// public contract: see Docs/CLI.md.
/// </summary>
public static class ExitCode
{
    /// <summary>The command succeeded.</summary>
    public const int Ok = 0;

    /// <summary>The command ran but failed: validation, missing id, nothing to do.</summary>
    public const int CommandError = 1;

    /// <summary>The command line itself was wrong: unknown verb, bad option, missing argument.</summary>
    public const int UsageError = 2;

    /// <summary>Media or FFmpeg failed: unreadable file, unsupported codec, encoder error.</summary>
    public const int MediaError = 3;

    /// <summary>--attach was requested but no running instance answered.</summary>
    public const int AttachFailed = 4;

    /// <summary>The user cancelled with Ctrl+C.</summary>
    public const int Cancelled = 130;
}
