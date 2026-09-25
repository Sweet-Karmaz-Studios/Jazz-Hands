using System.Windows;

namespace JazzHands.App.Views.Settings;

/// <summary>The New Project dialog. Everything it does is its viewmodel's.</summary>
public partial class NewProjectWindow : Window
{
    /// <summary>Creates the window, with the name ready to type over.</summary>
    public NewProjectWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }
}
