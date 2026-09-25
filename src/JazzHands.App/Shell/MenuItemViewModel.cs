using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using WpfCommand = System.Windows.Input.ICommand;

namespace JazzHands.App.Shell;

/// <summary>
/// One entry of the window's menu: a header, what it does, the keys that do the same, and
/// whether it is ticked.
/// </summary>
/// <remarks>
/// The menu is built from viewmodels rather than written in XAML so that a key rebound in the
/// keymap editor shows its new key in the menu at once, and so that a test can walk the whole
/// menu and find every shortcut in it.
/// </remarks>
public sealed partial class MenuItemViewModel : ObservableObject
{
    private readonly Func<bool>? _checked;

    [ObservableProperty]
    private string _gesture;

    [ObservableProperty]
    private bool _isChecked;

    /// <summary>An entry.</summary>
    /// <param name="header">What it says, with an underscore before its access key.</param>
    /// <param name="command">What it does, or null for a submenu.</param>
    /// <param name="gesture">The keys shown beside it.</param>
    /// <param name="isChecked">For an entry that can be ticked: whether it is, read each time the menu opens.</param>
    /// <param name="toolTip">A longer word on what it does.</param>
    public MenuItemViewModel(string header, WpfCommand? command = null, string gesture = "", Func<bool>? isChecked = null, string? toolTip = null)
    {
        Header = header;
        Command = command;
        _gesture = gesture;
        _checked = isChecked;
        ToolTip = toolTip;
        Refresh();
    }

    private MenuItemViewModel()
    {
        Header = string.Empty;
        _gesture = string.Empty;
        IsSeparator = true;
    }

    /// <summary>What it says.</summary>
    public string Header { get; }

    /// <summary>What it does.</summary>
    public WpfCommand? Command { get; }

    /// <summary>What the command is given.</summary>
    public object? CommandParameter { get; init; }

    /// <summary>The entries under it.</summary>
    public ObservableCollection<MenuItemViewModel> Items { get; } = [];

    /// <summary>True for the line between groups.</summary>
    public bool IsSeparator { get; }

    /// <summary>True for an entry that can be ticked.</summary>
    public bool IsCheckable => _checked is not null;

    /// <summary>A longer word on what it does, with its keys.</summary>
    public string? ToolTip { get; }

    /// <summary>The keymap command it stands for, when it stands for one; for the keymap to find it.</summary>
    public string? KeymapCommand { get; init; }

    /// <summary>That command's arguments.</summary>
    public JsonObject? KeymapArgs { get; init; }

    /// <summary>A line between groups.</summary>
    public static MenuItemViewModel Separator() => new();

    /// <summary>Reads whether it is ticked, and the same for everything under it; called as the menu opens.</summary>
    public void Refresh()
    {
        if (_checked is not null)
        {
            IsChecked = _checked();
        }

        foreach (MenuItemViewModel item in Items)
        {
            item.Refresh();
        }
    }

    /// <summary>Every entry under this one, at any depth.</summary>
    public IEnumerable<MenuItemViewModel> Descendants() => Items.SelectMany(item => (IEnumerable<MenuItemViewModel>)[item, .. item.Descendants()]);

    /// <summary>Adds entries and returns this one, for building a menu in one expression.</summary>
    public MenuItemViewModel With(params MenuItemViewModel?[] items)
    {
        foreach (MenuItemViewModel? item in items)
        {
            if (item is not null)
            {
                Items.Add(item);
            }
        }

        return this;
    }
}
