using System.Collections.Immutable;
using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using JazzHands.Core.Model;
using JazzHands.Core.Serialization;
using JazzHands.Core.Time;
using JazzHands.Core.Validation;

namespace JazzHands.Cli;

/// <summary>
/// The verbs that make, check and tidy a project file.
/// </summary>
/// <remarks>
/// These five are the ones that exist before commands do. Everything else in the CLI is generated
/// from the command registry in Phase 24, but a project has to be created and validated before
/// there is anything to run a command against, and Claude Code needs <c>jazz validate</c> from
/// the moment it can edit a .jazz file by hand.
/// </remarks>
public static class ProjectCommands
{
    /// <summary>Adds the project verbs to the root command.</summary>
    public static void AddTo(RootCommand root)
    {
        ArgumentNullException.ThrowIfNull(root);

        root.Subcommands.Add(BuildNew());
        root.Subcommands.Add(BuildValidate());
        root.Subcommands.Add(BuildFormat());
        root.Subcommands.Add(BuildRepair());
        root.Subcommands.Add(BuildIds());
    }

    private static Command BuildNew()
    {
        var file = new Argument<string>("project") { Description = "Where to write the new .jazz file." };
        var fps = new Option<string>("--fps")
        {
            Description = "Frame rate: 30, 60, 23.976, or an exact ratio such as 30000/1001.",
            DefaultValueFactory = _ => "30",
        };
        var size = new Option<string>("--size")
        {
            Description = "Frame size: 1920x1080, 1080p, 4k, 720p.",
            DefaultValueFactory = _ => "1920x1080",
        };
        var name = new Option<string?>("--name")
        {
            Description = "The project name. Defaults to the file name.",
        };
        var force = new Option<bool>("--force")
        {
            Description = "Overwrite an existing project file.",
        };

        var command = new Command("new", "Create an empty project with one video and one audio track.")
        {
            file, fps, size, name, force,
        };

        command.SetAction(parse =>
        {
            string path = Path.GetFullPath(EnsureExtension(parse.GetValue(file)!));

            if (File.Exists(path) && !parse.GetValue(force))
            {
                Console.Error.WriteLine($"jazz: '{path}' already exists. Pass --force to overwrite it.");
                return ExitCode.CommandError;
            }

            if (!TryParseFrameRate(parse.GetValue(fps)!, out Rational frameRate, out string? rateError))
            {
                Console.Error.WriteLine($"jazz: {rateError}");
                return ExitCode.UsageError;
            }

            if (!TryParseSize(parse.GetValue(size)!, out int width, out int height, out string? sizeError))
            {
                Console.Error.WriteLine($"jazz: {sizeError}");
                return ExitCode.UsageError;
            }

            string projectName = parse.GetValue(name) ?? Path.GetFileNameWithoutExtension(path);
            Project project = Project.CreateNew(projectName, new ProjectSettings(frameRate, width, height));
            ProjectFile.Save(path, project);

            if (parse.GetValue(JazzCli.JsonOption))
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(
                    new { path, id = project.Id, name = projectName, fps = frameRate.ToString(), size = $"{width}x{height}" },
                    JsonText));
            }
            else
            {
                Console.Out.WriteLine($"Created {path}");
                Console.Out.WriteLine($"  {width}x{height} at {frameRate} fps, one video track and one audio track.");
            }

            return ExitCode.Ok;
        });

        return command;
    }

    private static Command BuildValidate()
    {
        var file = new Argument<string>("project") { Description = "The .jazz file to check." };
        var strict = new Option<bool>("--strict")
        {
            Description = "Treat warnings as failures.",
        };

        var command = new Command("validate", "Check a project against the schema and the semantic rules.")
        {
            file, strict,
        };

        // The only verb that asks for schema validation. It is the one place where taking a
        // fifth of a second to check every member is exactly what was wanted.
        command.SetAction(parse => WithProject(parse, parse.GetValue(file)!, checkSchema: true, (load, json) =>
        {
            bool strictly = parse.GetValue(strict);
            ImmutableArray<ValidationIssue> issues = [.. load.Issues, .. JazzHands.Engine.Titles.TitleFonts.Missing(load.Project, load.Path)];

            if (json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(
                    new
                    {
                        path = load.Path,
                        loadable = load.IsLoadable,
                        errors = issues.Count(issue => issue.Severity == Severity.Error),
                        warnings = issues.Count(issue => issue.Severity == Severity.Warning),
                        issues = issues.Select(issue => new
                        {
                            severity = issue.Severity.ToString().ToLowerInvariant(),
                            code = issue.Code,
                            path = issue.Path,
                            message = issue.Message,
                        }),
                    },
                    JsonText));
            }
            else if (issues.IsEmpty)
            {
                Console.Out.WriteLine($"{load.Path}: no problems found.");
            }
            else
            {
                foreach (ValidationIssue issue in issues)
                {
                    TextWriter writer = issue.Severity == Severity.Error ? Console.Error : Console.Out;
                    writer.WriteLine($"{issue.Path}: {issue.Severity.ToString().ToLowerInvariant()}: {issue.Code}: {issue.Message}");
                }
            }

            bool failed = !load.IsLoadable || (strictly && !issues.IsEmpty);
            return failed ? ExitCode.CommandError : ExitCode.Ok;
        }));

        return command;
    }

    private static Command BuildFormat()
    {
        var file = new Argument<string>("project") { Description = "The .jazz file to rewrite." };
        var check = new Option<bool>("--check")
        {
            Description = "Report whether the file is already canonical instead of rewriting it.",
        };

        var command = new Command("fmt", "Rewrite a project into canonical form: ordering, indentation, defaults.")
        {
            file, check,
        };

        command.SetAction(parse => WithProject(parse, parse.GetValue(file)!, (load, json) =>
        {
            string current = File.ReadAllText(load.Path);
            string canonical = ProjectFile.Render(ProjectFile.WithStoredPaths(load.Path, load.Project), load.Unknown);
            bool changed = !string.Equals(current, canonical, StringComparison.Ordinal);

            if (parse.GetValue(check))
            {
                if (json)
                {
                    Console.Out.WriteLine(JsonSerializer.Serialize(new { path = load.Path, canonical = !changed }, JsonText));
                }
                else
                {
                    Console.Out.WriteLine(changed
                        ? $"{load.Path} is not canonical. Run 'jazz fmt' without --check to rewrite it."
                        : $"{load.Path} is already canonical.");
                }

                return changed ? ExitCode.CommandError : ExitCode.Ok;
            }

            if (changed)
            {
                ProjectFile.WriteAtomic(load.Path, canonical);
            }

            if (json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new { path = load.Path, rewritten = changed }, JsonText));
            }
            else
            {
                Console.Out.WriteLine(changed ? $"Rewrote {load.Path}." : $"{load.Path} was already canonical.");
                if (!load.Unknown.IsEmpty)
                {
                    Console.Out.WriteLine($"  Kept {load.Unknown.Count} member(s) this build does not know.");
                }
            }

            return ExitCode.Ok;
        }));

        return command;
    }

    private static Command BuildRepair()
    {
        var file = new Argument<string>("project") { Description = "The .jazz file to repair." };
        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "Report what would change without writing anything.",
        };

        var command = new Command("repair", "Fix the problems with one obvious answer: dangling references, zero speeds, fades that overrun.")
        {
            file, dryRun,
        };

        command.SetAction(parse => WithProject(parse, parse.GetValue(file)!, (load, json) =>
        {
            RepairResult result = ProjectRepair.Apply(load.Project);
            bool dry = parse.GetValue(dryRun);

            if (!result.IsClean && !dry)
            {
                ProjectFile.Save(load.Path, result.Project, load.Unknown);
            }

            if (json)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(
                    new
                    {
                        path = load.Path,
                        written = !result.IsClean && !dry,
                        actions = result.Actions.Select(action => new { code = action.Code, path = action.Path, message = action.Message }),
                        remaining = result.Remaining.Select(issue => new { code = issue.Code, path = issue.Path, message = issue.Message }),
                    },
                    JsonText));
            }
            else if (result.IsClean)
            {
                Console.Out.WriteLine($"{load.Path}: nothing to repair.");
            }
            else
            {
                foreach (RepairAction action in result.Actions)
                {
                    Console.Out.WriteLine(action.ToString());
                }

                Console.Out.WriteLine(dry
                    ? $"{result.Actions.Length} change(s) would be made. Run without --dry-run to write them."
                    : $"Wrote {load.Path} with {result.Actions.Length} change(s).");

                foreach (ValidationIssue issue in result.Remaining.Where(issue => issue.Severity == Severity.Error))
                {
                    Console.Error.WriteLine($"still broken: {issue}");
                }
            }

            return result.Remaining.Any(issue => issue.Severity == Severity.Error)
                ? ExitCode.CommandError
                : ExitCode.Ok;
        }));

        return command;
    }

    private static Command BuildIds()
    {
        var count = new Option<int>("--count", "-n")
        {
            Description = "How many identifiers to print.",
            DefaultValueFactory = _ => 1,
        };

        var newIds = new Command("new", "Print fresh ULIDs for hand-editing a project file.") { count };

        newIds.SetAction(parse =>
        {
            int howMany = Math.Max(1, parse.GetValue(count));
            string[] ids = [.. Enumerable.Range(0, howMany).Select(_ => Id.New())];

            Console.Out.WriteLine(parse.GetValue(JazzCli.JsonOption)
                ? JsonSerializer.Serialize(new { ids }, JsonText)
                : string.Join(Environment.NewLine, ids));

            return ExitCode.Ok;
        });

        var command = new Command("ids", "Identifiers.") { newIds };
        return command;
    }

    /// <summary>
    /// Opens a project and hands it to the body, turning the ways that can fail into exit codes.
    /// </summary>
    /// <remarks>
    /// A file that will not load at all is an exit code and a sentence on stderr, never a stack
    /// trace: the CLI is something Claude Code reads the output of.
    /// </remarks>
    private static int WithProject(
        System.CommandLine.ParseResult parse,
        string path,
        Func<ProjectLoad, bool, int> body) =>
        WithProject(parse, path, checkSchema: false, body);

    private static int WithProject(
        System.CommandLine.ParseResult parse,
        string path,
        bool checkSchema,
        Func<ProjectLoad, bool, int> body)
    {
        bool json = parse.GetValue(JazzCli.JsonOption);

        try
        {
            return body(ProjectFile.Load(path, checkSchema), json);
        }
        catch (ProjectFileException error)
        {
            // A file that will not load at all still has something useful to say when the schema
            // found the member that broke it.
            if (!error.Issues.IsEmpty)
            {
                ReportIssues(path, error.Issues, json);
                return ExitCode.CommandError;
            }

            Report(error.Message, json);
            return ExitCode.CommandError;
        }
        catch (IOException error)
        {
            Report(error.Message, json);
            return ExitCode.CommandError;
        }
        catch (UnauthorizedAccessException error)
        {
            Report(error.Message, json);
            return ExitCode.CommandError;
        }
    }

    private static void ReportIssues(string path, ImmutableArray<ValidationIssue> issues, bool json)
    {
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(
                new
                {
                    path,
                    loadable = false,
                    errors = issues.Length,
                    warnings = 0,
                    issues = issues.Select(issue => new
                    {
                        severity = issue.Severity.ToString().ToLowerInvariant(),
                        code = issue.Code,
                        path = issue.Path,
                        message = issue.Message,
                    }),
                },
                JsonText));
            return;
        }

        foreach (ValidationIssue issue in issues)
        {
            Console.Error.WriteLine($"{issue.Path}: error: {issue.Code}: {issue.Message}");
        }

        Console.Error.WriteLine($"jazz: '{path}' will not load until those are fixed.");
    }

    private static void Report(string message, bool json)
    {
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new { error = message }, JsonText));
        }
        else
        {
            Console.Error.WriteLine($"jazz: {message}");
        }
    }

    private static string EnsureExtension(string path) =>
        path.EndsWith(ProjectPaths.Extension, StringComparison.OrdinalIgnoreCase) ? path : path + ProjectPaths.Extension;

    /// <summary>
    /// Parses a frame rate, refusing the decimal spellings of the broadcast rates.
    /// </summary>
    /// <remarks>
    /// 29.97 is not a frame rate. Accepting it would store 2997/100, which drifts by a frame
    /// every thousand frames against real 30000/1001 footage, and nobody would find out until an
    /// export went out of sync. The common decimal shorthands are recognised and turned into the
    /// exact ratio they mean; anything else with a decimal point is refused with the ratio to
    /// type instead.
    /// </remarks>
    internal static bool TryParseFrameRate(string text, out Rational rate, out string? error)
    {
        rate = default;
        error = null;

        string trimmed = text.Trim();

        switch (trimmed)
        {
            case "23.976" or "23.98":
                rate = Rational.Fps23976;
                return true;
            case "29.97":
                rate = Rational.Fps2997;
                return true;
            case "59.94":
                rate = Rational.Fps5994;
                return true;
            case "119.88":
                rate = Rational.Fps11988;
                return true;
        }

        if (trimmed.Contains('.', StringComparison.Ordinal))
        {
            error = $"'{trimmed}' is not an exact frame rate. Write it as a ratio, for example 30000/1001.";
            return false;
        }

        if (!Rational.TryParse(trimmed, out rate) || rate.Num <= 0)
        {
            error = $"'{trimmed}' is not a frame rate. Try 30, 60, or 30000/1001.";
            return false;
        }

        return true;
    }

    /// <summary>Parses a frame size, accepting the named shorthands.</summary>
    internal static bool TryParseSize(string text, out int width, out int height, out string? error)
    {
        width = 0;
        height = 0;
        error = null;

        switch (text.Trim().ToLowerInvariant())
        {
            case "720p":
                (width, height) = (1280, 720);
                return true;
            case "1080p" or "fhd":
                (width, height) = (1920, 1080);
                return true;
            case "1440p" or "2k":
                (width, height) = (2560, 1440);
                return true;
            case "4k" or "2160p" or "uhd":
                (width, height) = (3840, 2160);
                return true;
            case "8k" or "4320p":
                (width, height) = (7680, 4320);
                return true;
        }

        string[] parts = text.Trim().ToLowerInvariant().Split('x');
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out width)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out height)
            && width >= 16
            && height >= 16)
        {
            return true;
        }

        error = $"'{text}' is not a frame size. Try 1920x1080, 1080p or 4k.";
        return false;
    }

    private static readonly JsonSerializerOptions JsonText = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
    };
}
