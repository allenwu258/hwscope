using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HwScope.Core.Hardware;
using Wpf.Ui.Controls;

namespace HwScope.App.Pages;

public partial class HardwareSummaryPage : UserControl
{
    private readonly HardwareCollector _collector = new();
    private SummaryViewMode _viewMode = SummaryViewMode.Card;
    private HardwareReport? _currentReport;

    public event EventHandler<HardwareReport?>? CurrentReportChanged;
    public event EventHandler<string>? StatusChanged;

    public HardwareSummaryPage()
    {
        InitializeComponent();
        ApplyViewMode();
        Loaded += (_, _) =>
        {
            if (_currentReport is null)
            {
                RefreshHardwareSummary();
            }
        };
    }

    public HardwareReport? CurrentReport => _currentReport;

    public void RefreshHardwareSummary()
    {
        SetSummaryBusyState(true);

        try
        {
            _currentReport = _collector.CollectSummary();
            var summaryItems = HardwareSummaryItem.FromReport(_currentReport);
            CardHardwareSummaryList.ItemsSource = summaryItems;
            ListHardwareSummaryList.ItemsSource = summaryItems;
            GeneratedAtText.Text = $"检测时间：{_currentReport.GeneratedAt:yyyy-MM-dd HH:mm:ss}";
            RaiseCurrentReportChanged();
            SetStatus("硬件检测完成。");
        }
        catch (Exception ex)
        {
            SetStatus($"硬件检测失败：{ex.Message}");
            System.Windows.MessageBox.Show(Window.GetWindow(this), ex.Message, "硬件检测失败", System.Windows.MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetSummaryBusyState(false);
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshHardwareSummary();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentReport is null)
        {
            return;
        }

        Clipboard.SetText(HardwareReportFormatter.FormatSummary(_currentReport));
        SetStatus("硬件摘要已复制到剪贴板。");
    }

    private void CardViewButton_Click(object sender, RoutedEventArgs e)
    {
        _viewMode = SummaryViewMode.Card;
        ApplyViewMode();
    }

    private void ListViewButton_Click(object sender, RoutedEventArgs e)
    {
        _viewMode = SummaryViewMode.List;
        ApplyViewMode();
    }

    private void SetSummaryBusyState(bool isBusy)
    {
        RefreshButton.IsEnabled = !isBusy;
        CopyButton.IsEnabled = !isBusy && _currentReport is not null;
        Mouse.OverrideCursor = isBusy ? Cursors.Wait : null;
    }

    private void ApplyViewMode()
    {
        var isCardView = _viewMode == SummaryViewMode.Card;
        CardSummaryPanel.Visibility = isCardView ? Visibility.Visible : Visibility.Collapsed;
        ListSummaryPanel.Visibility = isCardView ? Visibility.Collapsed : Visibility.Visible;
        CardViewButton.Background = isCardView ? (Brush)FindResource("HwScopeActiveViewBrush") : Brushes.Transparent;
        ListViewButton.Background = isCardView ? Brushes.Transparent : (Brush)FindResource("HwScopeActiveViewBrush");
    }

    private void SetStatus(string text)
    {
        StatusChanged?.Invoke(this, text);
    }

    private void RaiseCurrentReportChanged()
    {
        CurrentReportChanged?.Invoke(this, _currentReport);
    }

    private enum SummaryViewMode
    {
        Card,
        List
    }
}

public sealed record HardwareSummaryItem(string Label, string Value, SymbolRegular Icon)
{
    public static IReadOnlyList<HardwareSummaryItem> FromReport(HardwareReport report)
    {
        return
        [
            new("处理器", report.Processor, SymbolRegular.DeveloperBoard20),
            new("主板", report.Motherboard, SymbolRegular.Board20),
            new("内存", report.Memory, SymbolRegular.Database24),
            new("显卡", report.Graphics, SymbolRegular.DesktopPulse24),
            new("显示器", report.Display, SymbolRegular.Desktop20),
            new("硬盘", report.Disk, SymbolRegular.HardDrive20),
            new("声卡", string.Join(Environment.NewLine, report.Audio), SymbolRegular.DesktopSpeaker20),
            new("网卡", report.Network, SymbolRegular.WifiSettings20),
        ];
    }
}
