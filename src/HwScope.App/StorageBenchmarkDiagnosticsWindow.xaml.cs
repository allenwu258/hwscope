using System.Windows;
using Wpf.Ui.Controls;

namespace HwScope.App;

public partial class StorageBenchmarkDiagnosticsWindow : FluentWindow
{
    public StorageBenchmarkDiagnosticsWindow(string text)
    {
        InitializeComponent();
        DiagnosticsTextBox.Text = text;
        Loaded += (_, _) => App.ThemeService.Attach(this);
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(DiagnosticsTextBox.Text);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
