# 原子库说明

## 数据来源

- 公众号推文总数：167 篇
- 最终知识原子：1144 条
- 时间范围：2022-05 ~ 2026-07

## 字段说明

| 字段 | 类型 | 说明 |
|------|------|------|
| id | string | 格式：{季度}_{序号}，如 2026Q3_001 |
| knowledge | string | 提炼后的知识点陈述句 |
| original | string | 推文原文（最多200字） |
| date | string | 发布日期，格式 YYYY-MM-DD |
| topics | string[] | 主题分类 |
| skills | string[] | 关联 Skill |
| type | string | principle / technique / definition / insight / case / method / mindset / anti-pattern |
| confidence | float | 置信度，范围 0.80 ~ 0.95 |

## 数据统计

### type 分布

| type | 数量 | 占比 |
|------|------|------|
| principle | 575 | 50.3% |
| technique | 239 | 20.9% |
| definition | 91 | 8.0% |
| insight | 79 | 6.9% |
| case | 60 | 5.2% |
| method | 49 | 4.3% |
| mindset | 29 | 2.5% |
| anti-pattern | 22 | 1.9% |

### skills 分布

| skill | 数量 |
|------|------|
| tds-date | 385 |
| tds-attraction | 239 |
| tds-chat | 163 |
| tds-mindset | 101 |
| tds-relationship | 85 |
| tds-learn | 69 |
| tds-economics | 55 |
| tds-redpill | 50 |
| tds-approach | 33 |
| tds-frame | 27 |
| tds-growth | 27 |
| tds-persona | 10 |
| tds-showcase | 9 |

### confidence 分布

- 0.9：541 条（47.3%）
- 0.85：142 条（12.4%）
- 0.88：133 条（11.6%）
- 0.8：56 条（4.9%）
- 其余分布在 0.82~0.95 之间

## 文件结构

- `atoms.jsonl` — 全量合并（1144 条）
