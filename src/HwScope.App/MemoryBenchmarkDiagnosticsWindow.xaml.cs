using System.Windows;
using Wpf.Ui.Controls;

namespace HwScope.App;

public partial class MemoryBenchmarkDiagnosticsWindow : FluentWindow
{
    public MemoryBenchmarkDiagnosticsWindow(string diagnosticsText)
    {
        InitializeComponent();

        DiagnosticsTextBox.Text = diagnosticsText;
        Loaded += MemoryBenchmarkDiagnosticsWindow_Loaded;
    }

    private void MemoryBenchmarkDiagnosticsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        App.ThemeService.Attach(this);
        Loaded -= MemoryBenchmarkDiagnosticsWindow_Loaded;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(DiagnosticsTextBox.Text);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
