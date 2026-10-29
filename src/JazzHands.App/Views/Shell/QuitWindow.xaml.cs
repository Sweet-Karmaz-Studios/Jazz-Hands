using System.Globalization;
using System.Windows;
using JazzHands.App.Shell;

namespace JazzHands.App.Views.Shell;

/// <summary>Asks what to do about exports still running when the person quits.</summary>
public partial class QuitWindow : Window
{
    /// <summary>Creates the question for a number of running exports.</summary>
    public QuitWindow(int running)
    {
        InitializeComponent();
        Message.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{(running == 1 ? "An export is" : $"{running} exports are")} still running. Jazz Hands can finish {(running == 1 ? "it" : "them")} out of sight and then quit, pause {(running == 1 ? "it" : "them")} to resume from the Export Queue next time, or cancel {(running == 1 ? "it" : "them")} and quit now.");
    }

    /// <summary>What was chosen.</summary>
    public QuitChoice Choice { get; private set; } = QuitChoice.Cancel;

    /// <summary>
    /// The exports ended while the question was open, so there is nothing left to decide: it
    /// goes ahead as Wait would (QA, 2026-10-29: it went on saying an export was running).
    /// </summary>
    public void ExportsFinished()
    {
        if (IsVisible)
        {
            Choose(QuitChoice.Wait);
        }
    }

    private void Choose(QuitChoice choice)
    {
        Choice = choice;
        DialogResult = choice != QuitChoice.Cancel;
    }

    private void OnWait(object sender, RoutedEventArgs e) => Choose(QuitChoice.Wait);

    private void OnPause(object sender, RoutedEventArgs e) => Choose(QuitChoice.PauseForNextTime);

    private void OnQuitAnyway(object sender, RoutedEventArgs e) => Choose(QuitChoice.QuitAnyway);

    private void OnCancel(object sender, RoutedEventArgs e) => Choose(QuitChoice.Cancel);
}
