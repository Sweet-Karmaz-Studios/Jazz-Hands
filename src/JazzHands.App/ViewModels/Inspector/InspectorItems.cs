using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
}
