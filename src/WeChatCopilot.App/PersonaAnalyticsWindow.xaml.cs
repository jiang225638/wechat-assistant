using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using WeChatCopilot.AI;
using WeChatCopilot.Core.Ai;
using WeChatCopilot.Core.Models;
using WeChatCopilot.Data;
using WeChatCopilot.Data.Security;

namespace WeChatCopilot.App;

/// <summary>
/// 联系人多维心智画像全景深度分析看板：
/// 提供高分辨率全量雷达图、各维度量化柱状对比图、特质走势折线图、
/// 观察层原始证据清单以及 AI 综合破局战略分析报告与导出闭环。
/// </summary>
public partial class PersonaAnalyticsWindow : Window
{
    private readonly Persona _persona;
    private readonly DistillSkill _skill;
    private readonly IReadOnlyList<HistoryMessage> _history;
    private readonly IReadOnlyList<PersonaTrait> _traits;
    private readonly SettingsStore _settingsStore = new();
    private string _currentScene = "Colleague";

    public PersonaAnalyticsWindow(
        Persona persona,
        DistillSkill skill,
        IReadOnlyList<HistoryMessage>? history = null)
    {
        InitializeComponent();

        _persona = persona;
        _skill = skill;
        _history = history ?? Array.Empty<HistoryMessage>();
        _traits = PersonaDistiller.DeduplicateByDimension(persona.Traits, _history);

        ThemeManager.ApplyCurrentTheme(this);
        Loaded += PersonaAnalyticsWindow_Loaded;
    }

    private void PersonaAnalyticsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        InitRelationshipIdentity();
        LoadData();
    }

    private void LoadData()
    {
        // 1. 顶部 Header 元数据配置
        HeaderContactName.Text = $"「{_persona.ContactName}」多维画像看板";
        HeaderSkillText.Text = $"{_skill.Icon} {_skill.Name}";
        MetaSourceCount.Text = $"💬 基于 {_persona.SourceMessageCount} 条真实历史消息";
        MetaUpdatedTime.Text = $"⏱ 更新时间: {_persona.UpdatedAt:yyyy-MM-dd HH:mm}";
        MetaDimensionCount.Text = $"📊 共提取 {_traits.Count} 个独立量化评估维度";

        if (_traits.Count == 0)
        {
            return;
        }

        // 2. 绘制可视化图表
        DrawLargeRadarChart();
        PopulateBarChart();
        DrawLineChart();
        UpdateStatMetrics();

        // 3. 填充全维度详细特质与证据清单
        TraitsDetailItemsControl.ItemsSource = _traits;

        // 4. 填充实战破局战略报告
        PopulateStrategyPlaybook();
    }

    // ===================== 图表 1: 高分辨率全景雷达图 =====================

    private void DrawLargeRadarChart()
    {
        LargeRadarCanvas.Children.Clear();
        int count = _traits.Count;
        if (count < 3) return;

        double cx = LargeRadarCanvas.Width / 2.0;
        double cy = LargeRadarCanvas.Height / 2.0;
        double maxR = 105.0;

        double angleStep = 2 * Math.PI / count;
        double startAngle = -Math.PI / 2.0;

        // 1. 同心正多边形网格层 (4圈: 25%, 50%, 75%, 100%)
        for (int level = 1; level <= 4; level++)
        {
            double rLevel = maxR * (level / 4.0);
            var gridPoints = new PointCollection();
            for (int i = 0; i < count; i++)
            {
                double angle = startAngle + i * angleStep;
                gridPoints.Add(new Point(cx + rLevel * Math.Cos(angle), cy + rLevel * Math.Sin(angle)));
            }

            var gridPoly = new Polygon
            {
                Points = gridPoints,
                Stroke = new SolidColorBrush(Color.FromArgb(level == 4 ? (byte)100 : (byte)45, 148, 163, 184)),
                StrokeThickness = level == 2 ? 1.5 : 1, // 50分中性线加粗
                StrokeDashArray = level == 4 ? null : new DoubleCollection { 2, 2 }
            };
            LargeRadarCanvas.Children.Add(gridPoly);

            // 标尺数值刻度
            var scaleText = new TextBlock
            {
                Text = $"{level * 25}",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromArgb(90, 148, 163, 184))
            };
            Canvas.SetLeft(scaleText, cx + 4);
            Canvas.SetTop(scaleText, cy - rLevel - 6);
            LargeRadarCanvas.Children.Add(scaleText);
        }

        // 2. 径向轴线层
        for (int i = 0; i < count; i++)
        {
            double angle = startAngle + i * angleStep;
            var axis = new Line
            {
                X1 = cx,
                Y1 = cy,
                X2 = cx + maxR * Math.Cos(angle),
                Y2 = cy + maxR * Math.Sin(angle),
                Stroke = new SolidColorBrush(Color.FromArgb(50, 148, 163, 184)),
                StrokeThickness = 1
            };
            LargeRadarCanvas.Children.Add(axis);
        }

        // 3. 数据高光多边形层
        var dataPoints = new PointCollection();
        for (int i = 0; i < count; i++)
        {
            double angle = startAngle + i * angleStep;
            double rVal = maxR * (Math.Clamp(_traits[i].Score, 10, 100) / 100.0);
            dataPoints.Add(new Point(cx + rVal * Math.Cos(angle), cy + rVal * Math.Sin(angle)));
        }

        var dataPoly = new Polygon
        {
            Points = dataPoints,
            Stroke = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
            StrokeThickness = 2.5,
            Fill = new SolidColorBrush(Color.FromArgb(60, 56, 189, 248))
        };
        LargeRadarCanvas.Children.Add(dataPoly);

        // 4. 数据顶点高光圆点
        for (int i = 0; i < count; i++)
        {
            Point p = dataPoints[i];
            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                Stroke = Brushes.White,
                StrokeThickness = 2,
                ToolTip = $"{_traits[i].Dimension}: {_traits[i].ScoreInt}分 (可信度: {_traits[i].ConfidenceText})\n{_traits[i].Attribute}"
            };
            Canvas.SetLeft(dot, p.X - 4);
            Canvas.SetTop(dot, p.Y - 4);
            LargeRadarCanvas.Children.Add(dot);
        }

        // 5. 外周各维度全名标签与得分 (完全保留全名，绝不裁剪)
        double labelR = maxR + 24;
        for (int i = 0; i < count; i++)
        {
            double angle = startAngle + i * angleStep;
            double lx = cx + labelR * Math.Cos(angle);
            double ly = cy + labelR * Math.Sin(angle);

            string dimName = _traits[i].Dimension;
            if (dimName.Length > 6)
            {
                int mid = (dimName.Length + 1) / 2;
                dimName = dimName[..mid] + "\n" + dimName[mid..];
            }

            bool isLight = ThemeManager.IsLightMode;
            var tb = new TextBlock
            {
                Text = $"{dimName}\n{_traits[i].ScoreInt}分",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = _traits[i].Score >= 75
                    ? new SolidColorBrush(isLight ? Color.FromRgb(5, 150, 105) : Color.FromRgb(167, 243, 208))
                    : _traits[i].Score < 40
                        ? new SolidColorBrush(isLight ? Color.FromRgb(225, 29, 72) : Color.FromRgb(254, 205, 211))
                        : new SolidColorBrush(isLight ? Color.FromRgb(30, 41, 59) : Color.FromRgb(226, 232, 240)),
                TextAlignment = TextAlignment.Center
            };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            Canvas.SetLeft(tb, lx - tb.DesiredSize.Width / 2.0);
            Canvas.SetTop(tb, ly - tb.DesiredSize.Height / 2.0);
            LargeRadarCanvas.Children.Add(tb);
        }
    }

    // ===================== 图表 2: 各维度得分量化对比柱状图 =====================

    private void PopulateBarChart()
    {
        BarChartContainer.Children.Clear();

        var sorted = _traits.OrderByDescending(t => t.Score).ToList();
        double rowMarginV = sorted.Count <= 6 ? 6 : 4;

        foreach (var t in sorted)
        {
            var rowGrid = new Grid { Margin = new Thickness(0, rowMarginV, 0, rowMarginV) };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });

            // 1. 维度名称
            var dimTb = new TextBlock
            {
                Text = t.Dimension,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = t.Dimension
            };
            dimTb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            Grid.SetColumn(dimTb, 0);
            rowGrid.Children.Add(dimTb);

            // 2. 柱状条轨道与响应式自适应比例填充 (通过双列 Star Grid 随宽度自适应铺满，杜绝右侧留白)
            var trackBorder = new Border
            {
                Height = 15,
                CornerRadius = new CornerRadius(3.5),
                VerticalAlignment = VerticalAlignment.Center,
                ClipToBounds = true
            };
            trackBorder.SetResourceReference(Border.BackgroundProperty, "CardSubtleBg");

            var barGrid = new Grid();
            double clampedScore = Math.Clamp(t.Score, 5, 100);
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(clampedScore, GridUnitType.Star) });
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - clampedScore, GridUnitType.Star) });

            var barFill = new Border
            {
                Height = 15,
                CornerRadius = new CornerRadius(3.5)
            };

            if (t.Score >= 75)
            {
                barFill.Background = new LinearGradientBrush(
                    Color.FromRgb(16, 185, 129),
                    Color.FromRgb(52, 211, 153),
                    new Point(0, 0),
                    new Point(1, 0));
            }
            else if (t.Score >= 55)
            {
                barFill.Background = new LinearGradientBrush(
                    Color.FromRgb(59, 130, 246),
                    Color.FromRgb(56, 189, 248),
                    new Point(0, 0),
                    new Point(1, 0));
            }
            else if (t.Score >= 40)
            {
                barFill.Background = new LinearGradientBrush(
                    Color.FromRgb(245, 158, 11),
                    Color.FromRgb(251, 191, 36),
                    new Point(0, 0),
                    new Point(1, 0));
            }
            else
            {
                barFill.Background = new LinearGradientBrush(
                    Color.FromRgb(225, 29, 72),
                    Color.FromRgb(244, 63, 94),
                    new Point(0, 0),
                    new Point(1, 0));
            }

            Grid.SetColumn(barFill, 0);
            barGrid.Children.Add(barFill);
            trackBorder.Child = barGrid;

            Grid.SetColumn(trackBorder, 1);
            rowGrid.Children.Add(trackBorder);

            // 3. 得分数值
            var scoreTb = new TextBlock
            {
                Text = $"{t.ScoreInt}分",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            scoreTb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            Grid.SetColumn(scoreTb, 2);
            rowGrid.Children.Add(scoreTb);

            // 4. 状态标签胶囊
            string statusTag = t.Score >= 75 ? "优势高位" : t.Score < 40 ? "防卫敏感" : "基准稳态";
            Color tagColor = t.Score >= 75 ? Color.FromRgb(16, 185, 129) : t.Score < 40 ? Color.FromRgb(244, 63, 94) : Color.FromRgb(56, 189, 248);

            var tagBorder = new Border
            {
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(5, 1, 5, 1),
                Background = new SolidColorBrush(Color.FromArgb(30, tagColor.R, tagColor.G, tagColor.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(70, tagColor.R, tagColor.G, tagColor.B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                VerticalAlignment = VerticalAlignment.Center
            };
            tagBorder.Child = new TextBlock
            {
                Text = statusTag,
                FontSize = 9.5,
                Foreground = new SolidColorBrush(tagColor),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Grid.SetColumn(tagBorder, 3);
            rowGrid.Children.Add(tagBorder);

            BarChartContainer.Children.Add(rowGrid);
        }
    }

    // ===================== 图表 3: 特质走势与张力折线图 =====================

    private void DrawLineChart()
    {
        LineChartCanvas.Children.Clear();
        int count = _traits.Count;
        if (count < 2) return;

        double w = LineChartCanvas.Width;
        double h = LineChartCanvas.Height;
        double padLeft = 32.0;
        double padRight = 32.0;
        double padTop = 16.0;
        double padBottom = 22.0;

        double plotW = w - padLeft - padRight;
        double plotH = h - padTop - padBottom;

        // 1. 横向参考线 (25, 50, 75, 100)
        double[] levels = { 25, 50, 75, 100 };
        foreach (double lev in levels)
        {
            double y = padTop + plotH * (1.0 - lev / 100.0);
            var refLine = new Line
            {
                X1 = padLeft,
                Y1 = y,
                X2 = w - padRight,
                Y2 = y,
                Stroke = new SolidColorBrush(Color.FromArgb(lev == 50 ? (byte)75 : (byte)35, 148, 163, 184)),
                StrokeThickness = lev == 50 ? 1.5 : 1,
                StrokeDashArray = new DoubleCollection { 2, 2 }
            };
            LineChartCanvas.Children.Add(refLine);

            var levTb = new TextBlock
            {
                Text = $"{lev}",
                FontSize = 8.5,
                Foreground = new SolidColorBrush(Color.FromArgb(80, 148, 163, 184))
            };
            Canvas.SetLeft(levTb, padLeft - 22);
            Canvas.SetTop(levTb, y - 6);
            LineChartCanvas.Children.Add(levTb);
        }

        // 2. 平均分基准虚线
        double avgScore = _traits.Average(t => t.Score);
        double avgY = padTop + plotH * (1.0 - Math.Clamp(avgScore, 0, 100) / 100.0);
        var avgLine = new Line
        {
            X1 = padLeft,
            Y1 = avgY,
            X2 = w - padRight,
            Y2 = avgY,
            Stroke = new SolidColorBrush(Color.FromArgb(120, 245, 158, 11)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 2 }
        };
        LineChartCanvas.Children.Add(avgLine);

        // 3. 折线绘制
        double stepX = plotW / (count - 1);
        var points = new PointCollection();
        for (int i = 0; i < count; i++)
        {
            double x = padLeft + i * stepX;
            double y = padTop + plotH * (1.0 - Math.Clamp(_traits[i].Score, 0, 100) / 100.0);
            points.Add(new Point(x, y));
        }

        var polyline = new Polyline
        {
            Points = points,
            Stroke = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
            StrokeThickness = 2.5
        };
        LineChartCanvas.Children.Add(polyline);

        // 4. 数据点高光与标签
        for (int i = 0; i < count; i++)
        {
            Point pt = points[i];
            var dot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                Stroke = Brushes.White,
                StrokeThickness = 1.5,
                ToolTip = $"{_traits[i].Dimension}: {_traits[i].ScoreInt}分"
            };
            Canvas.SetLeft(dot, pt.X - 3.5);
            Canvas.SetTop(dot, pt.Y - 3.5);
            LineChartCanvas.Children.Add(dot);

            // 分数文本
            bool isLight = ThemeManager.IsLightMode;
            var scoreTb = new TextBlock
            {
                Text = $"{_traits[i].ScoreInt}",
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = isLight ? new SolidColorBrush(Color.FromRgb(15, 23, 42)) : Brushes.White
            };
            scoreTb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(scoreTb, pt.X - scoreTb.DesiredSize.Width / 2.0);
            Canvas.SetTop(scoreTb, pt.Y - 14);
            LineChartCanvas.Children.Add(scoreTb);

            // 底部维度简称
            string xLabel = _traits[i].Dimension.Length > 4 ? _traits[i].Dimension[..3] + ".." : _traits[i].Dimension;
            var dimTb = new TextBlock
            {
                Text = xLabel,
                FontSize = 9,
                Foreground = new SolidColorBrush(isLight ? Color.FromRgb(71, 85, 105) : Color.FromRgb(148, 163, 184)),
                ToolTip = _traits[i].Dimension
            };
            dimTb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(dimTb, pt.X - dimTb.DesiredSize.Width / 2.0);
            Canvas.SetTop(dimTb, h - padBottom + 4);
            LineChartCanvas.Children.Add(dimTb);
        }
    }

    // ===================== 统计数据更新 =====================

    private void UpdateStatMetrics()
    {
        if (_traits.Count == 0) return;

        var sorted = _traits.OrderByDescending(t => t.Score).ToList();
        var top1 = sorted.First();
        var top2 = sorted.Count > 1 ? sorted[1] : top1;
        var low1 = sorted.Last();
        double spread = top1.Score - low1.Score;
        double avgScore = _traits.Average(t => t.Score);
        double avgConf = _traits.Average(t => t.Confidence);

        // 卡片 1：第一主导维度 (动态绑定提取出的最高维度名称)
        StatCard1Header.Text = $"🌟 第一主导维度 · 【{top1.Dimension}】";
        StatTopDimText.Text = top1.Dimension;
        StatTopScoreText.Text = $"{top1.ScoreInt}分 (显著优势 · 核心支柱)";

        // 卡片 2：协同支柱维度 (动态绑定提取出的次高维度名称)
        StatCard2Header.Text = $"✨ 协同支柱维度 · 【{top2.Dimension}】";
        StatCard2DimText.Text = top2.Dimension;
        StatCard2ScoreText.Text = $"{top2.ScoreInt}分 (强劲支撑 · 稳态表现)";

        // 卡片 3：防备敏感/洼地维度 (动态绑定提取出的最低防御维度名称)
        StatCard3Header.Text = $"🛡️ 心理敏感/洼地维度 · 【{low1.Dimension}】";
        StatLowDimText.Text = low1.Dimension;
        StatLowScoreText.Text = $"{low1.ScoreInt}分 (防备防线 · 潜在雷区)";

        // 卡片 4：心智极差与均分
        StatCard4Header.Text = $"⚡ 心智张力极差与加权均分";
        StatSpreadText.Text = $"{spread:0}分极差 (均分: {avgScore:0.0}分)";
        StatAvgConfidenceText.Text = $"样本可信度: {avgConf:0.00} (高可信样本)";
    }

    // ===================== Tab 3: 交往身份研判与动态实战破局战术 =====================

    private string _customRelationship = "";

    private void InitRelationshipIdentity()
    {
        _currentScene = DetectRelationshipIdentity(out _);
        _customRelationship = GetSceneDisplayName(_currentScene);
    }

    private string DetectRelationshipIdentity(out string matchReason)
    {
        int scoreIntimacy = 0;
        int scoreLeader = 0;
        int scoreColleague = 0;
        int scoreClient = 0;
        int scoreFriend = 0;

        string skillContext = $"{_skill.Id} {_skill.Name} {_skill.Description}".ToLowerInvariant();
        string goalContext = (_persona.UltimateGoal ?? "").ToLowerInvariant();

        // 1. 技能模型特征判断
        if (skillContext.Contains("tds-relationship") || skillContext.Contains("she-love-me") ||
            skillContext.Contains("恋爱") || skillContext.Contains("情感") || skillContext.Contains("追求") ||
            skillContext.Contains("伴侣") || skillContext.Contains("约会") || skillContext.Contains("亲密") ||
            skillContext.Contains("挽回") || skillContext.Contains("脱单") || skillContext.Contains("心动") ||
            skillContext.Contains("表白") || skillContext.Contains("吸引"))
        {
            scoreIntimacy += 12;
        }
        if (skillContext.Contains("向上") || skillContext.Contains("领导") || skillContext.Contains("汇报") ||
            skillContext.Contains("老板") || skillContext.Contains("管理"))
        {
            scoreLeader += 12;
        }
        if (skillContext.Contains("商务") || skillContext.Contains("客户") || skillContext.Contains("销售") ||
            skillContext.Contains("谈判") || skillContext.Contains("成单") || skillContext.Contains("大客户"))
        {
            scoreClient += 12;
        }
        if (skillContext.Contains("朋友") || skillContext.Contains("社交") || skillContext.Contains("聚会") ||
            skillContext.Contains("闲聊"))
        {
            scoreFriend += 12;
        }

        // 2. 终极目的 (UltimateGoal) 判断
        if (goalContext.Contains("挽回") || goalContext.Contains("追求") || goalContext.Contains("恋爱") ||
            goalContext.Contains("脱单") || goalContext.Contains("前任") || goalContext.Contains("女友") ||
            goalContext.Contains("男友") || goalContext.Contains("喜欢") || goalContext.Contains("心仪") ||
            goalContext.Contains("暧昧") || goalContext.Contains("好感"))
        {
            scoreIntimacy += 10;
        }
        if (goalContext.Contains("晋升") || goalContext.Contains("加薪") || goalContext.Contains("领导满意") ||
            goalContext.Contains("向上汇报"))
        {
            scoreLeader += 10;
        }
        if (goalContext.Contains("签约") || goalContext.Contains("回款") || goalContext.Contains("客户") ||
            goalContext.Contains("成交"))
        {
            scoreClient += 10;
        }

        // 3. 画像实际提取的维度与心智推断词频特征
        string detectedKeyword = "";
        foreach (var t in _traits)
        {
            string tText = $"{t.Dimension} {t.Attribute}".ToLowerInvariant();
            if (tText.Contains("框架") || tText.Contains("需求感") || tText.Contains("依恋") ||
                tText.Contains("亲密") || tText.Contains("情绪价值") || tText.Contains("安全感") ||
                tText.Contains("废物测试") || tText.Contains("吸引") || tText.Contains("断联") ||
                tText.Contains("关键认知") || tText.Contains("传递方式") || tText.Contains("恋爱"))
            {
                scoreIntimacy += 3;
                if (string.IsNullOrEmpty(detectedKeyword)) detectedKeyword = t.Dimension;
            }
            if (tText.Contains("权威") || tText.Contains("汇报") || tText.Contains("下属") ||
                tText.Contains("考评") || tText.Contains("指标") || tText.Contains("掌控感"))
            {
                scoreLeader += 3;
                if (string.IsNullOrEmpty(detectedKeyword)) detectedKeyword = t.Dimension;
            }
            if (tText.Contains("采购") || tText.Contains("预算") || tText.Contains("甲方") ||
                tText.Contains("报价") || tText.Contains("方案确认") || tText.Contains("商业利益"))
            {
                scoreClient += 3;
                if (string.IsNullOrEmpty(detectedKeyword)) detectedKeyword = t.Dimension;
            }
            if (tText.Contains("协同") || tText.Contains("跨部门") || tText.Contains("排期") ||
                tText.Contains("分工") || tText.Contains("交付标准") || tText.Contains("平级"))
            {
                scoreColleague += 3;
                if (string.IsNullOrEmpty(detectedKeyword)) detectedKeyword = t.Dimension;
            }
            if (tText.Contains("爱好") || tText.Contains("聚会") || tText.Contains("闲聊") ||
                tText.Contains("随性") || tText.Contains("游玩") || tText.Contains("交情"))
            {
                scoreFriend += 3;
                if (string.IsNullOrEmpty(detectedKeyword)) detectedKeyword = t.Dimension;
            }
        }

        var scores = new (string Key, int Score, string Name)[]
        {
            ("Intimacy", scoreIntimacy, "亲密交往对象 (心仪伴侣)"),
            ("Leader", scoreLeader, "职场管理对象 (直属领导)"),
            ("Client", scoreClient, "商业合作对象 (关键客户)"),
            ("Colleague", scoreColleague, "职场协作对象 (平级同事)"),
            ("Friend", scoreFriend, "社交交往对象 (日常朋友)")
        };

        var best = scores.OrderByDescending(s => s.Score).First();
        if (best.Score == 0)
        {
            matchReason = "看板默认设定为职场协作对象，可自由输入或下拉快捷选择";
            return "Colleague";
        }

        string traitHint = !string.IsNullOrEmpty(detectedKeyword) ? $"及【{detectedKeyword}】" : "";
        matchReason = $"已结合技能「{_skill.Name}」{traitHint}智能研判";
        return best.Key;
    }

    private string GetSceneDisplayName(string scene) => scene switch
    {
        "Leader" => "👑 职场管理对象 (直属领导)",
        "Colleague" => "💼 职场协作对象 (平级同事)",
        "Client" => "🤝 商业合作对象 (关键客户)",
        "Intimacy" => "❤️ 亲密交往对象 (心仪伴侣)",
        "Friend" => "☕ 社交交往对象 (日常朋友)",
        _ => "💼 职场协作对象 (平级同事)"
    };

    private void PopulateStrategyPlaybook()
    {
        if (_traits.Count == 0) return;

        StrategySubTitle.Text = $"结合「{_persona.ContactName}」的多维心智特质与「{_skill.Name}」技能体系 · 资深心理与实战沟通专家深度拆解：";

        var sorted = _traits.OrderByDescending(t => t.Score).ToList();
        var tTop1 = sorted[0];
        var tTop2 = sorted.Count > 1 ? sorted[1] : sorted[0];
        var tLow1 = sorted.Last();
        var tLow2 = sorted.Count > 2 ? sorted[^2] : tLow1;

        // 1. 卡片标题动态化绑定（绝不写死固定维度）
        StrategyCard1Title.Text = tTop1.Dimension == tTop2.Dimension
            ? $"🎯 核心主导心智 · 【{tTop1.Dimension}】底层心理机制与诉求底色"
            : $"🎯 核心主导心智 · 【{tTop1.Dimension}】与【{tTop2.Dimension}】底层心理机制与诉求底色";

        StrategyCard2Title.Text = tTop1.Dimension == tTop2.Dimension
            ? $"✨ 顺势借力抓手 · 基于【{tTop1.Dimension}】的同频共鸣与推进法则"
            : $"✨ 顺势借力抓手 · 基于【{tTop1.Dimension}】与【{tTop2.Dimension}】的同频共鸣与推进法则";

        StrategyCard3Title.Text = tLow1.Dimension == tLow2.Dimension
            ? $"🚫 心理敏感死穴 · 针对【{tLow1.Dimension}】的沟通雷区与避坑红线"
            : $"🚫 心理敏感死穴 · 针对【{tLow1.Dimension}】与【{tLow2.Dimension}】的沟通雷区与避坑红线";

        StrategyCard4Title.Text = $"💬 实战临场示范 · 针对【{tTop1.Dimension}】与【{tLow1.Dimension}】的高情商破局示范话术";

        // 2. 卡片 1 内容：核心主导心智与诉求底色
        var sb1 = new StringBuilder();
        sb1.AppendLine($"• 第一核心主导特质：在【{tTop1.Dimension}】（得分 {tTop1.ScoreInt}分，可信度 {tTop1.ConfidenceText}）占据极其突出的心智高位。");
        sb1.AppendLine($"  心智机制剖析：{tTop1.Attribute}");
        if (tTop1.HasEvidence)
        {
            sb1.AppendLine($"  真实原话印证：“{tTop1.EvidenceDisplayText}”");
        }

        if (tTop1.Dimension != tTop2.Dimension)
        {
            sb1.AppendLine();
            sb1.AppendLine($"• 第二协同支柱特质：在【{tTop2.Dimension}】（得分 {tTop2.ScoreInt}分）形成强有力的心理辅助支撑。");
            sb1.AppendLine($"  心智机制剖析：{tTop2.Attribute}");
            if (tTop2.HasEvidence)
            {
                sb1.AppendLine($"  真实原话印证：“{tTop2.EvidenceDisplayText}”");
            }
        }
        sb1.AppendLine();
        sb1.AppendLine(BuildSceneMindsetSynthesis(_currentScene, tTop1, tTop2));
        StrategyMindsetOverviewText.Text = sb1.ToString().TrimEnd();

        // 3. 卡片 2 内容：顺势借力抓手
        var sb2 = new StringBuilder();
        sb2.AppendLine($"• 借力抓手 1（顺应【{tTop1.Dimension}】）：{BuildTopDimensionLeverageRule(_currentScene, tTop1)}");
        if (tTop1.Dimension != tTop2.Dimension)
        {
            sb2.AppendLine($"• 借力抓手 2（激活【{tTop2.Dimension}】）：{BuildTopDimensionLeverageRule(_currentScene, tTop2)}");
        }
        sb2.AppendLine($"• 深度同频心法：{BuildSceneRapportGuide(_currentScene, tTop1, tTop2)}");
        StrategyLeveragePointsText.Text = sb2.ToString().TrimEnd();

        // 4. 卡片 3 内容：心理敏感死穴与避坑红线
        var sb3 = new StringBuilder();
        sb3.AppendLine($"• 心理脆弱/敏感洼地：对方在【{tLow1.Dimension}】（仅 {tLow1.ScoreInt}分）显现出极强的防御心或心理脆弱点。");
        sb3.AppendLine($"  洼地机制推断：{tLow1.Attribute}");
        if (tLow1.Dimension != tLow2.Dimension && tLow2.Score < 50)
        {
            sb3.AppendLine($"  次级防备特质：在【{tLow2.Dimension}】（{tLow2.ScoreInt}分）同样存在敏感表现（{tLow2.Attribute}）。");
        }
        sb3.AppendLine();
        sb3.AppendLine($"• 针对【{tLow1.Dimension}】与【{tLow2.Dimension}】的三大绝对避坑红线：");
        sb3.AppendLine(BuildDimensionTaboos(_currentScene, tLow1, tLow2));
        StrategyTaboosText.Text = sb3.ToString().TrimEnd();

        // 5. 卡片 4 内容：实战临场示范话术
        var sb4 = new StringBuilder();
        sb4.AppendLine(BuildSceneDemonstrationScripts(_currentScene, tTop1, tLow1));
        StrategyPrescriptionText.Text = sb4.ToString().TrimEnd();
    }

    private string BuildSceneMindsetSynthesis(string scene, PersonaTrait tTop1, PersonaTrait tTop2) => scene switch
    {
        "Intimacy" =>
            $"• 专家情感透镜总析：在亲密交往中，对方并不是天生冷淡或难以靠近，而是其【{tTop1.Dimension}】（{tTop1.ScoreInt}分）构成了核心情感滤镜。对方深层最渴望的是【被真正看见、情绪同频与可依赖的安全感】。切忌用低位讨好的方式迎合，只有展示自洽成熟的个人状态与敏锐的情绪洞察，才能叩开对方心门。",
        "Leader" =>
            $"• 专家管理透镜总析：在上下级职场互动中，领导极其看重【确定性、结果闭环与掌控感】。对方在【{tTop1.Dimension}】的高位表现，意味着其极其反感含糊推诿与情绪化表达，最需要下属具备‘结论先行、凡事有交代、件件有着落’的高效执行闭环。",
        "Colleague" =>
            $"• 专家协作透镜总析：在职场平级配合中，对方讲求【权责明晰、效率对等、互不添堵】。其在【{tTop1.Dimension}】的高位凸显了其做事风格，对接前明确交代前因后果与利他价值，是推进丝滑协同的最优路径。",
        "Client" =>
            $"• 专家商业透镜总析：在商务决策场景中，客户真实核心诉求始终是【规避自身决策风险、降本增效并证明选择的正确性】。结合其在【{tTop1.Dimension}】的高敏感度，切忌空谈口头承诺，必须用扎实的数据与轻量低门槛试点打消其决策疑虑。",
        _ =>
            $"• 专家社交透镜总析：在日常社交相处中，对方崇尚【熟不逾矩、久处不累、自然舒坦】的相处节奏。顺应其在【{tTop1.Dimension}】的生活偏好，保持真诚有边界的互动，最能建立长久深厚的人际信任。"
    };

    private string BuildTopDimensionLeverageRule(string scene, PersonaTrait trait)
    {
        string dim = trait.Dimension.ToLowerInvariant();
        if (dim.Contains("关键认知") || dim.Contains("认知"))
        {
            return $"不要强行说教或直接灌输观点。善用事实细节、生活类比与侧面烘托，让对方在自主思考中得出符合你期望的结论，保护其自主感。";
        }
        if (dim.Contains("传递方式") || dim.Contains("间接"))
        {
            return $"采用‘润物细无声’的间接传递策略。通过朋友圈生活切面、第三方评价或无压力的偶遇细节展现价值，制造自然吸引与好奇心。";
        }
        if (dim.Contains("框架"))
        {
            return $"保持温和而坚定的个人框架。不随对方情绪剧烈起伏而动摇，设立健康的边界感，越有原则与主见，反而越能赢得其敬畏与倾斜。";
        }
        if (dim.Contains("情绪价值") || dim.Contains("需求感"))
        {
            return $"‘先接情绪，再谈事情’。在对方表达疲惫或委屈时，第一时间提供无评判的情绪兜底，成为其最安心的心理能量站。";
        }
        if (dim.Contains("权威") || dim.Contains("执行") || dim.Contains("结果"))
        {
            return $"以‘数据说话、结论先行、闭环交付’为准则。主动定期同步进展，让对方感受到无懈可击的专业度与确定性。";
        }
        return $"顺应其在【{trait.Dimension}】所追求的心理预期（{trait.Attribute}），顺势而为，不与之形成逆向抵触，以理解促合作。";
    }

    private string BuildSceneRapportGuide(string scene, PersonaTrait tTop1, PersonaTrait tTop2) => scene switch
    {
        "Intimacy" =>
            $"借力【{tTop1.Dimension}】建立高价值吸引，互动中多捕捉对方抛出的情绪暗号，适度制造‘推拉’与好奇心，形成双向奔赴的情感引力场。",
        "Leader" =>
            $"顺应其对【{tTop1.Dimension}】的高标准，请示工作带两套备选方案供其决策，永远不要把空白题直接推给领导。",
        "Colleague" =>
            $"对接工作提前预约并讲明利他点，把对方的工作阻力降到最低，用高情商分工换取长远互助资本。",
        "Client" =>
            $"以【{tTop1.Dimension}】为切入点剖析其业务痛点，以专业顾问姿态提供增量见解，实现从普通供应商到战略伙伴的跨越。",
        _ =>
            $"契合其共同兴趣点切入，不刻意迎合，保持松弛感与幽默感，让每次聊天都成为精神放松的愉悦体验。"
    };

    private string BuildDimensionTaboos(string scene, PersonaTrait tLow1, PersonaTrait tLow2)
    {
        var sb = new StringBuilder();
        string dimLow1 = tLow1.Dimension;
        string dimLow2 = tLow2.Dimension;

        // 红线 1：专门针对 tLow1 维度的深度避坑
        if (dimLow1.Contains("低位") || dimLow1.Contains("框架") || dimLow1.Contains("需求感"))
        {
            sb.AppendLine($"  1. 绝忌低声下气自我贬低与过度迎合：越是试图通过放弃原则、秒回讨好来索取回应，越会击穿对方的尊重底线，彻底沦为低位被动方；");
        }
        else if (dimLow1.Contains("情绪") || dimLow1.Contains("敏感") || dimLow1.Contains("脆弱"))
        {
            sb.AppendLine($"  1. 绝忌冷漠回避与逻辑说教：在其情绪防御高筑时切勿讲大道理，冰冷的分析只会激化对抗，必须先抚平情绪再理清事实；");
        }
        else
        {
            sb.AppendLine($"  1. 绝忌触碰其在【{dimLow1}】的心理雷区：对方在此特质处于防御洼地（{tLow1.Attribute}），切勿直接质疑或公开触碰其软肋；");
        }

        // 红线 2：次低维度或防备心避坑
        if (dimLow2 != dimLow1 && (dimLow2.Contains("信任") || dimLow2.Contains("坦诚") || dimLow2.Contains("控制")))
        {
            sb.AppendLine($"  2. 绝忌隐瞒欺瞒或越界控制：给予对方充分的安全感与空间边界，任何窥探或失信行为都会引发断崖式信任瓦解；");
        }
        else
        {
            sb.AppendLine($"  2. 绝忌好为人师与否定挑刺：永远不要在言语争辩上强争高下，否定对方的感受只会引爆逆反与逃避机制；");
        }

        // 红线 3：当前交往身份的专属禁忌
        string sceneTaboo = scene switch
        {
            "Intimacy" => "绝忌夺命连环催与查户口逼问：给彼此留足呼吸空间，分享欲要给懂的人，不要用令对方窒息的紧绷感绑架彼此；",
            "Leader" => "绝忌报喜不报忧、拖延到最后一刻爆雷：遇到风险尽早预警并附带补救预案，杜绝让领导产生不可控的惊吓；",
            "Colleague" => "绝忌在大群公开问责甩锅：协同卡点私下体面沟通对齐，切勿为了自保让同事当众难堪；",
            "Client" => "绝忌过度承诺与虚假吹嘘：无法100%交付的内容切勿信口开河，商业往来诚信是第一生命线；",
            _ => "绝忌毫无分寸的索取与道德绑架：亲密有间、熟不逾矩，切莫把朋友的好脾气当成理所当然的消耗品。"
        };
        sb.AppendLine($"  3. {sceneTaboo}");

        return sb.ToString().TrimEnd();
    }

    private string BuildSceneDemonstrationScripts(string scene, PersonaTrait tTop1, PersonaTrait tLow1) => scene switch
    {
        "Intimacy" =>
$@"【示范 1：顺应【{tTop1.Dimension}】的高价值轻松破冰】
“刚刚路过一家店看到[某样对方感兴趣的事物/萌物]，第一反应就觉得这完全是你的风格哈哈。今天忙不忙呀，给你云投喂一杯热拿铁，打工人续命要紧~”
（心法点拨：依托生活细节唤醒好奇与好感，分享欲自然流淌，绝无索取回应的低位压迫感）

【示范 2：遭遇冷淡或对方情绪低落时守住【{tLow1.Dimension}】防线】
“感觉你今天心情有点闷闷的/特别累，摸摸头。今晚就先不吵你啦，好好泡个热水澡放空一下。有想吐槽的随时找我，我一直在，晚安好梦~”
（心法点拨：表达细腻关切的同时主动提供安全撤退空间，零需求感、零指责，反向激发对方的内疚与依恋）",

        "Leader" =>
$@"【示范 1：突发卡点顺应【{tTop1.Dimension}】高效请示】
“领导，关于[XX事项]遇到一个突发卡点[一句话客观陈述核心事实]。我们团队迅速拟定了两套应对方案：方案A[见效快但需小额追加资源]，方案B[稳妥但需顺延半天]。我建议采用方案A，整体影响最小，您看是否可行？”
（心法点拨：结论先行 + 带着两套解法让领导做选择题，彻底消除失控焦虑）

【示范 2：任务交付顺应【{tLow1.Dimension}】主动对齐标准】
“收到领导。为确保最终产出分毫不差，我跟您快速对齐下验收要点：先抓好[要点1]和[要点2]，在[周五下午4点前]呈报终稿，您看这样节奏符合您的预期吗？”
（心法点拨：明确边界与时间节点，件件有着落，彻底打消领导的潜在担忧）",

        "Colleague" =>
$@"【示范 1：跨部门协同借助【{tTop1.Dimension}】发起推进】
“[名字]你好，这周推进[项目X]节点，在[模块Y]这块需要借力你的专业经验把把关。我们提炼了2个最关键的核心点，大概耽误你5分钟确认下，你看今天下午3点方便在工位对一下吗？”
（心法点拨：说明必要性 + 量化时间成本 + 礼貌预约，最易获得正向支持）

【示范 2：催促进度而不伤和气】
“[名字]，看你这两天特别忙，辛苦啦！我们下游环节计划明天启动，想问问手头的[材料Z]目前进展如何，有没有什么需要我们提前搭把手的地方？”
（心法点拨：先共情分担 + 再问进度，既达到了催办目的又照顾了同事人情）",

        "Client" =>
$@"【示范 1：客户对方案或预算心存疑虑时】
“非常理解您的审慎考量，换作是我也会精打细算每一笔投入。之所以设计该配置，核心是为了帮贵司把[关键隐患]提前堵死。如果您想先看实际成效，我们可以先启动[第一期小步验证]，您看这样是否更踏实？”
（心法点拨：肯定合理性 + 讲透风控价值 + 给出低风险折中台阶）

【示范 2：初次触达与商务破冰】
“[客户称呼]好，拜读过贵司近期在[业务赛道]的动作，非常钦佩。我们近期正好协助同业解决了[具体痛点]，顺手提炼了一份避坑参考资料，发您闲暇时翻翻，或许对当下规划有些许参考价值。”
（心法点拨：利他先行 + 赠送增量认知，无压迫感建立专业连接）",

        _ =>
$@"【示范 1：久未联系随性破冰】
“老铁，最近忙啥呢？昨天正好刷到[当年两人的共同回忆/搞笑梗]，瞬间想起你了，出来冒个泡，改天有空出来整两杯/吃个饭聚聚！”
（心法点拨：随性真诚无负担，唤醒共同记忆）

【示范 2：朋友受挫时给予力挺】
“哥们/姐妹，别一个人闷着硬扛，有什么需要我帮忙的随时支声，一句话随叫随到。无论咋样我都在这，随时找我吐苦水！”
（心法点拨：纯粹的情感托底，让人倍感温暖可靠）"
    };



    // ===================== 报告复制与导出 =====================

    private string BuildMarkdownReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 「{_persona.ContactName}」多维心智特质与沟通行为全景分析报告");
        sb.AppendLine();
        sb.AppendLine($"> **分析时间**：{_persona.UpdatedAt:yyyy-MM-dd HH:mm}  |  **采用技能体系**：{_skill.Icon} {_skill.Name}  |  **样本规模**：{_persona.SourceMessageCount} 条真实历史消息");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## 一、多维量化特质评分表");
        sb.AppendLine();
        sb.AppendLine("| 评估维度 | 量化评分 | 可信度 | 特质属性与心理模式推断 | 原话证据摘要 |");
        sb.AppendLine("| :--- | :---: | :---: | :--- | :--- |");
        foreach (var t in _traits.OrderByDescending(t => t.Score))
        {
            string evSummary = t.Evidence.Count > 0 ? $"\"{t.Evidence[0]}\"" + (t.Evidence.Count > 1 ? $" 等{t.Evidence.Count}条" : "") : "暂无直引";
            sb.AppendLine($"| **{t.Dimension}** | `{t.ScoreInt}分` | `{t.ConfidenceText}` | {t.Attribute} | {evSummary} |");
        }
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## 二、各维度观察层原始证据逐项锚定");
        sb.AppendLine();
        foreach (var t in _traits)
        {
            sb.AppendLine($"### 🔹 {t.Dimension} ({t.ScoreInt}分 / 可信度: {t.ConfidenceText})");
            sb.AppendLine($"- **深度推断**：{t.Attribute}");
            if (t.Evidence.Count > 0)
            {
                sb.AppendLine("- **亲口原话引用**：");
                foreach (string q in t.Evidence)
                {
                    sb.AppendLine($"  > “{q}”");
                }
            }
            sb.AppendLine();
        }
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"## 三、实战沟通破局战术指南（{_skill.Name}）");
        sb.AppendLine();

        sb.AppendLine($"### 1. {StrategyCard1Title.Text}\n{StrategyMindsetOverviewText.Text}\n");
        sb.AppendLine($"### 2. {StrategyCard2Title.Text}\n{StrategyLeveragePointsText.Text}\n");
        sb.AppendLine($"### 3. {StrategyCard3Title.Text}\n{StrategyTaboosText.Text}\n");
        sb.AppendLine($"### 4. {StrategyCard4Title.Text}\n{StrategyPrescriptionText.Text}\n");

        return sb.ToString();
    }

    private void CopyReportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string report = BuildMarkdownReport();
            Clipboard.SetText(report);
            MessageBox.Show($"已成功将「{_persona.ContactName}」全景画像分析报告复制到剪贴板！\n可直接粘贴至笔记软件或文档中查看。", "报告已复制", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("复制失败: " + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExportReportButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "导出画像分析报告",
            FileName = $"{_persona.ContactName}_全景心智画像报告_{DateTime.Now:yyyyMMdd}.md",
            Filter = "Markdown 报告 (*.md)|*.md|文本文档 (*.txt)|*.txt|所有文件 (*.*)|*.*"
        };

        if (dlg.ShowDialog() == true)
        {
            try
            {
                string report = BuildMarkdownReport();
                File.WriteAllText(dlg.FileName, report, Encoding.UTF8);
                MessageBox.Show($"报告已成功导出保存至：\n{dlg.FileName}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存文件失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
        }
        else if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
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
        ToggleMaximize();
    }

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
        }
        else
        {
            WindowState = WindowState.Maximized;
        }
        UpdateMaximizeButtonVisual();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        UpdateMaximizeButtonVisual();

        if (RootGrid != null)
        {
            // 最大化时 Windows 会向屏幕边界外延伸约 7px 隐藏系统边缘，适度增加内边距保持视觉间距一致
            RootGrid.Margin = WindowState == WindowState.Maximized
                ? new Thickness(7)
                : new Thickness(0);
        }
    }

    private void UpdateMaximizeButtonVisual()
    {
        if (MaximizeButtonIcon == null) return;

        if (WindowState == WindowState.Maximized)
        {
            MaximizeButtonIcon.Text = "🗗";
            if (MaximizeButton != null) MaximizeButton.ToolTip = "还原窗口大小";
        }
        else
        {
            MaximizeButtonIcon.Text = "🗖";
            if (MaximizeButton != null) MaximizeButton.ToolTip = "最大化窗口";
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
