using System.IO;
using System.Windows;
using HwScope.App.Pages.Cpu;
using HwScope.App.Topology.Controls;
using HwScope.App.Topology.Model;
using HwScope.Core.Hardware.Cpu;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace HwScope.App.Windows;

public partial class CpuTopologyInspectWindow : FluentWindow
{
    private readonly CpuTopologyInspectReport _report;
    private readonly string _reportText;
    private readonly TopologyDocument _visualDocument;

    public CpuTopologyInspectWindow(string cpuName, CpuTopologyInspectReport report)
    {
        _report = report;
        _reportText = CpuTopologyInspectFormatter.Format(report);
        _visualDocument = CpuTopologyVisualAdapter.ToDocument(report, cpuName);

        InitializeComponent();

        CpuNameText.Text = cpuName;
        GeneratedAtText.Text = $"Generated at {report.GeneratedAt:yyyy-MM-dd HH:mm:ss}";
        ReportTextBox.Text = _reportText;
        VisualTopologyCanvas.Document = _visualDocument;
        LegendList.ItemsSource = _visualDocument.Legend.Select(item => $"{item.Label}: {item.Description}").ToList();
        VisualNotesList.ItemsSource = _visualDocument.Notes.Select(note => note.Text).ToList();
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

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VisualTopologyCanvas is null || ZoomText is null)
        {
            return;
        }

        VisualTopologyCanvas.Zoom = e.NewValue;
        ZoomText.Text = $"{e.NewValue:P0}";
    }

    private void FitButton_Click(object sender, RoutedEventArgs e)
    {
        ZoomSlider.Value = 1.0;
        StatusText.Text = "Visual map zoom reset to 100%.";
    }

    private void VisualTopologyCanvas_ItemSelected(object? sender, TopologyItemSelectedEventArgs e)
    {
        VisualTopologyCanvas.HighlightedItemIds = BuildHighlightSet(e.ItemId);
        SelectedItemText.Text = FormatSelectedItem(e.ItemId);
        StatusText.Text = $"Selected {e.Kind}: {e.ItemId}";
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

    private IReadOnlyCollection<string> BuildHighlightSet(string itemId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { itemId };
        var node = _visualDocument.Nodes.FirstOrDefault(node => node.Id == itemId);
        if (node is not null)
        {
            foreach (var relatedId in node.RelatedIds)
            {
                result.Add(relatedId);
            }

            return result;
        }

        var group = _visualDocument.Groups.FirstOrDefault(group => group.Id == itemId);
        if (group is not null)
        {
            AddGroupAndChildren(group, result);
        }

        return result;
    }

    private void AddGroupAndChildren(TopologyGroup group, ISet<string> result)
    {
        result.Add(group.Id);
        foreach (var nodeId in group.NodeIds)
        {
            result.Add(nodeId);
        }

        foreach (var childGroupId in group.ChildGroupIds)
        {
            result.Add(childGroupId);
            var childGroup = _visualDocument.Groups.FirstOrDefault(candidate => candidate.Id == childGroupId);
            if (childGroup is not null)
            {
                AddGroupAndChildren(childGroup, result);
            }
        }
    }

    private string FormatSelectedItem(string itemId)
    {
        var node = _visualDocument.Nodes.FirstOrDefault(node => node.Id == itemId);
        if (node is not null)
        {
            return FormatProperties(node.Label, node.Subtitle, node.Properties);
        }

        var group = _visualDocument.Groups.FirstOrDefault(group => group.Id == itemId);
        if (group is not null)
        {
            var details = new Dictionary<string, string>(group.Properties)
            {
                ["Nodes"] = group.NodeIds.Count.ToString(),
                ["Child groups"] = group.ChildGroupIds.Count.ToString()
            };
            var badges = group.Badges.Count == 0 ? null : string.Join(", ", group.Badges.Select(badge => badge.Text));
            return FormatProperties(group.Label, badges, details);
        }

        return itemId;
    }

    private static string FormatProperties(string title, string? subtitle, IReadOnlyDictionary<string, string> properties)
    {
        var lines = new List<string> { title };
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            lines.Add(subtitle);
        }

        foreach (var property in properties)
        {
            lines.Add($"{property.Key}: {property.Value}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
