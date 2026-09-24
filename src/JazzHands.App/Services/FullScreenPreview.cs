using System.Windows;
using System.Windows.Interop;
using JazzHands.App.Controls.Preview;
using JazzHands.App.ViewModels.Playback;

namespace JazzHands.App.Services;

/// <summary>Opens the full screen preview on the monitor the editor is not on.</summary>
/// <param name="engine">Where frames come from.</param>
/// <param name="panel">The preview panel, which the window's keys are forwarded to.</param>
public sealed class FullScreenPreview(IPreviewEngine engine, Func<PreviewPanelViewModel> panel) : IFullScreenPreview
{
    private FullScreenPreviewWindow? _window;

    /// <inheritdoc />
    public event EventHandler? IsOpenChanged;

    /// <inheritdoc />
    public bool IsOpen => _window is not null;

    /// <inheritdoc />
    public void Toggle()
    {
        if (_window is not null)
        {
            _window.Close();
            return;
        }

        if (engine.Device is not { } device)
        {
            return;
        }

        IntPtr owner = Application.Current?.MainWindow is { } main ? new WindowInteropHelper(main).Handle : IntPtr.Zero;
        Int32Rect monitor = Monitors.OtherThan(owner);

        _window = new FullScreenPreviewWindow(
            device,
            engine,
            monitor,
            (key, modifiers, repeat) => panel().KeyDown(key, modifiers, repeat),
            key => panel().KeyUp(key),
            () => panel().Display);

        _window.Closed += (_, _) =>
        {
            _window = null;
            IsOpenChanged?.Invoke(this, EventArgs.Empty);
        };

        _window.Show();
        IsOpenChanged?.Invoke(this, EventArgs.Empty);
    }
}
