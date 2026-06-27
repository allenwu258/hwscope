using System.IO;
using System.Windows;
using HwScope.Core.Hardware.Cpu;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace HwScope.App.Windows;

public partial class CpuTopologyInspectWindow : FluentWindow
{
    private readonly CpuTopologyInspectReport _report;
    private readonly string _reportText;

    public CpuTopologyInspectWindow(string cpuName, CpuTopologyInspectReport report)
    {
        _report = report;
        _reportText = CpuTopologyInspectFormatter.Format(report);

        InitializeComponent();

        CpuNameText.Text = cpuName;
        GeneratedAtText.Text = $"Generated at {report.GeneratedAt:yyyy-MM-dd HH:mm:ss}";
        ReportTextBox.Text = _reportText;
        StatusText.Text = "Topology data is from Windows GetLogicalProcessorInformationEx. Hints are heuristic.";

        Loaded += CpuTopologyInspectWindow_Loaded;
    }

    private void CpuTopologyInspectWindow_Loaded(object sender, RoutedEventArgs e)
    {
        App.ThemeService.Attach(this);
        Loaded -= CpuTopologyInspectWindow_Loaded;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_reportText);
        StatusText.Text = "CPU topology inspect report copied to clipboard.";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存 CPU 拓扑 Inspect",
            Filter = "文本报告 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = $"HwScope-CPU-Topology-{_report.GeneratedAt:yyyyMMdd-HHmmss}.txt",
            AddExtension = true,
            DefaultExt = ".txt"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        File.WriteAllText(dialog.FileName, _reportText);
        StatusText.Text = $"CPU topology inspect report saved: {dialog.FileName}";
    }
}
