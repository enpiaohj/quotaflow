# Changelog

版本号遵循[语义化版本](https://semver.org/lang/zh-CN/)（`主版本.次版本.修订号`），每次发布打对应的 Git tag（`vX.Y.Z`）。

## [1.0.0] - 2026-08-27

首个可用版本。

### 新增

- Claude / Codex / MiniMax / DeepSeek 四平台额度查询，真实接口联调通过（详见 `README.md`）。
- Windows 系统托盘常驻，弹出面板展示四张平台卡片，固定顺序 Claude → Codex → MiniMax → DeepSeek。
- 本机 OAuth 凭据只读复用（Claude Code / Codex CLI），不要求重新登录、不收集账号密码。
- MiniMax / DeepSeek API Key 经 Windows 凭据管理器加密存储；设置页支持通过 Windows Hello（或密码兜底验证）查看已保存的明文 Key。
- 缓存先行展示 + 后台刷新；同一平台并发刷新请求自动去重；单平台失败不影响其他平台。
- 浅色 / 深色 / 跟随系统三态主题，125%/150%/200% DPI 适配。
- 品牌图标：渐变圆角背景 + "额度环"设计，覆盖托盘图标、应用图标、窗口图标。
- 71 个 xUnit 单元测试，覆盖额度换算、凭据解析、错误分类、缓存往返、并发去重等场景。

### 已知限制

- Claude/Codex 用量接口为非公开接口，未来可能变化（见 `README.md` "已知风险"）。
- 托盘面板定位为启发式算法，非 Shell 层面精确坐标查询。
- 视觉效果为手写 Fluent 风格模拟，未使用系统级 Mica/Acrylic 材质。
