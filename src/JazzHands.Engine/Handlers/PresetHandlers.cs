using System.Text.Json;
using System.Text.Json.Nodes;
using JazzHands.Core.Commands;
using JazzHands.Core.Export;
using JazzHands.Core.Model;
using JazzHands.Engine.Commands;
using JazzHands.Engine.Export;

namespace JazzHands.Engine.Handlers;

/// <summary>Lists the export presets, built in and a person's own.</summary>
public sealed class ListPresetsHandler : IQueryHandler<ListPresetsQuery, ExportPresetSummary[]>
{
    /// <inheritdoc />
    public ExportPresetSummary[] Handle(Project project, ListPresetsQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Category is { } category && !ExportPresets.Categories.Contains(category))
        {
            throw new CommandException("invalid-value", $"'{category}' is not a preset category. They are {string.Join(", ", ExportPresets.Categories)}.");
        }

        return [.. ExportPresetLibrary.All
            .Where(preset => query.Category is null || preset.Category == query.Category)
            .Select(ExportPresets.Summarise)];
    }
}

/// <summary>An export preset in full.</summary>
public sealed class GetPresetHandler : IQueryHandler<GetPresetQuery, ExportPreset>
{
    /// <inheritdoc />
    public ExportPreset Handle(Project project, GetPresetQuery query, QueryContext context)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ExportPresetLibrary.Require(query.Name);
    }
}

/// <summary>Saves an export preset of a person's own.</summary>
public sealed class SavePresetHandler : ICommandHandler<SavePresetCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, SavePresetCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        string name = command.Name.Trim();
        ExportPreset preset;

        if (command.Json is { } json && !string.IsNullOrWhiteSpace(json))
        {
            if (command.From is not null)
            {
                throw new CommandException("conflicting-options", "Give a whole preset with --json, or start from another with --from, not both.");
            }

            string text = json.TrimStart().StartsWith('{') ? json : File.ReadAllText(Resolve(json, context.ProjectPath));

            // The name given is the preset's name, whatever the JSON says, so a file can be saved
            // under another and one without a name at all is fine.
            if (JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is not JsonObject body)
            {
                throw new CommandException("invalid-preset", "An export preset is a JSON object.");
            }

            body["name"] = name;
            if (!ExportPresets.TryRead(body.ToJsonString(), out ExportPreset? read, out string? error))
            {
                throw new CommandException("invalid-preset", error!);
            }

            preset = read!;
        }
        else
        {
            ExportPreset basis = ExportPresetLibrary.Require(command.From);
            preset = ExportOverrideText.Apply(basis, command.ToOverrides()) with
            {
                Name = name,
                Label = string.Empty,
                Description = $"Based on {basis.Name}.",
            };
        }

        preset = preset with
        {
            Label = command.Label ?? preset.Label,
            Description = command.Description ?? preset.Description,
            BuiltIn = false,
            Source = string.Empty,
        };

        ExportPresetLibrary.Save(preset);
        context.Changed(preset.Name);
        return project;
    }

    private static string Resolve(string path, string projectPath) =>
        Path.IsPathRooted(path) || projectPath.Length == 0
            ? Path.GetFullPath(path)
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? ".", path);
}

/// <summary>Deletes one of a person's export presets.</summary>
public sealed class DeletePresetHandler : ICommandHandler<DeletePresetCommand>
{
    /// <inheritdoc />
    public Project Handle(Project project, DeletePresetCommand command, HandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        ExportPresetLibrary.Delete(command.Name);
        context.Changed(command.Name.Trim());
        return project;
    }
}
