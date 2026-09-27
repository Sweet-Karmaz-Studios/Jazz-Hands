using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.Core.Commands;
using JazzHands.Core.Model;

namespace JazzHands.App.ViewModels.Grading;

/// <summary>A node drawn in the Colour panel's node view, or the picture coming into the graph.</summary>
public sealed partial class NodeViewModel : ObservableObject
{
    /// <summary>A node's width in the view.</summary>
    public const double Width = 68;

    /// <summary>A node's height in the view.</summary>
    public const double Height = 26;

    /// <summary>Creates a node.</summary>
    public NodeViewModel(string id, string label, string typeId, double x, double y, bool isOutput, bool isEnabled, bool isKeyed)
    {
        Id = id;
        Label = label;
        TypeId = typeId;
        X = x;
        Y = y;
        IsOutput = isOutput;
        IsEnabled = isEnabled;
        IsKeyed = isKeyed;
    }

    /// <summary>The node's id; empty for the picture coming in.</summary>
    public string Id { get; }

    /// <summary>What it shows: its number and what it does.</summary>
    public string Label { get; }

    /// <summary>Its effect type.</summary>
    public string TypeId { get; }

    /// <summary>Its left edge.</summary>
    public double X { get; }

    /// <summary>Its top edge.</summary>
    public double Y { get; }

    /// <summary>True for the picture coming into the graph, which reads nothing.</summary>
    public bool IsInput => Id.Length == 0;

    /// <summary>True for a parallel mixer.</summary>
    public bool IsMix => TypeId == GradeGraph.MixType;

    /// <summary>True for the node the graph shows.</summary>
    public bool IsOutput { get; }

    /// <summary>False for a node switched off.</summary>
    public bool IsEnabled { get; }

    /// <summary>True for a node limited by a qualifier's key.</summary>
    public bool IsKeyed { get; }

    /// <summary>Where a connection into it arrives.</summary>
    public Point In => new(X, Y + (Height / 2));

    /// <summary>Where a connection out of it leaves.</summary>
    public Point Out => new(X + Width, Y + (Height / 2));

    /// <summary>True for the node the wheels and curves are driving.</summary>
    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>A connection drawn between two nodes.</summary>
/// <param name="From">The node read; empty for the picture coming in.</param>
/// <param name="To">The node reading it.</param>
/// <param name="Start">Where it leaves.</param>
/// <param name="End">Where it arrives.</param>
/// <param name="IsKey">True for a qualifier's key rather than a picture.</param>
public sealed record NodeLink(string From, string To, Point Start, Point End, bool IsKey);

/// <summary>A kind of node the panel can add.</summary>
/// <param name="Type">The node type for <c>color.node-add</c>.</param>
/// <param name="Label">Its name in the menu.</param>
public sealed record NodeChoice(string Type, string Label);

/// <summary>
/// The Colour panel's node view (Phase 44): the clip's colour graph drawn small, left to right,
/// the picture coming in on the left. Click a node to drive it with the wheels and curves; drag
/// from a node's right edge onto another to connect it (with Alt, as its key). Everything goes
/// through the <c>color.node-*</c> commands.
/// </summary>
public sealed partial class ColorPanelViewModel
{
    private const double ColumnWidth = 88;
    private const double RowHeight = 36;
    private const double Margin = 6;

    private string? _selectedNodeId;

    /// <summary>The graph's nodes, the picture coming in first.</summary>
    public ObservableCollection<NodeViewModel> Nodes { get; } = [];

    /// <summary>The connections between them.</summary>
    public ObservableCollection<NodeLink> Links { get; } = [];

    /// <summary>The kinds of node the panel adds.</summary>
    public IReadOnlyList<NodeChoice> NodeChoices { get; } =
    [
        new("wheels", "Colour wheels"),
        new("curves", "Curves"),
        new("hsl", "HSL qualifier"),
        new("lut", "LUT"),
        new("white-balance", "White balance"),
    ];

    /// <summary>The clip's colour graph, or null when it grades with separate effects.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGraph))]
    private string? _graphId;

    /// <summary>How wide the node view needs to be.</summary>
    [ObservableProperty]
    private double _nodesWidth;

    /// <summary>How tall the node view needs to be.</summary>
    [ObservableProperty]
    private double _nodesHeight;

    /// <summary>True when the clip has a colour graph to show.</summary>
    public bool HasGraph => GraphId is not null;

    /// <summary>The node the wheels and curves drive, or null.</summary>
    public string? SelectedNodeId => _selectedNodeId;

    /// <summary>Makes a node the one the wheels and curves drive.</summary>
    [RelayCommand]
    private void SelectNode(NodeViewModel? node)
    {
        if (node is null || node.IsInput)
        {
            return;
        }

        _selectedNodeId = node.Id;
        Reload();
    }

    /// <summary>Grades the clip with nodes: its Colour wheels become the first, or a first is added.</summary>
    [RelayCommand]
    private Task UseNodes()
    {
        if (_clipId is not { } clipId)
        {
            return Task.CompletedTask;
        }

        if (WheelsId is { } wheels)
        {
            _selectedNodeId = wheels;
            return RunAsync(new ConvertToColorGraphCommand(wheels));
        }

        string id = Id.New();
        _selectedNodeId = id;
        return RunAsync(new AddColorNodeCommand(clipId, "wheels", NodeId: id));
    }

    /// <summary>Adds a node after the selected one, which it then is.</summary>
    [RelayCommand]
    private Task AddNode(NodeChoice? choice) => AddNodeAsync(choice?.Type ?? "wheels", parallel: false);

    /// <summary>Adds a node beside the selected one, the two mixed.</summary>
    [RelayCommand]
    private Task AddParallelNode(NodeChoice? choice) => AddNodeAsync(choice?.Type ?? "wheels", parallel: true);

    /// <summary>Removes the selected node; what read it reads what it read.</summary>
    [RelayCommand]
    private Task RemoveNode()
    {
        if (_selectedNodeId is not { } id)
        {
            return Task.CompletedTask;
        }

        _selectedNodeId = null;
        return RunAsync(new RemoveColorNodeCommand(id));
    }

    /// <summary>Switches the selected node off, or back on.</summary>
    [RelayCommand]
    private Task ToggleNode()
    {
        NodeViewModel? node = Nodes.FirstOrDefault(item => item.Id == _selectedNodeId);
        return node is null ? Task.CompletedTask : RunAsync(new SetColorNodeCommand(node.Id, Enabled: !node.IsEnabled));
    }

    /// <summary>Makes the selected node the one the graph shows.</summary>
    [RelayCommand]
    private Task ShowNode() =>
        _selectedNodeId is { } id ? RunAsync(new SetColorNodeCommand(id, Output: true)) : Task.CompletedTask;

    /// <summary>
    /// A drag from one node's right edge let go over another: the second reads the first from
    /// now on, or is keyed by it. Empty <paramref name="from"/> is the picture coming in.
    /// </summary>
    public Task Connect(string from, string to, bool asKey)
    {
        if (to.Length == 0 || from == to)
        {
            return Task.CompletedTask;
        }

        return RunAsync(new ConnectColorNodeCommand(to, from.Length == 0 ? null : from, Key: asKey));
    }

    private Task AddNodeAsync(string type, bool parallel)
    {
        if (GraphId is not { } graph)
        {
            return Task.CompletedTask;
        }

        string id = Id.New();
        string? beside = _selectedNodeId;
        if (parallel && Nodes.FirstOrDefault(node => node.Id == beside) is not { IsMix: false })
        {
            Status = "Select a node to put the new one beside.";
            return Task.CompletedTask;
        }

        _selectedNodeId = id;
        return RunAsync(parallel
            ? new AddColorNodeCommand(graph, type, ParallelTo: beside, NodeId: id)
            : new AddColorNodeCommand(graph, type, After: beside, NodeId: id));
    }

    private async Task RunAsync(ICommand command)
    {
        CommandResult result = await _session.ExecuteAsync(command).ConfigureAwait(true);
        _ui.Post(() =>
        {
            Status = result.Ok ? string.Empty : result.Error ?? result.Code ?? "That did not work.";
            Reload();
        });
    }

    /// <summary>
    /// Lays the graph out, left to right: each node one column right of the furthest thing it
    /// reads, in the order they were added down each column. Returns the node the wheels drive.
    /// </summary>
    private GradeNode? LoadNodes(Effect? holder)
    {
        GraphId = holder?.Id;
        Nodes.Clear();
        Links.Clear();
        if (holder?.Graph is not { } graph)
        {
            _selectedNodeId = null;
            NodesWidth = NodesHeight = 0;
            return null;
        }

        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        int Depth(GradeNode node, int guard)
        {
            if (depth.TryGetValue(node.Id, out int known))
            {
                return known;
            }

            int deepest = 0;
            if (guard < graph.Nodes.Length)
            {
                foreach (string id in node.Key is { } key ? [.. node.Inputs, key] : node.Inputs.AsEnumerable())
                {
                    if (graph.Node(id) is { } input)
                    {
                        deepest = Math.Max(deepest, Depth(input, guard + 1));
                    }
                }
            }

            return depth[node.Id] = deepest + 1;
        }

        if (graph.Node(_selectedNodeId ?? string.Empty) is null)
        {
            _selectedNodeId = graph.OutputNode?.Id;
        }

        var rows = new Dictionary<int, int>();
        var placed = new Dictionary<string, NodeViewModel>(StringComparer.Ordinal);
        NodeViewModel Place(string id, string label, string type, int column, bool output, bool enabled, bool keyed)
        {
            int row = rows.GetValueOrDefault(column);
            rows[column] = row + 1;
            var view = new NodeViewModel(id, label, type, Margin + (column * ColumnWidth), Margin + (row * RowHeight), output, enabled, keyed)
            {
                IsSelected = id.Length > 0 && id == _selectedNodeId,
            };
            placed[id] = view;
            Nodes.Add(view);
            return view;
        }

        Place(string.Empty, "Input", string.Empty, 0, output: false, enabled: true, keyed: false);
        string? shown = graph.OutputNode?.Id;
        for (int index = 0; index < graph.Nodes.Length; index++)
        {
            GradeNode node = graph.Nodes[index];
            Place(node.Id, $"{index + 1} {Short(node.Effect.TypeId)}", node.Effect.TypeId, Depth(node, 0), node.Id == shown, node.Effect.Enabled, node.Key is not null);
        }

        foreach (GradeNode node in graph.Nodes)
        {
            NodeViewModel to = placed[node.Id];
            foreach (string from in node.Inputs.IsEmpty ? [string.Empty] : node.Inputs.AsEnumerable())
            {
                if (placed.TryGetValue(from, out NodeViewModel? source))
                {
                    Links.Add(new NodeLink(from, node.Id, source.Out, to.In, IsKey: false));
                }
            }

            if (node.Key is { } key && placed.TryGetValue(key, out NodeViewModel? qualifier))
            {
                Links.Add(new NodeLink(key, node.Id, qualifier.Out, new Point(to.X + (NodeViewModel.Width / 2), to.Y + NodeViewModel.Height), IsKey: true));
            }
        }

        NodesWidth = Nodes.Max(node => node.X) + NodeViewModel.Width + Margin;
        NodesHeight = Nodes.Max(node => node.Y) + NodeViewModel.Height + Margin;
        return graph.Node(_selectedNodeId ?? string.Empty);
    }

    private static string Short(string typeId) => typeId switch
    {
        "color.wheels" => "Wheels",
        "color.curves" => "Curves",
        "color.hsl" => "HSL",
        "color.lut" => "LUT",
        "color.white-balance" => "Balance",
        GradeGraph.MixType => "Mix",
        _ => "?",
    };
}
