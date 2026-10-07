using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.App.Services;
using JazzHands.App.Shell;
using JazzHands.Core.Commands;
using JazzHands.Core.Effects;
using JazzHands.Core.Model;
using JazzHands.Core.Time;
using JazzHands.Engine.Effects;
using JazzHands.Engine.Playback;
using JazzHands.Engine.Selection;
using Serilog;
using ICommand = JazzHands.Core.Commands.ICommand;

namespace JazzHands.App.ViewModels.Comp;

/// <summary>A port of a node, drawn as a dot on its left edge.</summary>
/// <param name="NodeId">The node.</param>
/// <param name="Name">The port's name.</param>
/// <param name="At">Where it is in the view.</param>
/// <param name="IsWired">True when something is wired into it.</param>
public sealed record CompPortViewModel(string NodeId, string Name, Point At, bool IsWired)
{
    /// <summary>What the port reads, in words.</summary>
    public string Label => Name;
}

/// <summary>A wire drawn from one node's output to another's port.</summary>
/// <param name="From">The node read.</param>
/// <param name="To">The node reading it.</param>
/// <param name="Port">Which of its ports.</param>
/// <param name="Start">Where it leaves.</param>
/// <param name="End">Where it arrives.</param>
public sealed record CompWireViewModel(string From, string To, string Port, Point Start, Point End);

/// <summary>A node drawn in the Nodes panel.</summary>
public sealed partial class CompNodeViewModel : ObservableObject
{
    /// <summary>A node's width.</summary>
    public const double Width = 150;

    /// <summary>The height of its title.</summary>
    public const double Header = 24;

    /// <summary>The height of each port's row.</summary>
    public const double Row = 18;

    /// <summary>Creates a node.</summary>
    public CompNodeViewModel(string id, string typeId, string title, double x, double y, IReadOnlyList<string> ports, bool isEnabled)
    {
        Id = id;
        TypeId = typeId;
        Title = title;
        _x = x;
        _y = y;
        PortNames = ports;
        IsEnabled = isEnabled;
    }

    /// <summary>The node's id.</summary>
    public string Id { get; }

    /// <summary>Its type.</summary>
    public string TypeId { get; }

    /// <summary>What it is, as a title.</summary>
    public string Title { get; }

    /// <summary>Its ports' names, top to bottom.</summary>
    public IReadOnlyList<string> PortNames { get; }

    /// <summary>False for a node switched off.</summary>
    public bool IsEnabled { get; }

    /// <summary>True for the output, which nothing reads.</summary>
    public bool IsOutput => TypeId == CompGraph.Out;

    /// <summary>True for a group, which is opened by a double click.</summary>
    public bool IsGroup => TypeId == CompGraph.Group;

    /// <summary>True for nodes that give a picture, which have an output dot.</summary>
    public bool HasOutput => !IsOutput;

    /// <summary>Its height: the title and a row per port, at least one.</summary>
    public double Height => Header + (Math.Max(1, PortNames.Count) * Row) + 6;

    /// <summary>Its ports, with where they are.</summary>
    public ObservableCollection<CompPortViewModel> Ports { get; } = [];

    /// <summary>Where a wire out of it leaves.</summary>
    public Point Output => new(X + Width, Y + Header + (Row / 2));

    /// <summary>Its left edge.</summary>
    [ObservableProperty]
    private double _x;

    /// <summary>Its top edge.</summary>
    [ObservableProperty]
    private double _y;

    /// <summary>True for the node the Inspector shows.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>True for the node the program monitor shows.</summary>
    [ObservableProperty]
    private bool _isViewed;

    /// <summary>Where a port's dot is, by its place in the list.</summary>
    public Point PortAt(int index) => new(X, Y + Header + (index * Row) + (Row / 2));
}

/// <summary>A node type offered in the Add menu.</summary>
/// <param name="Group">Its submenu.</param>
/// <param name="Name">What the menu says.</param>
/// <param name="TypeId">What <c>comp.node-add</c> is given.</param>
public sealed record CompNodeType(string Group, string Name, string TypeId);

/// <summary>
/// The Nodes panel (Phase 49): the selected clip's comp graph, drawn as nodes and wires. Clicking a
/// node shows it in the Inspector; dragging it moves it; dragging from its output dot to another
/// node's port wires them; right-clicking a port takes its wire out. Every change is a <c>comp.*</c>
/// command the CLI could send, a drag one command when it ends.
/// </summary>
public sealed partial class CompPanelViewModel : ToolViewModel
{
    /// <summary>The id the layout knows the panel by.</summary>
    public const string PanelId = "nodes";

    private const double Margin = 60;

    private readonly ILogger _log = Log.ForContext<CompPanelViewModel>();
    private readonly ISession _session;
    private readonly SelectionService _selection;
    private readonly IUiDispatcher _ui;
    private readonly IPreviewEngine? _preview;
    private readonly INodeViewer? _viewer;
    private Flicks _shownAt;

    /// <summary>Creates the panel.</summary>
    public CompPanelViewModel(ISession session, SelectionService selection, IUiDispatcher ui, IPreviewEngine? preview = null, INodeViewer? viewer = null)
        : base(PanelId, "Nodes")
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(ui);

        _session = session;
        _selection = selection;
        _ui = ui;
        _preview = preview;
        _viewer = viewer;
        _session.ProjectChanged += (_, _) => _ui.Post(Rebuild);
        _selection.Changed += (_, _) => _ui.Post(Rebuild);

        // The viewer follows the frame on screen. Playing, it draws the next picture once the last
        // is done, so it plays at the pace WARP draws a small picture and never queues behind.
        _shownAt = preview?.Position ?? Flicks.Zero;
        _preview?.PlayheadMoved += (_, moved) => _ui.Post(() =>
        {
            _shownAt = moved.Position;
            if (moved.State != TransportState.Playing || Viewing.IsCompleted)
            {
                RefreshViewer();
            }
        });
        Rebuild();
    }

    /// <summary>The clip whose graph is shown, or null.</summary>
    public string? ClipId { get; private set; }

    /// <summary>The clip's graph, or null when the clip has none.</summary>
    public string? GraphId { get; private set; }

    /// <summary>The graph the panel shows and adds to: the clip's, or the group it is inside (Phase 49a).</summary>
    public string? TargetId { get; private set; }

    /// <summary>The groups the panel is inside, outermost first.</summary>
    public IReadOnlyList<string> Path => _path;

    private List<string> _path = [];
    private string? _pathClip;

    /// <summary>The nodes.</summary>
    public ObservableCollection<CompNodeViewModel> Nodes { get; } = [];

    /// <summary>The wires.</summary>
    public ObservableCollection<CompWireViewModel> Wires { get; } = [];

    /// <summary>Every node type the Add menu offers: the graph's own, the 3D objects, then every video effect and generator.</summary>
    public IReadOnlyList<CompNodeType> NodeTypes { get; } = Types();

    /// <summary>What the panel says above the graph.</summary>
    [ObservableProperty]
    private string _heading = "Select a clip to see its comp graph.";

    /// <summary>True when a picture clip is selected.</summary>
    [ObservableProperty]
    private bool _hasClip;

    /// <summary>True when the selected clip has a graph.</summary>
    [ObservableProperty]
    private bool _hasGraph;

    /// <summary>How big the drawing is.</summary>
    [ObservableProperty]
    private double _surfaceWidth = 600;

    /// <summary>How tall the drawing is.</summary>
    [ObservableProperty]
    private double _surfaceHeight = 300;

    /// <summary>How far the view is zoomed: 1 is actual size.</summary>
    [ObservableProperty]
    private double _zoom = 1.0;

    /// <summary>The viewer's picture: the selected node (or the output) of the clip alone, at the playhead (Phase 49a).</summary>
    [ObservableProperty]
    private ImageSource? _viewerImage;

    /// <summary>What the viewer shows, in words.</summary>
    [ObservableProperty]
    private string _viewerCaption = string.Empty;

    /// <summary>True while the viewer is open beside the graph.</summary>
    [ObservableProperty]
    private bool _showViewer = true;

    /// <summary>True when there is a viewer to show: the host gave the panel one.</summary>
    public bool HasViewer => _viewer is not null;

    /// <summary>The viewer's last draw, for tests to wait on.</summary>
    public Task Viewing { get; private set; } = Task.CompletedTask;

    /// <summary>Why the last change did not take, or empty.</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>True when the panel is inside a group, so Up goes back out.</summary>
    [ObservableProperty]
    private bool _canGoUp;

    /// <summary>The node the Inspector shows, or null.</summary>
    public CompNodeViewModel? Selected => Nodes.FirstOrDefault(node => node.IsSelected);

    /// <summary>Every node selected here, in the graph's order.</summary>
    public IReadOnlyList<CompNodeViewModel> SelectedNodes => [.. Nodes.Where(node => node.IsSelected)];

    /// <summary>Gives the clip a graph.</summary>
    [RelayCommand]
    private Task CreateAsync() => ClipId is { } clip ? RunAsync(new CreateCompCommand(clip)) : Task.CompletedTask;

    /// <summary>Adds a node of a type at a place in the view.</summary>
    public Task AddAsync(string typeId, Point at) =>
        TargetId is { } graph ? RunAsync(new AddCompNodeCommand(graph, typeId, X: Math.Round(at.X), Y: Math.Round(at.Y))) : Task.CompletedTask;

    /// <summary>Adds a node of a type wired from the selected node, or beside the others.</summary>
    [RelayCommand]
    private Task AddTypeAsync(string typeId) =>
        TargetId is { } graph
            ? RunAsync(new AddCompNodeCommand(graph, typeId, From: Selected is { HasOutput: true } selected && CompGraph.PortsOf(typeId, IsGenerator(typeId)).Count > 0 ? selected.Id : null))
            : Task.CompletedTask;

    /// <summary>Takes the selected node out.</summary>
    [RelayCommand]
    private Task RemoveSelectedAsync() => Selected is { } node ? RunAsync(new RemoveCompNodeCommand(node.Id)) : Task.CompletedTask;

    /// <summary>Shows the selected node in the program monitor, or the program again when it already is.</summary>
    [RelayCommand]
    private Task ViewSelectedAsync()
    {
        string? viewed = _preview?.CompView;
        string? node = Selected?.Id;
        return RunAsync(new ViewCompNodeCommand(node is not null && node != viewed ? node : null));
    }

    /// <summary>Puts the selected nodes into a group of their own.</summary>
    [RelayCommand]
    private async Task GroupSelectedAsync()
    {
        if (SelectedNodes is not { Count: > 0 } chosen)
        {
            return;
        }

        // The new group is selected as it is made, so the panel stays here rather than following
        // the nodes inside it; refused, the nodes are selected again.
        string[] ids = [.. chosen.Select(node => node.Id)];
        string group = Id.New();
        _selection.Set([group]);
        if (!await TryAsync(new GroupCompNodesCommand([.. ids], group)).ConfigureAwait(true))
        {
            _selection.Set(ids);
        }
    }

    /// <summary>Takes the selected group's nodes back out.</summary>
    [RelayCommand]
    private Task UngroupSelectedAsync() =>
        Selected is { IsGroup: true } group ? RunAsync(new UngroupCompNodeCommand(group.Id)) : Task.CompletedTask;

    /// <summary>Goes out of the group the panel is inside, with that group selected.</summary>
    [RelayCommand]
    private void Up()
    {
        if (_path.Count > 0)
        {
            string left = _path[^1];
            _path.RemoveAt(_path.Count - 1);
            _selection.Set([left]);
            Rebuild();
        }
    }

    /// <summary>Goes inside a group.</summary>
    public void Enter(CompNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.IsGroup)
        {
            _path.Add(node.Id);
            _selection.Set([]);
            Rebuild();
        }
    }

    /// <summary>Shows a node in the Inspector; with <paramref name="extend"/>, adds it to the nodes selected, or takes it away.</summary>
    public void Select(CompNodeViewModel node, bool extend = false)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!extend)
        {
            _selection.Set([node.Id]);
            return;
        }

        List<string> ids = [.. SelectedNodes.Select(item => item.Id)];
        if (!ids.Remove(node.Id))
        {
            ids.Add(node.Id);
        }

        _selection.Set(ids);
    }

    /// <summary>Moves a node to where a drag left it: one command.</summary>
    public Task MoveAsync(CompNodeViewModel node, Point to)
    {
        ArgumentNullException.ThrowIfNull(node);
        return RunAsync(new MoveCompNodeCommand(node.Id, Math.Round(to.X, 1), Math.Round(to.Y, 1)));
    }

    /// <summary>Wires one node into another: into the port named, or its first free one.</summary>
    public Task ConnectAsync(string from, string to, string? port) => RunAsync(new ConnectCompNodeCommand(to, from, port));

    /// <summary>Takes the wire out of a port.</summary>
    public Task DisconnectAsync(string node, string port) => RunAsync(new DisconnectCompNodeCommand(node, port));

    /// <summary>The node under a point of the drawing, or null.</summary>
    public CompNodeViewModel? NodeAt(Point at) =>
        Nodes.LastOrDefault(node => at.X >= node.X && at.X <= node.X + CompNodeViewModel.Width && at.Y >= node.Y && at.Y <= node.Y + node.Height);

    /// <summary>The port under a point, within a few pixels, or null.</summary>
    public CompPortViewModel? PortAt(Point at) =>
        Nodes.SelectMany(node => node.Ports).FirstOrDefault(port => (port.At - at).Length <= 8);

    /// <summary>Rereads the graph from the project.</summary>
    public void Rebuild()
    {
        Project project = _session.Project;
        string? selected = _selection.Ids.FirstOrDefault();
        ParamOwner? owner = selected is null ? null : ParamTargets.Find(project, selected);

        // A node selected here keeps its clip; a clip selected on the timeline is its own.
        ClipLocation? found = owner switch
        {
            { Kind: ParamOwnerKind.Clip, Clip: { } clip } => project.FindClip(clip.Id),
            { Kind: ParamOwnerKind.Effect, Graph.Comp: not null, Clip: { } clip } => project.FindClip(clip.Id),
            _ => ClipId is { } kept ? project.FindClip(kept) : null,
        };

        Nodes.Clear();
        Wires.Clear();
        if (found is not { Track.Kind: TrackKind.Video } || SceneObjects.Is(found.Clip))
        {
            ClipId = GraphId = TargetId = null;
            HasClip = HasGraph = CanGoUp = false;
            _path = [];
            Heading = "Select a clip to see its comp graph.";
            OnPropertyChanged(nameof(Selected));
            return;
        }

        Clip picked = found.Clip;
        ClipId = picked.Id;
        HasClip = true;
        Effect? holder = picked.Effects.FirstOrDefault(effect => effect.TypeId == CompGraph.TypeId);
        GraphId = holder?.Id;
        CompGraph top = holder?.Comp ?? CompGraph.Empty;
        HasGraph = holder is not null && !top.Nodes.IsEmpty;

        // Another clip starts at its top; a node selected inside a group opens the groups holding it.
        if (_pathClip != picked.Id)
        {
            _path = [];
            _pathClip = picked.Id;
        }

        HashSet<string> chosen = new(_selection.Ids, StringComparer.Ordinal);
        if (selected is not null && Inside(top, _path).Node(selected) is null && PathTo(top, selected) is { } into)
        {
            _path = into;
        }

        // The groups still there, as far as they go.
        CompGraph graph = top;
        var reached = new List<string>();
        var names = new List<string> { $"'{picked.Name}'" };
        foreach (string id in _path)
        {
            if (graph.Node(id) is not { Effect: { TypeId: CompGraph.Group, Comp: { } inner } })
            {
                break;
            }

            reached.Add(id);
            names.Add($"Group {id[^4..]}");
            graph = inner;
        }

        _path = reached;
        CanGoUp = _path.Count > 0;
        TargetId = _path.Count > 0 ? _path[^1] : GraphId;
        Heading = HasGraph
            ? $"{string.Join(" > ", names)}: {graph.Nodes.Length} nodes"
            : $"'{picked.Name}' has no comp graph.";

        string? viewed = _preview?.CompView;
        double offsetX = Math.Min(0, graph.Nodes.Select(node => node.X).DefaultIfEmpty(0).Min()) - Margin;
        double offsetY = Math.Min(0, graph.Nodes.Select(node => node.Y).DefaultIfEmpty(0).Min()) - Margin;
        foreach (CompNode node in graph.Nodes)
        {
            IReadOnlyList<string> ports = CompGraph.PortsOf(node.Effect.TypeId, IsGenerator(node.Effect.TypeId));
            var view = new CompNodeViewModel(node.Id, node.Effect.TypeId, Name(node.Effect.TypeId), node.X - offsetX, node.Y - offsetY, ports, node.Effect.Enabled)
            {
                IsSelected = chosen.Contains(node.Id),
                IsViewed = node.Id == viewed,
            };
            for (int index = 0; index < ports.Count; index++)
            {
                view.Ports.Add(new CompPortViewModel(node.Id, ports[index], view.PortAt(index), node.Input(ports[index]) is not null));
            }

            Nodes.Add(view);
        }

        foreach (CompNode node in graph.Nodes)
        {
            CompNodeViewModel to = Nodes.First(view => view.Id == node.Id);
            foreach (CompInput input in node.Inputs)
            {
                if (Nodes.FirstOrDefault(view => view.Id == input.From) is { } from)
                {
                    int index = Math.Max(0, to.PortNames.ToList().IndexOf(input.Port));
                    Wires.Add(new CompWireViewModel(from.Id, to.Id, input.Port, from.Output, to.PortAt(index)));
                }
            }
        }

        SurfaceWidth = Math.Max(600, Nodes.Select(node => node.X + CompNodeViewModel.Width).DefaultIfEmpty(0).Max() + Margin);
        SurfaceHeight = Math.Max(300, Nodes.Select(node => node.Y + node.Height).DefaultIfEmpty(0).Max() + Margin);
        _origin = new Vector(offsetX, offsetY);
        OnPropertyChanged(nameof(Selected));
        RefreshViewer();
    }

    /// <summary>Draws the viewer again.</summary>
    public void RefreshViewer() => Viewing = ViewAsync();

    partial void OnShowViewerChanged(bool value) => RefreshViewer();

    private async Task ViewAsync()
    {
        CompNodeViewModel? shown = Selected ?? Nodes.FirstOrDefault(node => node.IsOutput);
        if (_viewer is null || !ShowViewer || ClipId is not { } clipId || shown is null || _session.Project.FindClip(clipId) is not { } found)
        {
            ViewerImage = null;
            ViewerCaption = string.Empty;
            return;
        }

        Clip clip = found.Clip;
        Flicks at = _shownAt >= clip.Start && _shownAt < clip.End ? _shownAt : clip.Start;
        string caption = $"{shown.Title} at {Timecode.FormatClock(at)}";
        ImageSource? image = await _viewer.RenderAsync(_session.Project, clipId, shown.Id, at, _session.ProjectPath).ConfigureAwait(true);

        // A newer draw that finished first is not covered by an older one; a draw cut short changes nothing.
        if (image is not null)
        {
            ViewerImage = image;
            ViewerCaption = caption;
        }
    }

    /// <summary>The groups leading to the graph that holds a node directly, outermost first; null when the graph does not have it.</summary>
    private static List<string>? PathTo(CompGraph graph, string id)
    {
        if (graph.Node(id) is not null)
        {
            return [];
        }

        foreach (CompNode node in graph.Nodes)
        {
            if (node.Effect is { TypeId: CompGraph.Group, Comp: { } inner } && PathTo(inner, id) is { } below)
            {
                return [node.Id, .. below];
            }
        }

        return null;
    }

    /// <summary>The graph a path of groups leads to, as far as it goes.</summary>
    private static CompGraph Inside(CompGraph graph, IEnumerable<string> path)
    {
        foreach (string id in path)
        {
            if (graph.Node(id) is not { Effect: { TypeId: CompGraph.Group, Comp: { } inner } })
            {
                break;
            }

            graph = inner;
        }

        return graph;
    }

    /// <summary>Where the view's top left is in the graph's own coordinates, which drags and adds are given in.</summary>
    public Point ToGraph(Point view) => view + _origin;

    private Vector _origin;

    private static bool IsGenerator(string typeId) => EffectCatalog.Registry.Find(typeId) is { Kind: EffectKind.Generator };

    private static string Name(string typeId) =>
        CompNodes.Find(typeId)?.Name ?? EffectCatalog.Registry.Find(typeId)?.Name ?? typeId;

    private static List<CompNodeType> Types()
    {
        var types = new List<CompNodeType>
        {
            new("Node", "Merge", CompGraph.Merge),
            new("Node", "Transform", CompGraph.Transform),
            new("Node", "Matte", CompGraph.Matte),
            new("Node", "Media", CompGraph.Media),
            new("Node", "In", CompGraph.In),
            new("Node", "Out", CompGraph.Out),
            new("Node", "Group", CompGraph.Group),
            new("3D", "3D render", CompGraph.Render3D),
            new("3D", "3D plane", CompGraph.Plane),
            new("3D", "3D text", SceneObjects.Text),
            new("3D", "3D shape", SceneObjects.Shape),
            new("3D", "3D model", SceneObjects.Model),
            new("3D", "3D camera", SceneObjects.Camera),
            new("3D", "3D light", SceneObjects.Light),
        };

        foreach (EffectDescriptor descriptor in EffectCatalog.Registry.All
            .Where(descriptor => descriptor.Kind is EffectKind.Video or EffectKind.Generator && descriptor.TypeId != CompGraph.TypeId && !SceneObjects.Is(descriptor.TypeId) && !SceneObjects.IsMesh(descriptor.TypeId))
            .OrderBy(descriptor => descriptor.Category, StringComparer.Ordinal)
            .ThenBy(descriptor => descriptor.Name, StringComparer.Ordinal))
        {
            types.Add(new CompNodeType(descriptor.Kind == EffectKind.Generator ? $"Generator: {descriptor.Category}" : $"Effect: {descriptor.Category}", descriptor.Name, descriptor.TypeId));
        }

        return types;
    }

    private Task RunAsync(ICommand command) => TryAsync(command);

    /// <summary>Runs a command; true when it took, otherwise false with why in <see cref="Status"/>.</summary>
    private async Task<bool> TryAsync(ICommand command)
    {
        Status = string.Empty;
        try
        {
            CommandResult result = await _session.ExecuteAsync(command).ConfigureAwait(true);
            if (!result.Ok)
            {
                Status = result.Error ?? result.Code ?? "That did not work.";
            }

            return result.Ok;
        }
        catch (CommandException refused)
        {
            Status = refused.Message;
        }
        catch (InvalidOperationException error)
        {
            _log.Warning(error, "A comp graph change failed");
            Status = error.Message;
        }

        return false;
    }
}
