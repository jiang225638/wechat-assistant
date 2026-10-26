using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using WeChatCopilot.Core.Models;

namespace WeChatCopilot.App;

/// <summary>
/// 话语力量与博弈推演全景大看板：
/// 基于【终极目的 × 对方画像 × 作战Skill × 用户意图】的动态非硬编码立体研判，
/// 在一屏全景内融合动态维度重叠雷达图、维度分组量化对决柱状图与战略博弈双轴象限图，
/// 让用户直观洞察话语力量与战略推进力。
/// </summary>
public partial class AdviceAnalyticsWindow : Window
{
    private UnifiedAiAdvice _advice;
    private readonly Persona? _persona;
    private readonly DistillSkill _skill;
    private readonly string _contactName;
    private readonly string _ultimateGoal;
    private readonly string _userIntent;
    private List<string> _dynamicDims;
    private readonly Func<CancellationToken, Task<UnifiedAiAdvice?>>? _regenerateCallback;
    private CancellationTokenSource? _regenCts;

    private int _selectedOptionIndex = -1;

    // 候选方案调色板体系 (方案1: 流光蓝, 方案2: 翡翠绿, 方案3: 紫罗兰, 方案4: 琥珀橙, 方案5: 蔷薇红)
    private static readonly Color[] OptionColors =
    {
        Color.FromRgb(56, 189, 248),   // #38BDF8 流光天蓝
        Color.FromRgb(16, 185, 129),  // #10B981 翡翠绿
        Color.FromRgb(168, 85, 247),  // #A855F7 霓虹紫
        Color.FromRgb(245, 158, 11),  // #F59E0B 琥珀橙
        Color.FromRgb(244, 63, 94)    // #F43F5E 蔷薇红
    };

    public AdviceAnalyticsWindow(
        UnifiedAiAdvice advice,
        Persona? persona = null,
        DistillSkill? skill = null,
        string? contactName = null,
        string? ultimateGoal = null,
        string? userIntent = null,
        Func<CancellationToken, Task<UnifiedAiAdvice?>>? regenerateCallback = null)
    {
        InitializeComponent();

        _advice = advice ?? throw new ArgumentNullException(nameof(advice));
        _persona = persona;
        _skill = skill ?? DistillSkillPresets.Intimacy;
        _contactName = !string.IsNullOrWhiteSpace(contactName)
            ? contactName
            : (_persona?.ContactName ?? "对方");
        _ultimateGoal = !string.IsNullOrWhiteSpace(ultimateGoal)
            ? ultimateGoal
            : (_persona?.UltimateGoal ?? "推进双方深度交流与共识达成");
        _userIntent = !string.IsNullOrWhiteSpace(userIntent)
            ? userIntent
            : "顺承当前语境，推进战略目标";

        // 提取动态评估维度
        _dynamicDims = ResolveDynamicDimensions();
        _regenerateCallback = regenerateCallback;
        Closed += (_, _) => { _regenCts?.Cancel(); _regenCts?.Dispose(); };

        ThemeManager.ApplyCurrentTheme(this);
        Loaded += AdviceAnalyticsWindow_Loaded;
    }

    private List<string> ResolveDynamicDimensions()
    {
        var dims = new List<string>();
        if (_advice.DynamicDimensions != null && _advice.DynamicDimensions.Count > 0)
        {
            foreach (var d in _advice.DynamicDimensions)
            {
                if (!string.IsNullOrWhiteSpace(d) && !dims.Contains(d))
                {
                    dims.Add(d.Trim());
                }
            }
        }

        // 若仍未满 3 个维度，从候选回复中采集
        if (dims.Count < 3 && _advice.Suggestions != null)
        {
            foreach (var s in _advice.Suggestions)
            {
                if (s.DimensionScores != null)
                {
                    foreach (var k in s.DimensionScores.Keys)
                    {
                        if (!string.IsNullOrWhiteSpace(k) && !dims.Contains(k.Trim()))
                        {
                            dims.Add(k.Trim());
                        }
                    }
                }
            }
        }

        // 仅在完全无维度输入时回退到有真实分值对应的两项（推进力 / 安全度），不再虚构其他维度
        if (dims.Count == 0)
        {
            dims.AddRange(new[] { "目标推进力", "画像安全度" });
        }
        else if (dims.Count < 3)
        {
            if (!dims.Contains("目标推进力")) dims.Add("目标推进力");
            if (!dims.Contains("画像防御度")) dims.Add("画像防御度");
        }

        return dims;
    }

    private void AdviceAnalyticsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        PopulateHeader();
        PopulateSandtable();
        PopulateRadarLegend();
        DrawMultiRadarChart();
        PopulateGroupedBarChart();
        DrawQuadrantMatrix();
        PopulateSuggestionsCards();
        UpdateDefaultBottomStatus();
        if (_regenerateCallback == null && RegenerateAdviceButton != null)
        {
            RegenerateAdviceButton.Visibility = Visibility.Collapsed;
        }
    }

    public void OnThemeChanged()
    {
        if (!IsLoaded) return;
        PopulateRadarLegend();
        DrawMultiRadarChart();
        PopulateGroupedBarChart();
        DrawQuadrantMatrix();
        PopulateSuggestionsCards();
    }

    private void RadarHostGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded || e.NewSize.Width < 180 || e.NewSize.Height < 140) return;
        RadarCanvas.Width = Math.Max(260, e.NewSize.Width - 8);
        RadarCanvas.Height = Math.Max(180, e.NewSize.Height - 4);
        DrawMultiRadarChart();
    }

    private void QuadrantHostGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded || e.NewSize.Width < 160 || e.NewSize.Height < 140) return;
        QuadrantCanvas.Width = Math.Max(220, e.NewSize.Width - 8);
        QuadrantCanvas.Height = Math.Max(180, e.NewSize.Height - 4);
        DrawQuadrantMatrix();
    }

    private async void RegenerateAdviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_regenerateCallback == null) return;

        if (_regenCts != null)
        {
            _regenCts.Cancel();
            return;
        }

        _regenCts = new CancellationTokenSource();
        RegenerateAdviceButton.Content = "⏹ 停止生成";
        BottomStatusText.Text = "⏳ 正在结合当前对话、画像、技能与意图重新调用 AI 推演方案（再次点击可停止）...";

        try
        {
            var newAdvice = await _regenerateCallback(_regenCts.Token);
            if (newAdvice != null)
            {
                _advice = newAdvice;
                _selectedOptionIndex = -1;
                _dynamicDims = ResolveDynamicDimensions();
                PopulateSandtable();
                PopulateRadarLegend();
                DrawMultiRadarChart();
                PopulateGroupedBarChart();
                DrawQuadrantMatrix();
                PopulateSuggestionsCards();
                BottomStatusText.Text = $"✅ AI 已于 {DateTime.Now:HH:mm:ss} 重新生成最新推演方案与量化评分";
            }
            else
            {
                BottomStatusText.Text = "⚠️ 本次重新生成未返回有效结果，已保留当前方案";
            }
        }
        catch (OperationCanceledException)
        {
            BottomStatusText.Text = "⏹ 已取消本次重新生成";
        }
        catch (Exception ex)
        {
            BottomStatusText.Text = $"❌ 重新生成失败：{ex.Message}";
        }
        finally
        {
            _regenCts?.Dispose();
            _regenCts = null;
            RegenerateAdviceButton.Content = "🔄 AI 重新生成";
        }
    }

    // ===================== 顶部 Header 填充 =====================

    private void PopulateHeader()
    {
        HeaderContactName.Text = _contactName;
        HeaderGoalText.Text = _ultimateGoal;
        HeaderSkillText.Text = $"{_skill.Icon} {_skill.Name}";

        if (!string.IsNullOrWhiteSpace(_userIntent))
        {
            HeaderIntentBorder.Visibility = Visibility.Visible;
            HeaderIntentText.Text = _userIntent;
        }
        else
        {
            HeaderIntentBorder.Visibility = Visibility.Collapsed;
        }
    }

    // ===================== 左侧沙盘推演流填充 =====================

    private void PopulateSandtable()
    {
        SandtableTargetQuoteText.Text = !string.IsNullOrWhiteSpace(_advice.TargetQuote)
            ? $"“{_advice.TargetQuote}”"
            : "（无具体原话）";

        SandtableContextFlowText.Text = !string.IsNullOrWhiteSpace(_advice.ContextFlowSummary)
            ? _advice.ContextFlowSummary
            : "（AI 本次未返回战局脉络）";

        SandtableSubtextText.Text = !string.IsNullOrWhiteSpace(_advice.Subtext)
            ? _advice.Subtext
            : (!string.IsNullOrWhiteSpace(_advice.Literal) ? _advice.Literal : "（AI 本次未返回潜台词洞察）");

        SandtableIntentText.Text = !string.IsNullOrWhiteSpace(_advice.Intent)
            ? _advice.Intent
            : "（AI 本次未返回对方真实动机）";

        SandtableStrategyTitle.Text = $"⚡ {_skill.Name} 破局心法与避坑指南";
        SandtableStrategyText.Text = !string.IsNullOrWhiteSpace(_advice.Strategy)
            ? _advice.Strategy
            : "（AI 本次未返回破局心法，可重新生成建议）";
    }

    // ===================== 图表 1: 多方案全景重叠雷达图 =====================

    private void PopulateRadarLegend()
    {
        RadarLegendPanel.Children.Clear();
        var suggestions = _advice.Suggestions ?? Array.Empty<ReplySuggestion>();
        for (int i = 0; i < suggestions.Count; i++)
        {
            var s = suggestions[i];
            var col = OptionColors[i % OptionColors.Length];

            var btn = new Button
            {
                Margin = new Thickness(4, 2, 4, 2),
                Padding = new Thickness(8, 2, 8, 2),
                Background = new SolidColorBrush(Color.FromArgb(30, col.R, col.G, col.B)),
                BorderBrush = new SolidColorBrush(col),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Tag = i
            };

            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(col),
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var txt = new TextBlock
            {
                Text = $"方案{i + 1}: {s.Tone}",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };

            sp.Children.Add(dot);
            sp.Children.Add(txt);
            btn.Content = sp;
            btn.Click += LegendButton_Click;

            RadarLegendPanel.Children.Add(btn);
        }
    }

    private void LegendButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is int idx)
        {
            SelectOption(idx == _selectedOptionIndex ? -1 : idx);
        }
    }

    private void DrawMultiRadarChart()
    {
        RadarCanvas.Children.Clear();
        int dimCount = _dynamicDims.Count;
        if (dimCount < 3) return;

        double w = RadarCanvas.Width > 0 ? RadarCanvas.Width : 360;
        double h = RadarCanvas.Height > 0 ? RadarCanvas.Height : 240;
        double cx = w / 2.0;
        double cy = h / 2.0;
        double maxR = Math.Min(cx, cy) - 34;
        if (maxR <= 20) maxR = 80;

        double angleStep = 2 * Math.PI / dimCount;
        double startAngle = -Math.PI / 2.0;

        // 1. 绘制 5 层同心多边形背景网格 (20%, 40%, 60%, 80%, 100%)
        int gridLevels = 5;
        for (int level = 1; level <= gridLevels; level++)
        {
            double r = maxR * level / gridLevels;
            var ring = new Polygon
            {
                Stroke = (Brush)FindResource("BorderLightBrush"),
                StrokeThickness = level == gridLevels ? 1.2 : 0.7,
                StrokeDashArray = level < gridLevels ? new DoubleCollection { 2, 2 } : null,
                Fill = level % 2 == 0 ? new SolidColorBrush(Color.FromArgb(12, 255, 255, 255)) : Brushes.Transparent
            };

            for (int i = 0; i < dimCount; i++)
            {
                double angle = startAngle + i * angleStep;
                ring.Points.Add(new Point(cx + r * Math.Cos(angle), cy + r * Math.Sin(angle)));
            }

            RadarCanvas.Children.Add(ring);
        }

        // 2. 绘制各维度轴向射线
        for (int i = 0; i < dimCount; i++)
        {
            double angle = startAngle + i * angleStep;
            var line = new Line
            {
                X1 = cx,
                Y1 = cy,
                X2 = cx + maxR * Math.Cos(angle),
                Y2 = cy + maxR * Math.Sin(angle),
                Stroke = (Brush)FindResource("BorderSubtleBrush"),
                StrokeThickness = 1
            };
            RadarCanvas.Children.Add(line);
        }

        // 3. 绘制外周动态维度文字标签
        double labelR = maxR + 18;
        for (int i = 0; i < dimCount; i++)
        {
            double angle = startAngle + i * angleStep;
            double lx = cx + labelR * Math.Cos(angle);
            double ly = cy + labelR * Math.Sin(angle);

            string dimName = _dynamicDims[i];
            var tb = new TextBlock
            {
                Text = dimName,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                TextAlignment = TextAlignment.Center,
                Width = 72
            };

            Canvas.SetLeft(tb, lx - 36);
            Canvas.SetTop(tb, ly - 8);
            RadarCanvas.Children.Add(tb);
        }

        // 4. 重叠绘制各候选回复方案的多边形数据
        var suggestions = _advice.Suggestions ?? Array.Empty<ReplySuggestion>();
        for (int idx = 0; idx < suggestions.Count; idx++)
        {
            var s = suggestions[idx];
            var col = OptionColors[idx % OptionColors.Length];
            bool isSelected = (_selectedOptionIndex == idx);
            bool isDimmed = (_selectedOptionIndex != -1 && !isSelected);

            byte alpha = isSelected ? (byte)90 : (isDimmed ? (byte)15 : (byte)35);
            double strokeThick = isSelected ? 3.0 : (isDimmed ? 1.0 : 1.8);

            var poly = new Polygon
            {
                Stroke = new SolidColorBrush(col),
                StrokeThickness = strokeThick,
                Fill = new SolidColorBrush(Color.FromArgb(alpha, col.R, col.G, col.B)),
                Tag = idx,
                Cursor = Cursors.Hand
            };

            var dataPoints = new List<Point>();
            for (int d = 0; d < dimCount; d++)
            {
                string dimName = _dynamicDims[d];
                double? score = GetScoreForDimension(s, dimName, idx);
                double r = score.HasValue ? maxR * Math.Clamp(score.Value, 10, 100) / 100.0 : 0;
                double angle = startAngle + d * angleStep;
                var pt = new Point(cx + r * Math.Cos(angle), cy + r * Math.Sin(angle));
                poly.Points.Add(pt);
                dataPoints.Add(pt);
            }

            poly.MouseLeftButtonDown += (sender, e) =>
            {
                if (sender is Polygon p && p.Tag is int optIdx)
                {
                    SelectOption(optIdx == _selectedOptionIndex ? -1 : optIdx);
                }
            };
            RadarCanvas.Children.Add(poly);

            // 绘制各顶点高光圆点
            if (!isDimmed)
            {
                foreach (var pt in dataPoints)
                {
                    var dot = new Ellipse
                    {
                        Width = isSelected ? 7 : 5,
                        Height = isSelected ? 7 : 5,
                        Fill = new SolidColorBrush(col),
                        Stroke = Brushes.White,
                        StrokeThickness = 1.2
                    };
                    Canvas.SetLeft(dot, pt.X - (isSelected ? 3.5 : 2.5));
                    Canvas.SetTop(dot, pt.Y - (isSelected ? 3.5 : 2.5));
                    RadarCanvas.Children.Add(dot);
                }
            }
        }
    }

    /// <summary>
    /// 取方案在某维度上的得分；模型未给出时返回 null（界面显示“—”，不伪造数值）。
    /// </summary>
    private double? GetScoreForDimension(ReplySuggestion s, string dim, int optIndex)
    {
        if (s.DimensionScores != null)
        {
            foreach (var kvp in s.DimensionScores)
            {
                if (string.Equals(kvp.Key, dim, StringComparison.OrdinalIgnoreCase) ||
                    kvp.Key.Contains(dim) || dim.Contains(kvp.Key))
                {
                    return kvp.Value;
                }
            }
        }

        if ((dim.Contains("推进") || dim.Contains("目标")) && s.HasGoalPushScore) return s.GoalPushScore;
        if ((dim.Contains("安全") || dim.Contains("防") || dim.Contains("契合")) && s.HasPersonaSafetyScore) return s.PersonaSafetyScore;

        return null;
    }

    private static string FormatScore(double? score) => score.HasValue ? score.Value.ToString("0") : "—";

    // ===================== 图表 2: 动态维度分组量化对决柱状图 =====================

    private void PopulateGroupedBarChart()
    {
        BarChartContainer.Children.Clear();
        var suggestions = _advice.Suggestions ?? Array.Empty<ReplySuggestion>();
        if (suggestions.Count == 0) return;

        foreach (var dim in _dynamicDims)
        {
            var card = new Border
            {
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(8, 6, 8, 6),
                Background = (Brush)FindResource("CardSubtleBgBrush"),
                BorderBrush = (Brush)FindResource("BorderSubtleBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6)
            };

            var sp = new StackPanel();

            // 维度名称
            var title = new TextBlock
            {
                Text = $"【{dim}】",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                Margin = new Thickness(0, 0, 0, 5)
            };
            sp.Children.Add(title);

            // 各方案对比条
            for (int i = 0; i < suggestions.Count; i++)
            {
                var s = suggestions[i];
                var col = OptionColors[i % OptionColors.Length];
                double? score = GetScoreForDimension(s, dim, i);
                bool isSelected = (_selectedOptionIndex == i);

                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });

                // 方案标签
                var optLabel = new TextBlock
                {
                    Text = $"方案{i + 1}",
                    FontSize = 10,
                    FontWeight = isSelected ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = isSelected ? new SolidColorBrush(col) : (Brush)FindResource("TextSecondaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(optLabel, 0);
                row.Children.Add(optLabel);

                // 柱状条轨道
                var track = new Border
                {
                    Height = isSelected ? 12 : 10,
                    Background = (Brush)FindResource("BorderSubtleBrush"),
                    CornerRadius = new CornerRadius(4),
                    VerticalAlignment = VerticalAlignment.Center
                };

                var trackGrid = new Grid();
                double pct = score.HasValue ? Math.Clamp(score.Value, 0, 100) : 0;
                trackGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(pct, GridUnitType.Star) });
                trackGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, 100 - pct), GridUnitType.Star) });

                var fillBar = new Border
                {
                    Height = isSelected ? 12 : 10,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(col),
                    CornerRadius = new CornerRadius(4)
                };
                Grid.SetColumn(fillBar, 0);
                trackGrid.Children.Add(fillBar);
                track.Child = trackGrid;
                Grid.SetColumn(track, 1);
                row.Children.Add(track);

                // 分数文本
                var scoreText = new TextBlock
                {
                    Text = score.HasValue ? $"{score.Value:0}分" : "—",
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(col),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(scoreText, 2);
                row.Children.Add(scoreText);

                sp.Children.Add(row);
            }

            card.Child = sp;
            BarChartContainer.Children.Add(card);
        }
    }

    // ===================== 图表 3: 战略博弈双轴象限图 =====================

    private void DrawQuadrantMatrix()
    {
        QuadrantCanvas.Children.Clear();

        double w = QuadrantCanvas.Width > 0 ? QuadrantCanvas.Width : 280;
        double h = QuadrantCanvas.Height > 0 ? QuadrantCanvas.Height : 255;

        double padL = 36;
        double padR = 20;
        double padT = 20;
        double padB = 30;

        double plotW = w - padL - padR;
        double plotH = h - padT - padB;
        if (plotW <= 50 || plotH <= 50) return;

        double midX = padL + plotW / 2.0;
        double midY = padT + plotH / 2.0;

        bool isLight = ThemeManager.IsLightMode;

        // 1. 四象限背景微色调色块与高对比度文字
        // Q1 (右上): 🌟 黄金破局 (推进高，安全高)
        DrawQuadrantBox(midX, padT, plotW / 2.0, plotH / 2.0,
            isLight ? Color.FromArgb(28, 16, 185, 129) : Color.FromArgb(20, 16, 185, 129),
            "🌟 黄金破局",
            isLight ? Color.FromRgb(4, 120, 87) : Color.FromRgb(52, 211, 153));

        // Q2 (左上): ⚡ 高能突刺 (推进高，安全低)
        DrawQuadrantBox(padL, padT, plotW / 2.0, plotH / 2.0,
            isLight ? Color.FromArgb(28, 245, 158, 11) : Color.FromArgb(20, 245, 158, 11),
            "⚡ 高能突刺",
            isLight ? Color.FromRgb(180, 83, 9) : Color.FromRgb(251, 191, 36));

        // Q3 (左下): ⚠️ 谨慎踩雷 (推进低，安全低)
        DrawQuadrantBox(padL, midY, plotW / 2.0, plotH / 2.0,
            isLight ? Color.FromArgb(28, 244, 63, 94) : Color.FromArgb(20, 244, 63, 94),
            "⚠️ 谨慎踩雷",
            isLight ? Color.FromRgb(190, 18, 60) : Color.FromRgb(251, 113, 133));

        // Q4 (右下): 🛡️ 稳健防守 (推进低，安全高)
        DrawQuadrantBox(midX, midY, plotW / 2.0, plotH / 2.0,
            isLight ? Color.FromArgb(28, 56, 189, 248) : Color.FromArgb(20, 56, 189, 248),
            "🛡️ 稳健防守",
            isLight ? Color.FromRgb(3, 105, 161) : Color.FromRgb(56, 189, 248));

        // 2. 双轴中心十字线 (X=50, Y=50)
        var vLine = new Line
        {
            X1 = midX,
            Y1 = padT,
            X2 = midX,
            Y2 = padT + plotH,
            Stroke = (Brush)FindResource("BorderLightBrush"),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 2 }
        };
        QuadrantCanvas.Children.Add(vLine);

        var hLine = new Line
        {
            X1 = padL,
            Y1 = midY,
            X2 = padL + plotW,
            Y2 = midY,
            Stroke = (Brush)FindResource("BorderLightBrush"),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 2 }
        };
        QuadrantCanvas.Children.Add(hLine);

        // 3. 轴向标签
        // X 轴 (画像安全度)
        var xLabel = new TextBlock
        {
            Text = "画像安全度 (X) →",
            FontSize = 9.5,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Canvas.SetLeft(xLabel, w - padR - 85);
        Canvas.SetTop(xLabel, h - padB + 6);
        QuadrantCanvas.Children.Add(xLabel);

        // Y 轴 (目标推进力)
        var yLabel = new TextBlock
        {
            Text = "↑ 目标推进力 (Y)",
            FontSize = 9.5,
            Foreground = (Brush)FindResource("TextSecondaryBrush")
        };
        Canvas.SetLeft(yLabel, padL - 8);
        Canvas.SetTop(yLabel, 3);
        QuadrantCanvas.Children.Add(yLabel);

        // 4. 打点候选方案
        var suggestions = _advice.Suggestions ?? Array.Empty<ReplySuggestion>();
        for (int i = 0; i < suggestions.Count; i++)
        {
            var s = suggestions[i];
            if (!s.HasPersonaSafetyScore || !s.HasGoalPushScore) continue; // 缺分值的方案不打点，避免伪造位置
            var col = OptionColors[i % OptionColors.Length];
            bool isSelected = (_selectedOptionIndex == i);

            double safe = Math.Clamp(s.PersonaSafetyScore, 10, 100);
            double push = Math.Clamp(s.GoalPushScore, 10, 100);

            double ptX = padL + (safe / 100.0) * plotW;
            double ptY = padT + ((100.0 - push) / 100.0) * plotH;

            // 选中时的大发光圈
            if (isSelected)
            {
                var halo = new Ellipse
                {
                    Width = 22,
                    Height = 22,
                    Fill = new SolidColorBrush(Color.FromArgb(50, col.R, col.G, col.B)),
                    Stroke = new SolidColorBrush(col),
                    StrokeThickness = 1.5
                };
                Canvas.SetLeft(halo, ptX - 11);
                Canvas.SetTop(halo, ptY - 11);
                QuadrantCanvas.Children.Add(halo);
            }

            var dot = new Ellipse
            {
                Width = isSelected ? 12 : 9,
                Height = isSelected ? 12 : 9,
                Fill = new SolidColorBrush(col),
                Stroke = Brushes.White,
                StrokeThickness = 1.5,
                Cursor = Cursors.Hand,
                Tag = i
            };
            Canvas.SetLeft(dot, ptX - (isSelected ? 6 : 4.5));
            Canvas.SetTop(dot, ptY - (isSelected ? 6 : 4.5));

            int optIdx = i;
            dot.MouseLeftButtonDown += (sender, e) => SelectOption(optIdx == _selectedOptionIndex ? -1 : optIdx);
            QuadrantCanvas.Children.Add(dot);

            // 方案文字标号
            var tag = new TextBlock
            {
                Text = $"方案{i + 1}",
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(col)
            };
            Canvas.SetLeft(tag, ptX + 7);
            Canvas.SetTop(tag, ptY - 7);
            QuadrantCanvas.Children.Add(tag);
        }
    }

    private void DrawQuadrantBox(double x, double y, double w, double h, Color tint, string label, Color labelColor)
    {
        var rect = new Rectangle
        {
            Width = w,
            Height = h,
            Fill = new SolidColorBrush(tint)
        };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        QuadrantCanvas.Children.Add(rect);

        var txt = new TextBlock
        {
            Text = label,
            FontSize = 9.5,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(labelColor)
        };
        Canvas.SetLeft(txt, x + 6);
        Canvas.SetTop(txt, y + 5);
        QuadrantCanvas.Children.Add(txt);
    }

    // ===================== 右侧回复卡片与对决推演 =====================

    private void PopulateSuggestionsCards()
    {
        SuggestionsCardsContainer.Children.Clear();
        var suggestions = _advice.Suggestions ?? Array.Empty<ReplySuggestion>();

        for (int i = 0; i < suggestions.Count; i++)
        {
            var s = suggestions[i];
            var col = OptionColors[i % OptionColors.Length];
            bool isSelected = (_selectedOptionIndex == i);

            var card = new Border
            {
                Margin = new Thickness(0, 0, 0, 10),
                Padding = new Thickness(12, 10, 12, 10),
                Background = isSelected
                    ? new SolidColorBrush(Color.FromArgb(35, col.R, col.G, col.B))
                    : (Brush)FindResource("CardSubtleBgBrush"),
                BorderBrush = isSelected
                    ? new SolidColorBrush(col)
                    : (Brush)FindResource("BorderLightBrush"),
                BorderThickness = new Thickness(isSelected ? 2 : 1),
                CornerRadius = new CornerRadius(8),
                Cursor = Cursors.Hand,
                Tag = i
            };

            int optIdx = i;
            card.MouseLeftButtonDown += (sender, e) =>
            {
                // 若点击不是复制按钮，则切换选中
                SelectOption(optIdx == _selectedOptionIndex ? -1 : optIdx);
            };

            var sp = new StackPanel();

            // 卡片 Header
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var badgesSp = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };

            // 方案序号徽章
            var idxBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(40, col.R, col.G, col.B)),
                BorderBrush = new SolidColorBrush(col),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 6, 2)
            };
            idxBorder.Child = new TextBlock
            {
                Text = $"方案 {i + 1}",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(col)
            };
            badgesSp.Children.Add(idxBorder);

            // 战术风格徽章
            var toneBorder = new Border
            {
                Background = (Brush)FindResource("BorderSubtleBrush"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 8, 2)
            };
            toneBorder.Child = new TextBlock
            {
                Text = s.Tone,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimaryBrush")
            };
            badgesSp.Children.Add(toneBorder);

            // 推进力分值徽章
            var pushBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(30, 16, 185, 129)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 6, 2)
            };
            pushBorder.Child = new TextBlock
            {
                Text = $"🚩 推进力 {s.GoalPushText}",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153))
            };
            badgesSp.Children.Add(pushBorder);

            // 安全度分值徽章
            var safeBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(30, 56, 189, 248)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 6, 2)
            };
            safeBorder.Child = new TextBlock
            {
                Text = $"🛡️ 安全度 {s.PersonaSafetyText}",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248))
            };
            badgesSp.Children.Add(safeBorder);

            Grid.SetColumn(badgesSp, 0);
            headerGrid.Children.Add(badgesSp);

            // 复制话术按钮
            var copyBtn = new Button
            {
                Content = "📋 复制话术",
                Style = (Style)FindResource("ActionBtnStyle"),
                Height = 26,
                Padding = new Thickness(10, 0, 10, 0),
                Tag = s.Text
            };
            copyBtn.Click += CopyButton_Click;

            var pasteBtn = new Button
            {
                Content = "📤 填入微信",
                Style = (Style)FindResource("ActionBtnStyle"),
                Height = 26,
                Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "一键复制到剪贴板并自动填入微信输入框（不自动发送）",
                Tag = s.Text
            };
            pasteBtn.Click += PasteButton_Click;

            var actionButtonsPanel = new StackPanel { Orientation = Orientation.Horizontal };
            actionButtonsPanel.Children.Add(pasteBtn);
            actionButtonsPanel.Children.Add(copyBtn);
            Grid.SetColumn(actionButtonsPanel, 1);
            headerGrid.Children.Add(actionButtonsPanel);

            sp.Children.Add(headerGrid);

            // 回复正文
            var replyText = new TextBlock
            {
                Text = s.Text,
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                LineHeight = 19,
                Margin = new Thickness(0, 8, 0, 6),
                TextWrapping = TextWrapping.Wrap
            };
            sp.Children.Add(replyText);

            // 战术依据 / 理由
            if (!string.IsNullOrWhiteSpace(s.Reason))
            {
                var reasonBorder = new Border
                {
                    Background = (Brush)FindResource("BorderSubtleBrush"),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(0, 2, 0, 6)
                };
                var reasonText = new TextBlock
                {
                    Text = $"💡 战术逻辑：{s.Reason}",
                    FontSize = 11,
                    Foreground = (Brush)FindResource("TextSecondaryBrush"),
                    LineHeight = 16,
                    TextWrapping = TextWrapping.Wrap
                };
                reasonBorder.Child = reasonText;
                sp.Children.Add(reasonBorder);
            }

            // 动态维度量化胶囊列表
            var dimsPanel = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
            foreach (var dim in _dynamicDims)
            {
                double? score = GetScoreForDimension(s, dim, i);
                var dimPill = new Border
                {
                    Background = (Brush)FindResource("BorderSubtleBrush"),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(5, 1, 5, 1),
                    Margin = new Thickness(0, 0, 5, 2)
                };
                dimPill.Child = new TextBlock
                {
                    Text = $"{dim}: {FormatScore(score)}",
                    FontSize = 10,
                    Foreground = (Brush)FindResource("TextSecondaryBrush")
                };
                dimsPanel.Children.Add(dimPill);
            }
            sp.Children.Add(dimsPanel);

            card.Child = sp;
            SuggestionsCardsContainer.Children.Add(card);
        }
    }

    private void SelectOption(int index)
    {
        _selectedOptionIndex = index;
        DrawMultiRadarChart();
        PopulateGroupedBarChart();
        DrawQuadrantMatrix();
        PopulateSuggestionsCards();

        if (index >= 0 && _advice.Suggestions != null && index < _advice.Suggestions.Count)
        {
            var s = _advice.Suggestions[index];
            BottomStatusText.Text = $"🎯 已聚焦方案 {index + 1}【{s.Tone}】 · 推进力: {s.GoalPushText} · 安全度: {s.PersonaSafetyText}";
            return;
        }
        UpdateDefaultBottomStatus();
    }
#if false
        }
        else
        {
            UpdateDefaultBottomStatus();
        if (_regenerateCallback == null && RegenerateAdviceButton != null)
        {
            RegenerateAdviceButton.Visibility = Visibility.Collapsed;
        }
    }

    public void OnThemeChanged()
    {
        if (!IsLoaded) return;
        PopulateRadarLegend();
        DrawMultiRadarChart();
        PopulateGroupedBarChart();
        DrawQuadrantMatrix();
        PopulateSuggestionsCards();
    }

    private void RadarHostGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded || e.NewSize.Width < 180 || e.NewSize.Height < 140) return;
        RadarCanvas.Width = Math.Max(260, e.NewSize.Width - 8);
        RadarCanvas.Height = Math.Max(180, e.NewSize.Height - 4);
        DrawMultiRadarChart();
    }

    private void QuadrantHostGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded || e.NewSize.Width < 160 || e.NewSize.Height < 140) return;
        QuadrantCanvas.Width = Math.Max(220, e.NewSize.Width - 8);
        QuadrantCanvas.Height = Math.Max(180, e.NewSize.Height - 4);
        DrawQuadrantMatrix();
    }

    private async void RegenerateAdviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_regenerateCallback == null) return;

        if (_regenCts != null)
        {
            _regenCts.Cancel();
            return;
        }

        _regenCts = new CancellationTokenSource();
        RegenerateAdviceButton.Content = "⏹ 停止生成";
        BottomStatusText.Text = "⏳ 正在结合当前对话、画像、技能与意图重新调用 AI 推演方案（再次点击可停止）...";

        try
        {
            var newAdvice = await _regenerateCallback(_regenCts.Token);
            if (newAdvice != null)
            {
                _advice = newAdvice;
                _selectedOptionIndex = -1;
                _dynamicDims = ResolveDynamicDimensions();
                PopulateSandtable();
                PopulateRadarLegend();
                DrawMultiRadarChart();
                PopulateGroupedBarChart();
                DrawQuadrantMatrix();
                PopulateSuggestionsCards();
                BottomStatusText.Text = $"✅ AI 已于 {DateTime.Now:HH:mm:ss} 重新生成最新推演方案与量化评分";
            }
            else
            {
                BottomStatusText.Text = "⚠️ 本次重新生成未返回有效结果，已保留当前方案";
            }
        }
        catch (OperationCanceledException)
        {
            BottomStatusText.Text = "⏹ 已取消本次重新生成";
        }
        catch (Exception ex)
        {
            BottomStatusText.Text = $"❌ 重新生成失败：{ex.Message}";
        }
        finally
        {
            _regenCts?.Dispose();
            _regenCts = null;
            RegenerateAdviceButton.Content = "🔄 AI 重新生成";
        }
        }
    }

#endif
    private void UpdateDefaultBottomStatus()
    {
        var suggestions = _advice.Suggestions ?? Array.Empty<ReplySuggestion>();
        bool anyMissing = suggestions.Any(s =>
            !s.HasGoalPushScore ||
            !s.HasPersonaSafetyScore ||
            _dynamicDims.Any(d => !GetScoreForDimension(s, d, 0).HasValue));

        BottomStatusText.Text = anyMissing
            ? "ℹ️ 部分分值本次 AI 未返回，已如实标为“—”（不再伪造数值）"
            : "✨ 动态量化研判完成 · 维度自适应提炼 · 感受话语的力量";
    }

    private async void PasteButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button btn && btn.Tag is string text)
        {
            Clipboard.SetText(text);
            string original = btn.Content?.ToString() ?? "📤 填入微信";
            bool pasted = await WeChatCopilot.Windows.WindowPositioner.TryPasteToWeChatInputAsync();
            btn.Content = pasted ? "✅ 已填入!" : "✔ 已复制";
            btn.IsEnabled = false;

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1500)
            };
            timer.Tick += (s, ev) =>
            {
                timer.Stop();
                btn.Content = original;
                btn.IsEnabled = true;
            };
            timer.Start();
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button btn && btn.Tag is string text)
        {
            Clipboard.SetText(text);
            string original = btn.Content?.ToString() ?? "📋 复制话术";
            btn.Content = "✅ 已复制!";
            btn.IsEnabled = false;

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1500)
            };
            timer.Tick += (s, ev) =>
            {
                timer.Stop();
                btn.Content = original;
                btn.IsEnabled = true;
            };
            timer.Start();
        }
    }

    // ===================== 导出战局推演报告 =====================

    private void ExportReportButton_Click(object sender, RoutedEventArgs e)
    {
        var sfd = new SaveFileDialog
        {
            Title = "导出话语力量与博弈推演全景分析报告",
            Filter = "Markdown 文档 (*.md)|*.md|文本文件 (*.txt)|*.txt",
            FileName = $"{_contactName}_博弈推演全景报告_{DateTime.Now:yyyyMMdd_HHmm}.md"
        };

        if (sfd.ShowDialog() != true) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# 🎯 话语力量与博弈推演全景大看板战略报告");
            sb.AppendLine();
            sb.AppendLine($"- **对话对象**：{_contactName}");
            sb.AppendLine($"- **终极战略目的**：{_ultimateGoal}");
            sb.AppendLine($"- **作战体系 Skill**：{_skill.Icon} {_skill.Name}");
            if (!string.IsNullOrWhiteSpace(_userIntent))
            {
                sb.AppendLine($"- **用户意图导向**：{_userIntent}");
            }
            sb.AppendLine($"- **生成时间**：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("## 🧠 一、战局立体推演沙盘");
            sb.AppendLine();
            sb.AppendLine($"### 1. 对方最新发言");
            sb.AppendLine($"> {_advice.TargetQuote ?? "（无具体原话）"}");
            sb.AppendLine();
            sb.AppendLine($"### 2. 战局态势与语境阶段");
            sb.AppendLine(_advice.ContextFlowSummary ?? "双方处于多轮交互博弈推进期。");
            sb.AppendLine();
            sb.AppendLine($"### 3. 潜台词深度穿透");
            sb.AppendLine(_advice.Subtext ?? "言辞克制，隐藏着深层安全感试探。");
            sb.AppendLine();
            sb.AppendLine($"### 4. 画像透视与底层价值动因");
            sb.AppendLine(_advice.Intent ?? "期望在稳住心理边界的同时观察我方诚意。");
            sb.AppendLine();
            sb.AppendLine($"### 5. {_skill.Name} 破局心法与避坑策略");
            sb.AppendLine(_advice.Strategy ?? "不踩画像雷区，稳步施加战略推力。");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("## 📊 二、动态量化博弈评估维度");
            sb.AppendLine();
            sb.AppendLine($"本次战局由 AI 实时提炼专属评估维度：**{string.Join("、", _dynamicDims)}**");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("## ⚔️ 三、候选回复方案量化对决清单");
            sb.AppendLine();

            var suggestions = _advice.Suggestions ?? Array.Empty<ReplySuggestion>();
            for (int i = 0; i < suggestions.Count; i++)
            {
                var s = suggestions[i];
                sb.AppendLine($"### 方案 {i + 1}：[{s.Tone}]");
                sb.AppendLine($"- **话术内容**：{s.Text}");
                sb.AppendLine($"- **战术依据**：{s.Reason}");
                sb.AppendLine($"- **核心量化**：目标推进力 = **{(s.HasGoalPushScore ? s.GoalPushText + "分" : "—")}**，画像安全度 = **{(s.HasPersonaSafetyScore ? s.PersonaSafetyText + "分" : "—")}**");
                sb.Append("- **动态维度得分**：");
                var dimScores = _dynamicDims.Select(d =>
                {
                    double? ds = GetScoreForDimension(s, d, i);
                    return ds.HasValue ? $"{d}={ds.Value:0}分" : $"{d}=—";
                });
                sb.AppendLine(string.Join("，", dimScores));
                sb.AppendLine();
            }

            File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
            MessageBox.Show($"战局推演全景分析报告已成功导出至：\n{sfd.FileName}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出报告失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ===================== 窗口控制 =====================

    private void HeaderBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
