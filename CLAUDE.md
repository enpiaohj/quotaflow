# QuotaFlow — Repository Rules

> 本文件是本仓库对 Claude Code 的项目级规则。优先级：
> 用户当前明确指令 > 本文件 > 用户级全局规则（`%USERPROFILE%\.claude\`）> 默认行为。
> 与全局规则冲突时，采用更具体、风险更低的一方，并说明冲突。

## 产品与仓库

| 项 | 值 |
| --- | --- |
| 产品名称 | QuotaFlow |
| 仓库 | `enpiaohj/quotaflow`（Private） |
| 定位 | Windows 11 系统托盘 AI 额度监控，正式独立产品 |
| 本地路径 | `D:\AIProjects\QuotaFlow` |
| 默认分支 | `main` |
| 技术栈 | .NET 8 / WPF / MVVM（CommunityToolkit.Mvvm）/ xUnit |

本仓库自 2026-09-01 起从 `ai-coding-workspace/apps/quotaflow` 拆分为独立仓库，
完整开发历史（49 个提交）与全部版本标签均已保留。

## Branch

- 稳定分支：`main`。
- 小而明确的安全修改允许直接提交 `main`。
- 以下情况建议先开分支：大规模重构、数据格式迁移、架构调整、大功能开发、
  高风险修改、发布流程或构建系统重做。
- 分支命名：`feature/<name>` / `fix/<name>` / `refactor/<name>` / `docs/<name>`。
- **禁止对 `main` 强制推送。**

## Commit

采用 Conventional Commits：`feat` / `fix` / `docs` / `refactor` / `perf` / `test` /
`build` / `ci` / `chore` / `release`。

- 一个 Commit 表达一个明确目的，提交前检查 `git status` 与 `git diff`，避免混入无关文件。
- 提交说明要能独立说明"为什么改"，禁止 `update` / `fix` / `修改` 这类无意义说明。
- 发布提交格式：`release: QuotaFlow vX.Y.Z`。

## Version 与 Tag

- 语义化版本 `MAJOR.MINOR.PATCH`，版本号写在
  `QuotaFlow.Windows.App/QuotaFlow.Windows.App.csproj` 的 `<Version>`。
- Tag 格式 **`vX.Y.Z`**（仓库本身已叫 quotaflow，标签不再重复产品名）。
- 已发布的版本号不得重复或倒退。
- `v1.0.0`～`v1.4.9` 是早期未足够谨慎标注的历史版本，按规则保留不动；
  自 `v0.5.0` 起改用 `0.x`，走向 1.0 的条件见 `docs/` 中的优化计划文档。

## Release

```
代码完成 → 检查 diff → Restore/Build → Tests → 更新 CHANGELOG → 更新版本号
→ Commit → Tag → Push Commit 与 Tag → 创建 GitHub Release → 上传 Artifact → 验证
```

- 构建产物（`.exe` / `.zip` / 安装包）进 **GitHub Releases，不进 Git 历史**。
- 本地 `release/vX.Y.Z/` 快照目录不纳入版本库（已在 `.gitignore` 中排除）。

## Build / Test

```powershell
dotnet build -c Release        # 必须 0 错误 0 警告
dotnet test  -c Release        # 必须全绿
powershell -File scripts/release.ps1   # 打包
```

**构建必须看退出码**，不要只 grep 错误关键字——应用正在运行时文件被占用会报
`MSB3021`，只过滤 `error CS` 会把它漏掉，把失败误读成成功（实际发生过）。
打包/构建前先 `Stop-Process -Name QuotaFlow -Force`。

## 硬性安全规则

- **密钥只进 Windows 凭据管理器**（`SecureCredentialStore`），绝不写入 JSON、
  源码、日志、测试或发布目录。
- 配置以 DPAPI 加密信封落盘，不得明文内嵌。
- **绝不把未知数据显示成 0% 或 0.00** —— 宁可隐藏或显示错误，也不能显示假数据。
- 不提供 Claude/Codex 密码输入框，不做网页登录抓取。
- 跨设备备份包 `*.qfbackup` 含加密后的密钥，不得进版本库。
- 单个平台失败不得影响其它平台。

## 修改前检查

1. 先理解现有实现，优先最小必要改动，定位并修复根因。
2. 不隐藏 Error / Warning，不删除失败测试来"解决"问题。
3. 涉及并发、异步、线程安全时显式检查风险。
4. **设置持久化改动必须走 `AppSettingsStore.Update`（读-改-写）**，
   不要整份写回一份可能已经陈旧的快照——两次数据丢失事故都源于此。
5. UI 改动**必须运行时验证**：编译通过不代表功能可用（自定义模板缺 ToggleButton、
   `TabControl` 内容宿主未命名 `PART_SelectedContentHost`，两者编译都是 0 错误 0 警告）。
6. 涉及持久化的验证**直接读存储**，不要从界面推断磁盘状态。

## 发布前检查

- `git status` / `git diff` 确认无意外文件。
- Build 0 错误 0 警告，测试全绿。
- 发布包内不得含 settings / cache / 凭据 / 日志 / pdb / `.qfbackup`。
- CHANGELOG 基于真实修改撰写，不写"优化部分功能"这类模糊描述。

## 文档命名

- 标准文件保留通用名：`README.md` / `CHANGELOG.md` / `CLAUDE.md`。
- 其它文档：`YYYY-MM-DD-内容-vX.Y.md`，放 `docs/`。

## 禁止的 Git 操作

未经用户明确授权，不得执行：

```
git push --force      git reset --hard      git clean -fd
git rebase            git branch -D         git tag -d
```

`git push` 本身也需要明确授权。

## 备份

任何历史重写、目录删除或 filter 操作之前，必须先用 `git bundle` 做完整备份并
`git bundle verify` 验证，备份放 `D:\AIProjects\_Backup\`，且不得删除历史备份。
