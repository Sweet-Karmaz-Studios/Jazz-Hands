using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using JazzHands.Core.Titles;

namespace JazzHands.App.Controls;

/// <summary>
/// A rich text box for a title's text: shows its markup as styled text, and hands back markup
/// when the text changes.
/// </summary>
/// <remarks>
/// Typing is sent after a short pause and when the box loses focus, through
/// <see cref="CommitCommand"/>, which merges into one undo step while the same title is edited.
/// The box keeps no undo of its own: Ctrl+Z goes to the editor's, which also takes back a style
/// the buttons put on. A change from elsewhere (an undo, the command line) replaces the document
/// only when it says something different from what is in the box, and keeps the selection.
/// </remarks>
public sealed class TitleTextEditor : RichTextBox
{
    /// <summary>The title's text as markup.</summary>
    public static readonly DependencyProperty MarkupProperty = DependencyProperty.Register(
        nameof(Markup),
        typeof(string),
        typeof(TitleTextEditor),
        new FrameworkPropertyMetadata(string.Empty, (target, _) => ((TitleTextEditor)target).Show()));

    /// <summary>The title's own size, which span sizes are shown against.</summary>
    public static readonly DependencyProperty BaseSizeProperty = DependencyProperty.Register(
        nameof(BaseSize),
        typeof(double),
        typeof(TitleTextEditor),
        new FrameworkPropertyMetadata(96.0));

    /// <summary>Called with new markup when the text has changed.</summary>
    public static readonly DependencyProperty CommitCommandProperty = DependencyProperty.Register(
        nameof(CommitCommand),
        typeof(ICommand),
        typeof(TitleTextEditor));

    /// <summary>Where the selection starts, in the plain text.</summary>
    public static readonly DependencyProperty SelectedStartProperty = DependencyProperty.Register(
        nameof(SelectedStart),
        typeof(int),
        typeof(TitleTextEditor),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>How long the selection is, in the plain text.</summary>
    public static readonly DependencyProperty SelectedLengthProperty = DependencyProperty.Register(
        nameof(SelectedLength),
        typeof(int),
        typeof(TitleTextEditor),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    private readonly DispatcherTimer _pause;
    private bool _showing;

    /// <summary>Creates the editor.</summary>
    public TitleTextEditor()
    {
        IsUndoEnabled = false;
        AcceptsReturn = true;
        AcceptsTab = false;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

        _pause = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(400) };
        _pause.Tick += (_, _) =>
        {
            _pause.Stop();
            Commit();
        };

        TextChanged += (_, _) =>
        {
            if (!_showing)
            {
                _pause.Stop();
                _pause.Start();
            }
        };

        SelectionChanged += (_, _) =>
        {
            if (!_showing)
            {
                SelectedStart = TitleDocument.Offset(Document, Selection.Start);
                SelectedLength = TitleDocument.Offset(Document, Selection.End) - SelectedStart;
            }
        };

        LostKeyboardFocus += (_, _) =>
        {
            _pause.Stop();
            Commit();
        };

        Show();
    }

    /// <summary>The title's text as markup.</summary>
    public string Markup
    {
        get => (string)GetValue(MarkupProperty);
        set => SetValue(MarkupProperty, value);
    }

    /// <summary>The title's own size.</summary>
    public double BaseSize
    {
        get => (double)GetValue(BaseSizeProperty);
        set => SetValue(BaseSizeProperty, value);
    }

    /// <summary>Called with new markup.</summary>
    public ICommand? CommitCommand
    {
        get => (ICommand?)GetValue(CommitCommandProperty);
        set => SetValue(CommitCommandProperty, value);
    }

    /// <summary>Where the selection starts.</summary>
    public int SelectedStart
    {
        get => (int)GetValue(SelectedStartProperty);
        set => SetValue(SelectedStartProperty, value);
    }

    /// <summary>How long the selection is.</summary>
    public int SelectedLength
    {
        get => (int)GetValue(SelectedLengthProperty);
        set => SetValue(SelectedLengthProperty, value);
    }

    /// <summary>The box's text as markup.</summary>
    public string Current => TitleMarkup.Format(TitleDocument.FromDocument(Document, BaseSize));

    /// <summary>Sends what is typed, if it is not what the title already says.</summary>
    public void Commit()
    {
        string markup = Current;
        if (!string.Equals(markup, Markup, StringComparison.Ordinal) && CommitCommand is { } command && command.CanExecute(markup))
        {
            command.Execute(markup);
        }
    }

    /// <inheritdoc />
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // Escape gives the text back to what the title says; Ctrl+Enter sends it at once.
        if (e.Key == Key.Escape)
        {
            _pause.Stop();
            Show(force: true);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _pause.Stop();
            Commit();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    /// <summary>Shows the markup, unless the box already says the same.</summary>
    private void Show(bool force = false)
    {
        string markup = Markup ?? string.Empty;
        if (!force && string.Equals(markup, Current, StringComparison.Ordinal))
        {
            return;
        }

        int start = SelectedStart;
        int length = SelectedLength;
        _showing = true;
        try
        {
            Document = TitleDocument.ToDocument(TitleMarkup.Parse(markup), BaseSize);
            Selection.Select(TitleDocument.Pointer(Document, start), TitleDocument.Pointer(Document, start + length));
        }
        finally
        {
            _showing = false;
        }
    }
}
