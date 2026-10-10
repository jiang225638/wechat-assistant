using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using WeChatCopilot.Core.Ai;
using WeChatCopilot.Core.Models;

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

        Loaded += PersonaAnalyticsWindow_Loaded;
    }

    private void PersonaAnalyticsWindow_Loaded(object sender, RoutedEventArgs e)
    {
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

            var tb = new TextBlock
            {
                Text = $"{dimName}\n{_traits[i].ScoreInt}分",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = _traits[i].Score >= 75
                    ? new SolidColorBrush(Color.FromRgb(167, 243, 208))
                    : _traits[i].Score < 40
                        ? new SolidColorBrush(Color.FromRgb(254, 205, 211))
                        : new SolidColorBrush(Color.FromRgb(226, 232, 240)),
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
        foreach (var t in sorted)
        {
            var rowGrid = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });

            // 1. 维度名称
            var dimTb = new TextBlock
            {
                Text = t.Dimension,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = t.Dimension
            };
            Grid.SetColumn(dimTb, 0);
            rowGrid.Children.Add(dimTb);

            // 2. 柱状条轨道与彩色进度
            var trackBorder = new Border
            {
                Height = 14,
                Background = new SolidColorBrush(Color.FromRgb(26, 32, 44)),
                CornerRadius = new CornerRadius(3),
                VerticalAlignment = VerticalAlignment.Center
            };

            var barFill = new Border
            {
                Height = 14,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(3),
                Width = Math.Clamp(t.Score, 5, 100) * 2.3 // 比例缩放宽度
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

            trackBorder.Child = barFill;
            Grid.SetColumn(trackBorder, 1);
            rowGrid.Children.Add(trackBorder);

            // 3. 得分数值
            var scoreTb = new TextBlock
            {
                Text = $"{t.ScoreInt}分",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
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
            var scoreTb = new TextBlock
            {
                Text = $"{_traits[i].ScoreInt}",
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
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
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
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

        var top = _traits.OrderByDescending(t => t.Score).First();
        var low = _traits.OrderBy(t => t.Score).First();
        double spread = top.Score - low.Score;
        double avgScore = _traits.Average(t => t.Score);
        double avgConf = _traits.Average(t => t.Confidence);

        StatTopDimText.Text = top.Dimension;
        StatTopScoreText.Text = $"{top.ScoreInt}分 (显著优势)";

        StatLowDimText.Text = low.Dimension;
        StatLowScoreText.Text = $"{low.ScoreInt}分 (心理防备区)";

        StatSpreadText.Text = $"{spread:0}分落差";
        StatAvgScoreText.Text = $"{avgScore:0.0}分";
        StatAvgConfidenceText.Text = $"加权可信度: {avgConf:0.00}";
    }

    // ===================== Tab 3: 实战沟通破局战略报告 =====================

    private void PopulateStrategyPlaybook()
    {
        if (_traits.Count == 0) return;

        StrategySubTitle.Text = $"结合【{_skill.Icon} {_skill.Name}】方法论模型与 {_persona.ContactName} 的真实特质分布，生成的破局沟通战术：";

        var topTraits = _traits.OrderByDescending(t => t.Score).Take(2).ToList();
        var lowTraits = _traits.OrderBy(t => t.Score).Take(2).ToList();

        // 1. 核心心智模式概貌
        var sbOverview = new StringBuilder();
        sbOverview.AppendLine($"• 底层心理结构：对方在【{string.Join("】与【", topTraits.Select(t => t.Dimension))}】表现出鲜明的高位特质（得分均超 75+），表明其具备极强的个性主线与自洽诉求。");
        foreach (var t in topTraits)
        {
            sbOverview.AppendLine($"  - 【{t.Dimension}】（{t.ScoreInt}分）：{t.Attribute}");
        }
        StrategyMindsetOverviewText.Text = sbOverview.ToString().TrimEnd();

        // 2. 顺势借力沟通点
        var sbLeverage = new StringBuilder();
        string tones = _skill.SuggestedTones is { Count: > 0 } ? string.Join("、", _skill.SuggestedTones) : "真诚同频、高位自洽";
        sbLeverage.AppendLine($"• 推荐破局调性：{tones}。");
        if (!string.IsNullOrWhiteSpace(_skill.AdviceGuideline))
        {
            sbLeverage.AppendLine($"• 方法论指引：{_skill.AdviceGuideline.Split('\n')[0].Trim()}");
        }
        StrategyLeveragePointsText.Text = sbLeverage.ToString().TrimEnd();

        // 3. 沟通雷区与防卫
        var sbTaboos = new StringBuilder();
        sbTaboos.AppendLine($"• 防御反模式：对方在【{string.Join("】与【", lowTraits.Select(t => t.Dimension))}】展现出极低的耐受度或强烈的防御机制。");
        foreach (var t in lowTraits)
        {
            sbTaboos.AppendLine($"  - 严厉禁忌【{t.Dimension}】：严禁触发其防御机制，杜绝此类沟通表现（特质表现：{t.Attribute}）。");
        }
        StrategyTaboosText.Text = sbTaboos.ToString().TrimEnd();

        // 4. 专属行动处方
        var sbPrescription = new StringBuilder();
        sbPrescription.AppendLine($"1. 顺应认知节奏：在聊及工作、生活或情感议题时，切忌正面强行硬顶其【{topTraits[0].Dimension}】，而应先认同其底层逻辑，再顺水推舟给出建议。");
        sbPrescription.AppendLine($"2. 避免低位讨好：坚守自身高位框架与清晰边界感，杜绝秒回长篇大论、自我解释与查户口式盘问。");
        sbPrescription.AppendLine($"3. 闭环行动指引：若推进邀约或合作，用'选择题'代替'填空题'，以低需求感姿态给予对方选择权。");
        StrategyPrescriptionText.Text = sbPrescription.ToString().TrimEnd();
    }

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
        sb.AppendLine("## 三、AI 实战沟通破局战术指南");
        sb.AppendLine();
        sb.AppendLine($"### 1. 核心心智模型总评\n{StrategyMindsetOverviewText.Text}\n");
        sb.AppendLine($"### 2. 顺势借力沟通策略\n{StrategyLeveragePointsText.Text}\n");
        sb.AppendLine($"### 3. 绝对禁忌与雷区\n{StrategyTaboosText.Text}\n");
        sb.AppendLine($"### 4. 实战行动处方\n{StrategyPrescriptionText.Text}\n");

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
    }

    private void UpdateMaximizeButtonVisual()
    {
        if (MaximizeButton == null) return;

        if (WindowState == WindowState.Maximized)
        {
            MaximizeButton.Content = "🗗 还原";
            MaximizeButton.ToolTip = "还原窗口大小";
        }
        else
        {
            MaximizeButton.Content = "🗖 最大化";
            MaximizeButton.ToolTip = "最大化窗口";
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
