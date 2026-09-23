namespace JazzHands.App.Services;

/// <summary>
/// How a viewmodel gets back onto the UI thread.
/// </summary>
/// <remarks>
/// Engine events arrive on engine threads, and touching a bound collection from one of those
/// corrupts the list the UI is walking. Every viewmodel that reacts to an engine event posts
/// through this.
///
/// It is an interface rather than a call to <see cref="Dispatch"/> because a viewmodel that
/// reaches for <c>Application.Current</c> behaves differently depending on whether an application
/// happens to exist, which makes its tests depend on what ran before them.
/// </remarks>
public interface IUiDispatcher
{
    /// <summary>Runs work on the UI thread, soon.</summary>
    void Post(Action work);
}

/// <summary>Posts to WPF's dispatcher.</summary>
public sealed class WpfDispatcher : IUiDispatcher
{
    /// <inheritdoc />
    public void Post(Action work) => Dispatch.OnUi(work);
}

/// <summary>Runs the work where it stands. For tests, and for the headless case.</summary>
public sealed class InlineDispatcher : IUiDispatcher
{
    /// <inheritdoc />
    public void Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        work();
    }
}
