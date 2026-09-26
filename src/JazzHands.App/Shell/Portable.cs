using System.IO;

namespace JazzHands.App.Shell;

/// <summary>
/// The portable zip (Phase 34): a <c>portable.txt</c> beside JazzHands.exe says so, and the editor
/// then leaves Windows alone: no .jazz association, no Explorer verbs, no jazzhands: links, no
/// start with Windows, no jump list. Settings and the cache still live in the usual per-person
/// folders. The installer is for all of those.
/// </summary>
public static class Portable
{
    /// <summary>The file that marks a portable copy.</summary>
    public const string Marker = "portable.txt";

    /// <summary>True when this copy is the portable one.</summary>
    public static bool IsOn { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, Marker));

    /// <summary>True when this process may register itself with Windows: installed, and not an isolated run.</summary>
    public static bool MayRegister => !IsOn && !Core.JazzFolders.IsIsolated;
}
