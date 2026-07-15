using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using HwScope.App.Topology.Layout;
using HwScope.App.Topology.Model;

namespace HwScope.App.Topology.Controls;

public partial class TopologyCanvas : UserControl
{
    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document),
        typeof(TopologyDocument),
        typeof(TopologyCanvas),
        new PropertyMetadata(TopologyDocument.Empty, OnVisualPropertyChanged));

    public static readonly DependencyProperty DensityProperty = DependencyProperty.Register(
        nameof(Density),
        typeof(TopologyDensity),
        typeof(TopologyCanvas),
        new PropertyMetadata(TopologyDensity.Detailed, OnVisualPropertyChanged));

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom),
        typeof(double),
        typeof(TopologyCanvas),
        new PropertyMetadata(1.0, OnZoomChanged));

    public static readonly DependencyProperty SelectedItemIdProperty = DependencyProperty.Register(
        nameof(SelectedItemId),
        typeof(string),
        typeof(TopologyCanvas),
        new PropertyMetadata(null, OnVisualPropertyChanged));

    public static readonly DependencyProperty HighlightedItemIdsProperty = DependencyProperty.Register(
        nameof(HighlightedItemIds),
        typeof(IReadOnlyCollection<string>),
        typeof(TopologyCanvas),
        new PropertyMetadata(Array.Empty<string>(), OnVisualPropertyChanged));

    public static readonly DependencyProperty LayoutModeProperty = DependencyProperty.Register(
        nameof(LayoutMode),
        typeof(TopologyLayoutMode),
        typeof(TopologyCanvas),
        new PropertyMetadata(TopologyLayoutMode.NestedDomains, OnVisualPropertyChanged));

    private readonly ITopologyLayoutEngine _nestedLayoutEngine = new NestedDomainLayoutEngine();
    private readonly ITopologyLayoutEngine _hierarchicalLayoutEngine = new HierarchicalTopologyLayoutEngine();
    private TopologyLayoutResult _layout = TopologyLayoutResult.Empty;

    public event EventHandler<TopologyItemSelectedEventArgs>? ItemSelected;

    public event EventHandler<TopologyExpansionToggledEventArgs>? ExpansionToggled;

    public TopologyCanvas()
    {
        InitializeComponent();
        Loaded += (_, _) => Render();
        SizeChanged += (_, _) => Render();
    }

    public TopologyDocument Document
    {
        get => (TopologyDocument)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public TopologyDensity Density
    {
        get => (TopologyDensity)GetValue(DensityProperty);
        set => SetValue(DensityProperty, value);
    }

    public double Zoom
    {
        get => (double)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public string? SelectedItemId
    {
        get => (string?)GetValue(SelectedItemIdProperty);
        set => SetValue(SelectedItemIdProperty, value);
    }

    public IReadOnlyCollection<string> HighlightedItemIds
    {
        get => (IReadOnlyCollection<string>)GetValue(HighlightedItemIdsProperty);
        set => SetValue(HighlightedItemIdsProperty, value);
    }

    public TopologyLayoutMode LayoutMode
    {
        get => (TopologyLayoutMode)GetValue(LayoutModeProperty);
        set => SetValue(LayoutModeProperty, value);
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((TopologyCanvas)d).Render();
    }

    private static void OnZoomChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (TopologyCanvas)d;
        var zoom = Math.Clamp((double)e.NewValue, 0.3, 3.0);
        canvas.ZoomTransform.ScaleX = zoom;
        canvas.ZoomTransform.ScaleY = zoom;
    }

    private void Render()
    {
        if (!IsLoaded)
        {
            return;
        }

        DrawingCanvas.Children.Clear();

        var document = Document ?? TopologyDocument.Empty;
        var availableWidth = ActualWidth > 0 ? ActualWidth - 36 : 900;
        var layoutEngine = LayoutMode == TopologyLayoutMode.Hierarchical
            ? _hierarchicalLayoutEngine
            : _nestedLayoutEngine;
        _layout = layoutEngine.Layout(document, TopologyLayoutOptions.Default with
        {
            AvailableWidth = Math.Max(360, availableWidth),
            Density = Density
        });

        DrawingCanvas.Width = _layout.CanvasSize.Width;
        DrawingCanvas.Height = _layout.CanvasSize.Height;

        DrawEdges(document);
        DrawGroups(document);
        DrawNodes(document);
    }

    private void DrawEdges(TopologyDocument document)
    {
        foreach (var edge in document.Edges)
        {
            if (!_layout.EdgePorts.TryGetValue(edge.FromId, out var from) || !_layout.EdgePorts.TryGetValue(edge.ToId, out var to))
            {
                continue;
            }

            var line = new Line
            {
                X1 = from.X,
                Y1 = from.Y,
                X2 = to.X,
                Y2 = to.Y,
                Stroke = ResolveBrush(edge.Style.AccentKey),
                StrokeThickness = IsSelected(edge.Id) ? 2.4 : 1.2,
                Opacity = edge.Style.Opacity
            };

            if (edge.Style.IsDashed)
            {
                line.StrokeDashArray = [4, 3];
            }

            DrawingCanvas.Children.Add(line);
        }
    }

    private void DrawGroups(TopologyDocument document)
    {
        foreach (var group in document.Groups.OrderBy(group => Depth(group, document.Groups)))
        {
            if (!_layout.GroupBounds.TryGetValue(group.Id, out var bounds))
            {
                continue;
            }

            var border = new Border
            {
                Width = bounds.Width,
                Height = bounds.Height,
                Background = ResolveBackground(group.Style.AccentKey),
                BorderBrush = ResolveBrush(group.Style.AccentKey),
                BorderThickness = IsSelected(group.Id) ? new Thickness(2.4) : new Thickness(1.2),
                CornerRadius = new CornerRadius(7),
                Opacity = ResolveOpacity(group.Id, group.Style.Opacity),
                Tag = group.Id
            };

            if (group.Style.IsDashed || group.IsHeuristic)
            {
                border.BorderBrush = ResolveBrush(TopologyAccentKeys.Heuristic);
            }

            border.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                SelectItem(group.Id, group.Kind);
            };

            var panel = new StackPanel { Margin = new Thickness(10, 8, 10, 0) };
            panel.Children.Add(new TextBlock
            {
                Text = group.Label,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = TryResourceBrush("HwScopeStrongTextBrush", Brushes.Black),
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            if (group.Badges.Count > 0)
            {
                var badges = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
                foreach (var badge in group.Badges)
                {
                    badges.Children.Add(CreateBadge(badge.Text, badge.Style));
                }

                panel.Children.Add(badges);
            }

            border.Child = panel;
            Canvas.SetLeft(border, bounds.Left);
            Canvas.SetTop(border, bounds.Top);
            DrawingCanvas.Children.Add(border);
        }
    }

    private void DrawNodes(TopologyDocument document)
    {
        var nodeById = document.Nodes.ToDictionary(node => node.Id);
        foreach (var pair in _layout.NodeBounds)
        {
            if (!nodeById.TryGetValue(pair.Key, out var node))
            {
                continue;
            }

            var bounds = pair.Value;
            var border = new Border
            {
                Width = bounds.Width,
                Height = bounds.Height,
                Padding = new Thickness(9, 7, 9, 7),
                Background = TryResourceBrush("HwScopeCardBrush", Brushes.White),
                BorderBrush = ResolveBrush(node.Style.AccentKey),
                BorderThickness = IsSelected(node.Id) ? new Thickness(2.4) : new Thickness(1.2),
                CornerRadius = new CornerRadius(6),
                Opacity = ResolveOpacity(node.Id, node.Style.Opacity),
                ToolTip = BuildTooltip(node),
                Tag = node.Id
            };

            if (node.Style.IsDashed)
            {
                border.BorderBrush = ResolveBrush(TopologyAccentKeys.Heuristic);
            }

            border.MouseLeftButtonDown += (_, e) =>
            {
                if (IsInsideButton(e.OriginalSource as DependencyObject, border))
                {
                    return;
                }

                e.Handled = true;
                SelectItem(node.Id, node.Kind);
            };

            border.Child = CreateNodeContent(node);
            Canvas.SetLeft(border, bounds.Left);
            Canvas.SetTop(border, bounds.Top);
            DrawingCanvas.Children.Add(border);
        }
    }

    private UIElement CreateNodeContent(TopologyNode node)
    {
        var panel = new StackPanel();
        var header = new DockPanel { LastChildFill = true };
        if (node.CanExpand)
        {
            var expansionButton = new Button
            {
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 5, 0),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                Content = node.IsExpanded ? "▾" : "▸",
                ToolTip = node.IsExpanded ? "收起分支" : $"展开分支（隐藏 {node.HiddenChildCount} 个节点）"
            };
            expansionButton.Click += (_, e) =>
            {
                e.Handled = true;
                ExpansionToggled?.Invoke(this, new TopologyExpansionToggledEventArgs(node.Id, !node.IsExpanded));
            };
            DockPanel.SetDock(expansionButton, Dock.Left);
            header.Children.Add(expansionButton);
        }

        header.Children.Add(new TextBlock
        {
            Text = node.Label,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = TryResourceBrush("HwScopeStrongTextBrush", Brushes.Black),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        panel.Children.Add(header);

        if (!string.IsNullOrWhiteSpace(node.Subtitle))
        {
            panel.Children.Add(new TextBlock
            {
                Text = node.Subtitle,
                Margin = new Thickness(0, 3, 0, 0),
                FontSize = 11,
                Foreground = TryResourceBrush("HwScopeMutedTextBrush", Brushes.DimGray),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        foreach (var property in node.Properties.Take(Density == TopologyDensity.Compact ? 2 : 4))
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"{property.Key}: {property.Value}",
                Margin = new Thickness(0, 3, 0, 0),
                FontSize = 11,
                Foreground = TryResourceBrush("HwScopeTextBrush", Brushes.Black),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        return panel;
    }

    private Border CreateBadge(string text, TopologyStyle style)
    {
        return new Border
        {
            Margin = new Thickness(0, 0, 5, 4),
            Padding = new Thickness(6, 2, 6, 2),
            CornerRadius = new CornerRadius(4),
            Background = ResolveBackground(style.AccentKey),
            BorderBrush = ResolveBrush(style.AccentKey),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10,
                Foreground = TryResourceBrush("HwScopeTextBrush", Brushes.Black)
            }
        };
    }

    private string BuildTooltip(TopologyNode node)
    {
        var lines = new List<string> { node.Label };
        if (!string.IsNullOrWhiteSpace(node.Subtitle))
        {
            lines.Add(node.Subtitle);
        }

        lines.AddRange(node.Properties.Select(property => $"{property.Key}: {property.Value}"));
        return string.Join(Environment.NewLine, lines);
    }

    private void SelectItem(string id, string kind)
    {
        SelectedItemId = id;
        ItemSelected?.Invoke(this, new TopologyItemSelectedEventArgs(id, kind));
    }

    private static bool IsInsideButton(DependencyObject? source, DependencyObject boundary)
    {
        var current = source;
        while (current is not null && current != boundary)
        {
            if (current is Button)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private bool IsSelected(string id)
    {
        return string.Equals(SelectedItemId, id, StringComparison.Ordinal);
    }

    private double ResolveOpacity(string id, double defaultOpacity)
    {
        if (HighlightedItemIds.Count == 0 || HighlightedItemIds.Contains(id) || IsSelected(id))
        {
            return defaultOpacity;
        }

        return Math.Min(defaultOpacity, 0.32);
    }

    private static int Depth(TopologyGroup group, IReadOnlyList<TopologyGroup> groups)
    {
        var byId = groups.ToDictionary(item => item.Id);
        var depth = 0;
        var current = group;
        while (!string.IsNullOrWhiteSpace(current.ParentGroupId) && byId.TryGetValue(current.ParentGroupId, out var parent))
        {
            depth++;
            current = parent;
        }

        return depth;
    }

    private Brush ResolveBrush(string accentKey)
    {
        return accentKey switch
        {
            TopologyAccentKeys.CacheL1Data => new SolidColorBrush(Color.FromRgb(0x34, 0x98, 0xDB)),
            TopologyAccentKeys.CacheL1Instruction => new SolidColorBrush(Color.FromRgb(0x9B, 0x59, 0xB6)),
            TopologyAccentKeys.CacheL2 => new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71)),
            TopologyAccentKeys.CacheL3 => new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22)),
            TopologyAccentKeys.CacheL3VCache => new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C)),
            TopologyAccentKeys.CorePerformance => new SolidColorBrush(Color.FromRgb(0x34, 0x98, 0xDB)),
            TopologyAccentKeys.CoreEfficiency => new SolidColorBrush(Color.FromRgb(0x95, 0xA5, 0xA6)),
            TopologyAccentKeys.Heuristic => new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C)),
            TopologyAccentKeys.GroupNuma => new SolidColorBrush(Color.FromRgb(0x5D, 0x78, 0xA6)),
            TopologyAccentKeys.GroupPcieRoot => new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71)),
            TopologyAccentKeys.DevicePcie => new SolidColorBrush(Color.FromRgb(0x7F, 0x8C, 0x8D)),
            TopologyAccentKeys.GroupUsbController => new SolidColorBrush(Color.FromRgb(0x26, 0x7A, 0x73)),
            TopologyAccentKeys.GroupUsbHub => new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
            TopologyAccentKeys.PortUsb => new SolidColorBrush(Color.FromRgb(0x78, 0x78, 0x78)),
            TopologyAccentKeys.DeviceUsb => new SolidColorBrush(Color.FromRgb(0xD0, 0x79, 0x27)),
            TopologyAccentKeys.GroupPackage => new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
            _ => TryResourceBrush("HwScopeLineBrush", Brushes.Gray)
        };
    }

    private Brush ResolveBackground(string accentKey)
    {
        var brush = ResolveBrush(accentKey).Clone();
        brush.Opacity = 0.12;
        return brush;
    }

    private Brush TryResourceBrush(string resourceKey, Brush fallback)
    {
        return TryFindResource(resourceKey) as Brush ?? fallback;
    }
}

public sealed class TopologyItemSelectedEventArgs(string itemId, string kind) : EventArgs
{
    public string ItemId { get; } = itemId;

    public string Kind { get; } = kind;
}

public sealed class TopologyExpansionToggledEventArgs(string itemId, bool isExpanded) : EventArgs
{
    public string ItemId { get; } = itemId;

    public bool IsExpanded { get; } = isExpanded;
}
