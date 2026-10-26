using System;
using System.Collections.Generic;
using WeChatCopilot.Core.Abstractions;
using WeChatCopilot.Core.Models;
using WeChatCopilot.Data;
using Xunit;

namespace WeChatCopilot.Tests;

public class WeChatAnalyticsServiceTests
{
    [Fact]
    public void AnalyzeRelationship_EmptyMessages_ReturnsZeroScoreAndFriendlyAdvice()
    {
        var stats = WeChatAnalyticsService.AnalyzeRelationship("测试好友", Array.Empty<HistoryMessage>());

        Assert.Equal("测试好友", stats.ContactName);
        Assert.Equal(0, stats.TotalMessages);
        Assert.Equal(0, stats.OutgoingCount);
        Assert.Equal(0, stats.IncomingCount);
        Assert.Equal(0, stats.TemperatureScore);
        Assert.Contains("暂无互动", stats.TemperatureLevel);
    }

    [Fact]
    public void AnalyzeRelationship_BalancedActiveMessages_CalculatesMetricsAccurately()
    {
        var baseTime = new DateTime(2026, 10, 26, 23, 30, 0, DateTimeKind.Local);
        var messages = new List<HistoryMessage>
        {
            new(MessageRole.Incoming, "在忙吗？", baseTime),
            new(MessageRole.Outgoing, "不忙，刚忙完工作呢", baseTime.AddMinutes(5)),
            new(MessageRole.Incoming, "明天一起去吃烤肉吧", baseTime.AddMinutes(10)),
            new(MessageRole.Outgoing, "好啊，明晚见！", baseTime.AddMinutes(15))
        };

        var stats = WeChatAnalyticsService.AnalyzeRelationship("测试好友", messages);

        Assert.Equal(4, stats.TotalMessages);
        Assert.Equal(2, stats.OutgoingCount);
        Assert.Equal(2, stats.IncomingCount);
        Assert.Equal(50.0, stats.OutgoingRatio);
        Assert.True(stats.LateNightCount > 0, "23:30 should count as late night");
        Assert.True(stats.AverageResponseMinutes is > 0 and <= 10);
        Assert.True(stats.TemperatureScore >= 5);
        Assert.False(string.IsNullOrWhiteSpace(stats.TemperatureLevel));
        Assert.False(string.IsNullOrWhiteSpace(stats.SummaryAdvice));
    }

    [Fact]
    public void BuildUnreadRadar_FiltersAndSortsUnreadSessions()
    {
        var now = DateTime.UtcNow;
        var sessions = new List<WeChatSessionSummary>
        {
            new("wx_01", "小张", "张三", "张三", UnreadCount: 0, Summary: "好的", LastActiveTime: now.AddHours(-3)),
            new("wx_02", "小李", "李四", "李四", UnreadCount: 6, Summary: "紧急方案发你了", LastActiveTime: now.AddMinutes(-5)),
            new("wx_03", "小王", "王五", "王五", UnreadCount: 2, Summary: "在吗？", LastActiveTime: now.AddMinutes(-30))
        };

        var radar = WeChatAnalyticsService.BuildUnreadRadar(sessions);

        Assert.Equal(2, radar.Count);
        Assert.Equal("小李", radar[0].DisplayName);
        Assert.Equal(6, radar[0].UnreadCount);
        Assert.Contains("紧急", radar[0].FollowUpPriority);

        Assert.Equal("小王", radar[1].DisplayName);
        Assert.Equal(2, radar[1].UnreadCount);
        Assert.Contains("普通", radar[1].FollowUpPriority);
    }

    [Fact]
    public void ExtractCommitments_IdentifiesAppointmentsAndDeliveries()
    {
        var messages = new List<HistoryMessage>
        {
            new(MessageRole.Outgoing, "没问题，明天一起吃饭聊聊", DateTime.UtcNow.AddHours(-2)),
            new(MessageRole.Incoming, "稍后把合同发给你看下", DateTime.UtcNow.AddHours(-1)),
            new(MessageRole.Outgoing, "好的，我一定准时到，别忘带资料", DateTime.UtcNow),
            new(MessageRole.Incoming, "日常闲聊哈哈哈哈", DateTime.UtcNow)
        };

        var commitments = WeChatAnalyticsService.ExtractCommitments(messages);

        Assert.True(commitments.Count >= 2);
        Assert.Contains(commitments, c => c.Tag == "📅 聚会约定");
        Assert.Contains(commitments, c => c.Tag == "📑 交付承诺");
    }

    [Fact]
    public void SearchMessages_FindsMatchesCaseInsensitively()
    {
        var messages = new List<HistoryMessage>
        {
            new(MessageRole.Incoming, "今晚去吃Hotpot火锅吗？", DateTime.UtcNow.AddMinutes(-30)),
            new(MessageRole.Outgoing, "可以啊，哪家HOTPOT？", DateTime.UtcNow.AddMinutes(-20)),
            new(MessageRole.Incoming, "就是天街那家", DateTime.UtcNow.AddMinutes(-10))
        };

        var results = WeChatAnalyticsService.SearchMessages("hotpot", messages, "张三");

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("张三", r.ContactName));
    }
}
