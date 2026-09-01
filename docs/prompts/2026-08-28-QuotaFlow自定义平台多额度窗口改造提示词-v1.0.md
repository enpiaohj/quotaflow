# QuotaFlow 自定义平台多额度窗口改造提示词

> 日期：2026-08-28  
> 文档版本：v1.0  
> 适用项目：QuotaFlow v1.0.5  
> 使用方式：将本文档完整提交给 Claude Code 执行。

---

## 任务目标

请修复 QuotaFlow 自定义平台的多额度窗口设计缺陷。

当前“添加自定义平台”只能配置一组数据语义、JSON 取值路径和窗口重置时间路径，相当于假设“一个平台只有一个额度指标”。该结构无法正确支持 OpenCode GO、MiniMax 等具有多个独立额度窗口的平台。

请将数据模型由：

```text
一个平台 → 一个额度指标
```

调整为：

```text
一个平台 → 一次接口请求 → 多个额度窗口
```

同一平台只请求一次接口，再从同一份 JSON 响应中解析多个额度窗口。不得要求用户将 5 小时、每周、每月额度分别创建成三个重复平台。

## OpenCode GO 示例

接口：

```http
GET https://opencode.ai/zen/go/v1/usage
Authorization: Bearer <API_KEY>
```

典型返回结构：

```json
{
  "usage": {
    "rolling": {
      "percent": 19.5,
      "resetsAt": "2026-08-28T12:00:00Z"
    },
    "weekly": {
      "percent": 29.7,
      "resetsAt": "2026-09-01T00:00:00Z"
    },
    "monthly": {
      "percent": 25.0,
      "resetsAt": "2026-09-01T00:00:00Z"
    }
  }
}
```

字段映射：

| 窗口 | 数值路径 | 重置时间路径 |
|---|---|---|
| 5 小时 | `usage.rolling.percent` | `usage.rolling.resetsAt` |
| 每周 | `usage.weekly.percent` | `usage.weekly.resetsAt` |
| 每月 | `usage.monthly.percent` | `usage.monthly.resetsAt` |

当前只能填写一个取值路径，导致 OpenCode GO 卡片只能显示一条笼统的“额度 100%”，无法同时显示三个真实额度窗口。

## 配置界面改造

保留现有平台级配置：

- 显示名称；
- 接口地址；
- 鉴权方式；
- API Key；
- 自定义请求头。

将现有单一的“数据语义、取值路径、重置时间路径”改成可重复配置的“额度窗口”列表。

每个额度窗口包含：

1. 窗口名称；
2. 数据语义；
3. JSON 数值路径；
4. 重置时间路径；
5. 可选的额度上限；
6. 可选的额度上限 JSON 路径；
7. 可选的数值单位；
8. 重置时间类型；
9. 排序序号。

交互要求：

- 至少保留一个额度窗口；
- 支持添加、删除和调整顺序；
- 建议最多支持 5 个窗口；
- 保存前验证窗口名称和取值路径；
- 同一平台内不允许出现相同窗口名称；
- 使用可折叠卡片，避免配置页面过于拥挤；
- 新增窗口默认展开，已有窗口默认收起并显示摘要；
- 保持当前简洁、轻量的 UI 风格。

## 数据模型改造

不要继续在平台模型上只保存单一的：

```text
valuePath
resetPath
dataSemantics
```

新增通用额度窗口模型，例如：

```text
CustomQuotaWindow
- id
- name
- dataSemantics
- valuePath
- resetPath
- resetTimeType
- limitPath
- fixedLimit
- unit
- sortOrder
```

平台模型调整为：

```text
CustomPlatform
- id
- displayName
- endpoint
- authConfiguration
- requestHeaders
- quotaWindows: [CustomQuotaWindow]
```

字段名称应结合现有项目架构和编码规范确定，不要机械照抄示例。

## 数据语义与计算

每个额度窗口可以独立选择：

- 已使用百分比；
- 剩余百分比；
- 余额；
- 已使用数值；
- 剩余数值。

统一转换成内部展示模型。

### 已使用百分比

```text
usedPercent = API 返回值
remainingPercent = 100 - usedPercent
```

### 剩余百分比

```text
remainingPercent = API 返回值
usedPercent = 100 - remainingPercent
```

### 已使用数值

需要同时配置固定额度上限或上限 JSON 路径：

```text
usedPercent = usedValue / limitValue × 100
remainingPercent = 100 - usedPercent
```

### 剩余数值

```text
remainingPercent = remainingValue / limitValue × 100
usedPercent = 100 - remainingPercent
```

### 余额

余额类指标不强制转换成百分比，沿用 DeepSeek 当前的余额展示方式。

所有百分比最终限制在 `0...100` 范围内。路径不存在、类型错误或无法计算时，只标记对应窗口异常，不得导致整个平台加载失败。

## 请求与解析机制

同一个自定义平台刷新时：

1. 只发送一次网络请求；
2. 获取完整 JSON；
3. 遍历该平台的所有额度窗口；
4. 根据各窗口的 JSON 路径分别取值；
5. 独立解析重置时间；
6. 生成多个窗口展示数据；
7. 任意一个窗口解析失败时，其他成功窗口继续正常显示。

必须避免因为配置三个窗口而向服务端连续发送三次相同请求。

JSON 路径继续兼容：

```text
data.usage.percent
data.items[0].quota
usage.rolling.percent
```

如果当前解析器不支持数组下标，请在此次改造中补齐。

## OpenCode GO 内置模板

在自定义平台中增加“OpenCode GO”快捷模板，自动填入：

```text
显示名称：OpenCode GO
接口地址：https://opencode.ai/zen/go/v1/usage
鉴权方式：Bearer Token
```

自动创建三个额度窗口：

### 5 小时

```text
窗口名称：5 小时
数据语义：已使用百分比
取值路径：usage.rolling.percent
重置时间路径：usage.rolling.resetsAt
重置时间类型：绝对时间
```

### 每周

```text
窗口名称：每周
数据语义：已使用百分比
取值路径：usage.weekly.percent
重置时间路径：usage.weekly.resetsAt
重置时间类型：绝对时间
```

### 每月

```text
窗口名称：每月
数据语义：已使用百分比
取值路径：usage.monthly.percent
重置时间路径：usage.monthly.resetsAt
重置时间类型：绝对时间
```

用户只需填写并保存 API Key。不得将 API Key 硬编码到模板、普通配置文件或日志中。

## 主面板展示

OpenCode GO 应像 MiniMax 一样，在同一张平台卡片内显示多行额度：

```text
OpenCode GO                         正常

5 小时   ━━━━━━━━━━━━━━━━  19%
         3小时42分后重置

每周     ━━━━━━━━━━━━━━━━  30%
         4天后重置

每月     ━━━━━━━━━━━━━━━━  25%
         18天后重置
```

展示要求：

- 一个平台只显示一张卡片；
- 每个窗口显示名称、进度条、百分比和重置时间；
- 平台整体状态取所有窗口中的最高已用百分比作为风险等级；
- 任一窗口达到警告阈值，平台卡片显示对应警告；
- 单个窗口异常时仅在该行显示“数据不可用”；
- 平台更新时间只显示一次；
- 卡片高度根据窗口数量自适应；
- 窄窗口及高 DPI 下不得截断或重叠；
- 不再显示笼统的“额度 100%”。

## 重置时间兼容

支持以下格式：

- Unix 秒；
- Unix 毫秒；
- ISO 8601 时间字符串；
- `resetInSec` 剩余秒数。

为窗口增加重置时间类型：

- 自动识别；
- 绝对时间；
- 剩余秒数。

OpenCode GO 模板默认使用 `resetsAt` 和“绝对时间”。

## 旧配置迁移

必须兼容 v1.0.5 已保存的自定义平台。

旧配置中的：

```text
valuePath
resetPath
dataSemantics
```

加载时自动迁移为：

```text
quotaWindows[0]
```

迁移要求：

- 不丢失平台配置；
- 不丢失 API Key；
- 不要求用户重新配置；
- 不产生重复平台；
- 迁移后能够正常刷新；
- 保存新格式前提供安全回退或备份；
- 日志中不得输出明文 Key。

## 测试要求

至少补充以下测试：

1. 单窗口旧配置迁移；
2. OpenCode GO 三窗口解析；
3. 同一平台只发起一次请求；
4. 某一个 JSON 路径不存在；
5. 某一个字段不是数字；
6. 百分比超出 `0...100`；
7. ISO 8601 重置时间；
8. Unix 秒和毫秒时间；
9. `resetInSec` 相对时间；
10. 数组下标 JSON 路径；
11. 已使用百分比与剩余百分比换算；
12. 已使用数值配合固定上限计算；
13. 单窗口异常不影响其他窗口；
14. API Key 不出现在日志、错误信息或界面明文中；
15. 主面板多窗口布局在窄窗口和高 DPI 下无截断。

## 实施约束

开始修改前，先检查现有项目的：

- 数据模型；
- 网络请求层；
- 凭据存储；
- JSONPath 解析器；
- 状态和阈值判断逻辑；
- MiniMax 多窗口展示实现；
- 自定义平台配置持久化及迁移方式。

优先复用 MiniMax 已有的多额度窗口 UI 和状态计算能力，但不要把 MiniMax 专属字段硬编码进通用模型。

禁止：

- 为 OpenCode GO 堆叠平台专属临时代码；
- 将三个额度窗口实现成三个平台；
- 每个窗口重复请求同一个接口；
- 破坏现有内置平台；
- 在日志或配置明文中泄露 API Key；
- 只修改界面而不升级底层数据模型；
- 只写演示数据而不接入真实接口。

## 版本与交付要求

按项目既有版本规范升级版本号，建议：

```text
v1.1.0
```

这是自定义平台数据模型和能力升级，不属于简单补丁。

完成后必须：

1. 编译项目；
2. 运行现有测试和新增测试；
3. 修复本次改造引入的错误；
4. 验证旧配置自动迁移；
5. 使用脱敏模拟响应验证 OpenCode GO 三个窗口；
6. 检查主面板和配置页布局；
7. 输出修改文件清单；
8. 输出数据迁移说明；
9. 输出测试结果；
10. 输出仍存在的限制。

请先分析现有实现并给出简短改造计划，然后直接完成代码修改和验证，不要只提供建议。
