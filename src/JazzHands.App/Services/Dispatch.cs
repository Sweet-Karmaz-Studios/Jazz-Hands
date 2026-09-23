using System.Windows;
using System.Windows.Threading;

namespace JazzHands.App.Services;

/// <summary>
/// Getting back onto the UI thread from an engine one.
/// </summary>
/// <remarks>
/// Engine events are raised on the dispatcher's own thread, and a viewmodel that touched an
/// observable collection from there would corrupt the bound list. Everything that reacts to an
/// engine event goes through here.
///
/// It is also what lets a viewmodel be tested without a WPF application: with no dispatcher
/// running, the work is done inline on the calling thread.
/// </remarks>
public static class Dispatch
{
    /// <summary>
    /// Runs <paramref name="work"/> on the UI thread, or inline when there is no application.
    /// </summary>
    public static void OnUi(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        Dispatcher? dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            work();
            return;
        }

        // DataBind priority rather than Normal: these updates feed bindings, and running them
        // ahead of layout keeps a burst of engine events from interleaving with render passes.
        dispatcher.InvokeAsync(work, DispatcherPriority.DataBind);
    }
}
