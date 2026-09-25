using System.Windows;

namespace JazzHands.App.Views.Settings;

/// <summary>Asks for one line of text: a workspace's name, and the like.</summary>
public partial class TextPromptWindow : Window
{
    /// <summary>Creates the prompt, with the text ready to type over.</summary>
    public TextPromptWindow(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        Prompt.Text = prompt;
        Input.Text = initial;
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    /// <summary>What was typed, when OK was pressed.</summary>
    public string? Answer { get; private set; }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Answer = Input.Text.Trim();
        DialogResult = Answer.Length > 0;
    }
}
