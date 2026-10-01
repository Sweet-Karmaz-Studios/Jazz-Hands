using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;

namespace JazzHands.App.Controls;

/// <summary>
/// A menu whose items answer UI Automation's Invoke the way a button does: the click is queued
/// and the call returns at once.
/// </summary>
/// <remarks>
/// A WPF menu item clicks inside the automation call. When the click opens a modal window (the
/// Windows file picker behind Import media..., Open...), that window's message loop runs inside
/// the call, and a screen reader or the UI suite waits on it until the window closes, with the
/// editor answering nothing meanwhile. A mouse or a key is
/// unchanged.
/// </remarks>
public class QueuedMenu : Menu
{
    /// <inheritdoc />
    protected override DependencyObject GetContainerForItemOverride() => new QueuedMenuItem();

    /// <inheritdoc />
    protected override bool IsItemItsOwnContainerOverride(object item) => item is MenuItem or Separator;
}

/// <summary>A menu item whose automation Invoke is queued; its submenu's items are the same.</summary>
public class QueuedMenuItem : MenuItem
{
    /// <summary>Creates an item.</summary>
    /// <remarks>
    /// Windows' own menu item style binds these two to the nearest ItemsControl, and a generated
    /// item has none until it is placed: each such item logged two binding errors. Set here, the
    /// bindings are never made; the theme's templates do not read them.
    /// </remarks>
    public QueuedMenuItem()
    {
        HorizontalContentAlignment = HorizontalAlignment.Left;
        VerticalContentAlignment = VerticalAlignment.Center;
    }

    /// <inheritdoc />
    protected override DependencyObject GetContainerForItemOverride() => new QueuedMenuItem();

    /// <inheritdoc />
    protected override bool IsItemItsOwnContainerOverride(object item) => item is MenuItem or Separator;

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new QueuedPeer(this);

    private sealed class QueuedPeer(MenuItem owner) : MenuItemAutomationPeer(owner)
    {
        public override object GetPattern(PatternInterface patternInterface)
        {
            object pattern = base.GetPattern(patternInterface);
            return patternInterface == PatternInterface.Invoke && pattern is IInvokeProvider click
                ? new QueuedInvoke((MenuItem)Owner, click)
                : pattern;
        }
    }

    private sealed class QueuedInvoke(MenuItem owner, IInvokeProvider click) : IInvokeProvider
    {
        public void Invoke()
        {
            // Refused now, as the item's own Invoke would; once queued, nothing is left to tell.
            if (!owner.IsEnabled)
            {
                throw new ElementNotEnabledException();
            }

            owner.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (owner.IsEnabled)
                {
                    click.Invoke();
                }
            });
        }
    }
}
