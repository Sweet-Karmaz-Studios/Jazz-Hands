using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JazzHands.Core.Model;

namespace JazzHands.App.ViewModels.Inspector;

/// <summary>A clip's own parameters of one kind: Transform, Opacity, Crop, Audio, or a generator's.</summary>
/// <param name="title">The section heading.</param>
public sealed partial class InspectorSectionViewModel(string title) : ObservableObject
{
    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>The heading.</summary>
    public string Title { get; } = title;

    /// <summary>The parameters, in order.</summary>
    public ObservableCollection<ParamRowViewModel> Rows { get; } = [];
}

/// <summary>What an effect in the inspector asks of the panel.</summary>
public interface IEffectEditor
{
    /// <summary>Bypass the effect, or turn it back on.</summary>
    void SetEnabled(EffectItemViewModel effect, bool enabled);

    /// <summary>Take it off the clip.</summary>
    void Remove(EffectItemViewModel effect);

    /// <summary>Move it to another place in the chain.</summary>
    void Move(EffectItemViewModel effect, int index);

    /// <summary>Put every parameter back to its default.</summary>
    void ResetAll(EffectItemViewModel effect);

    /// <summary>Adds a mask of a shape to a clip or one of its effects, over the middle of the picture.</summary>
    void AddMask(string ownerId, MaskShape shape);

    /// <summary>Takes a mask off.</summary>
    void RemoveMask(MaskItemViewModel mask);

    /// <summary>Changes how a mask combines with the ones before it, or turns it inside out.</summary>
    void SetMask(MaskItemViewModel mask, MaskMode? mode, bool? invert);
}

/// <summary>
/// One mask of the inspected clip or of one of its effects: how it combines, whether it keeps the
/// outside, and its feather, opacity and expansion. Its shape is drawn and moved on the preview.
/// </summary>
public sealed partial class MaskItemViewModel : ObservableObject
{
    private readonly IEffectEditor _editor;
    private bool _loading;

    [ObservableProperty]
    private string _mode = "add";

    [ObservableProperty]
    private bool _invert;

    /// <summary>Creates an item.</summary>
    public MaskItemViewModel(IEffectEditor editor, string id, string name)
    {
        ArgumentNullException.ThrowIfNull(editor);
        _editor = editor;
        Id = id;
        Name = name;
    }

    /// <summary>The mask's id.</summary>
    public string Id { get; }

    /// <summary>What it is called: its place and shape, "Mask 2, ellipse".</summary>
    public string Name { get; }

    /// <summary>The ways a mask combines with the ones before it.</summary>
    public static IReadOnlyList<string> Modes { get; } = ["add", "subtract", "intersect"];

    /// <summary>Its feather, opacity and expansion.</summary>
    public ObservableCollection<ParamRowViewModel> Rows { get; } = [];

    /// <summary>Shows the project's state without sending anything back.</summary>
    public void Load(MaskMode mode, bool invert)
    {
        _loading = true;
        try
        {
            Mode = mode.ToString().ToLowerInvariant();
            Invert = invert;
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnModeChanged(string value)
    {
        if (!_loading && Enum.TryParse(value, ignoreCase: true, out MaskMode mode))
        {
            _editor.SetMask(this, mode, null);
        }
    }

    partial void OnInvertChanged(bool value)
    {
        if (!_loading)
        {
            _editor.SetMask(this, null, value);
        }
    }

    [RelayCommand]
    private void Remove() => _editor.RemoveMask(this);
}

/// <summary>One effect on the inspected clip: its header, bypass switch, order and parameters.</summary>
public sealed partial class EffectItemViewModel : ObservableObject
{
    private readonly IEffectEditor _editor;
    private bool _loading;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand), nameof(MoveDownCommand))]
    private int _index;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    private int _count;

    /// <summary>Creates an item.</summary>
    public EffectItemViewModel(IEffectEditor editor, string id, string typeId, string name, bool known)
    {
        ArgumentNullException.ThrowIfNull(editor);
        _editor = editor;
        Id = id;
        TypeId = typeId;
        Name = name;
        Known = known;
    }

    /// <summary>The effect's id.</summary>
    public string Id { get; }

    /// <summary>Its type.</summary>
    public string TypeId { get; }

    /// <summary>What it is called.</summary>
    public string Name { get; }

    /// <summary>False for a type this build does not have: shown, kept, and not run.</summary>
    public bool Known { get; }

    /// <summary>Its parameters, in order.</summary>
    public ObservableCollection<ParamRowViewModel> Rows { get; } = [];

    /// <summary>Why it is not heard or seen though it is on: a plugin not installed here, a type this build lacks.</summary>
    public string Note { get; init; } = string.Empty;

    /// <summary>True when there is a <see cref="Note"/> to show under the header.</summary>
    public bool HasNote => Note.Length > 0;

    /// <summary>True for a picture effect on a clip, which a mask can limit to part of the frame.</summary>
    public bool CanMask { get; init; }

    /// <summary>The masks limiting where it applies, in order.</summary>
    public ObservableCollection<MaskItemViewModel> Masks { get; } = [];

    /// <summary>Shows the project's state without sending anything back.</summary>
    public void Load(bool enabled, int index, int count)
    {
        _loading = true;
        try
        {
            Enabled = enabled;
            Index = index;
            Count = count;
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnEnabledChanged(bool value)
    {
        if (!_loading)
        {
            _editor.SetEnabled(this, value);
        }
    }

    private bool CanMoveUp() => Index > 0;

    private bool CanMoveDown() => Index < Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => _editor.Move(this, Index - 1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => _editor.Move(this, Index + 1);

    [RelayCommand]
    private void Remove() => _editor.Remove(this);

    [RelayCommand]
    private void ResetAll() => _editor.ResetAll(this);

    /// <summary>Adds a mask: rectangle or ellipse, drawn over the middle of the picture to be moved on the preview.</summary>
    [RelayCommand]
    private void AddMask(string shape)
    {
        if (Enum.TryParse(shape, ignoreCase: true, out MaskShape parsed))
        {
            _editor.AddMask(Id, parsed);
        }
    }
}
