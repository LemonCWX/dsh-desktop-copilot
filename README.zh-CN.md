# 桌面副驾 · Desktop Copilot

[English](README.md) | **简体中文**

**面向真实 Windows 桌面的人机协同 computer-use 层。**
截图 → 模型出 ≤3 个动作 → 你确认 → 它执行 → 再看一眼。

![version](https://img.shields.io/badge/version-1.2.1-blue)
![platform](https://img.shields.io/badge/platform-Windows%2010%2F11-lightgrey)
![license](https://img.shields.io/badge/license-MIT-green)
![made by](https://img.shields.io/badge/packaged%20%26%20pushed-by%20an%20AI%20agent-8A2BE2)

> ### 🤖 从打包到提交，全程由 AI 完成（除登录步骤外）
> **人类只负责「grill me」和「yes」。**

它不是「每秒截一张图」的代理：每一步都是一次有意的往返——一张快照、最多三个动作、一个看得见的结果。
因此 token 花得少，而且默认每一步都留着人确认的余地。

---

## 它能干什么

在任何 DSH 对话里直接用大白话说任务：

- 「帮我把这个表单填了」
- 「把桌面那个 PDF 发给文件传输助手」
- 「把微信里未读的聊天点开看看」

插件提供三个工具：

| 工具 | 作用 |
| --- | --- |
| `desktop_capture` | 截取前台窗口或整屏，返回 UI Automation 控件树；没有控件树时返回 OCR 文字 |
| `desktop_step` | 执行最多 3 个动作（`click` / `type` / `key` / `scroll` / `drag` / `wait` / `open`），可选走确认卡片 |
| `desktop_control` | `status`、`stop`、`focus`、`auto_on` / `auto_off`、`enable` / `disable` |

浮动卡片、全局急停键、页面左下角胶囊共同控制整个流程。

---

## 功能

- **三选一卡片**：`1 自己执行` / `2 下一步` / `3 驳回`（可写原因，同图重新规划）
- **全局快捷键**：`Ctrl+Alt+1/2/3` 不用离开鼠标就能选；`Ctrl+Alt+Esc` 全部停下
- **三种执行模式**：确认（默认）/ 自动 / 只指路（只画框，不碰任何东西）
- **截图范围**：前台窗口（省钱）或整屏（每个可见窗口带标题归属）
- **OCR 兜底**：微信 4.x、Electron 壳等自绘界面没有控件，插件调用 Windows OCR 返回文字与坐标
- **便宜的轮询**：`image: false` 只回文字，用于后台「有没有变化」的循环
- **危险拦截**：删除 / 安装 / 系统设置 / 密码框 / UAC 一律退回人工
- **总开关**：可从页面胶囊暂停整个能力，暂停后两个工具直接拒绝

---

## 安装

```bash
dsh plugin --profile <profile> add link:<本仓库路径>
```

依赖：

- Windows 10/11，.NET Framework 4.8
- 带 `web` 界面的 DSH profile
- 原生 helper 会在首次使用时自动部署到 `%LOCALAPPDATA%\dsh-desktop-copilot\`
  （必须放在工作区之外：从工作区里启动的进程既不能注入输入，也读不到跨进程 UI Automation）

安装后**刷新一次页面**——控制胶囊是注入到 DSH 索引 HTML 里的。

---

## 使用

| 控件 | 位置 | 作用 |
| --- | --- | --- |
| 模式胶囊 | 页面左下角 | 总开关、执行模式、截图范围 |
| 确认卡片 | 目标旁边 | 三选一、自动模式开关 |
| `Ctrl+Alt+1 / 2 / 3` | 全局 | 自己执行 / 下一步 / 驳回 |
| `Ctrl+Alt+Esc` | 全局 | 停止：释放输入、撤掉高亮 |

---

## 安全规则

- 读不到窗口标题时，不自动回车发送，先让用户确认目标对话
- 每任务 ≤20 步；同一动作失败重试 1 次后停；连续 3 步界面无变化停手
- 删除 / 安装 / 改系统设置 / 登录 / 支付：一律人工确认
- 密码永远不由模型输入

---

## 实现结构

```
index.js             Host 半：三个工具、步骤循环、模式路由、页面胶囊注入
schemas.js           工具参数与输出 schema（标准 JSON Schema）
helper/Helper.cs     原生 helper：覆盖层、卡片、快捷键、截图、注入、剪贴板
helper/Uia.cs        遗留的进程内 UIA（Chromium 读不到，已让位于 uia-dump.ps1）
helper/Input.cs      SendInput、剪贴板粘贴、用户活动检测
helper/uia-dump.ps1  PowerShell UIA 快照（唯一能读通 Chromium MSAA 桥的路径）
helper/ocr-capture.ps1  对单张 PNG 做 Windows OCR，返回文字与外接框
dev/                 离线驱动与探针，不随包安装
```

Host 与 helper 之间走本地 TCP（NDJSON）；helper 是独立的 `winexe`，带消息循环窗体，
负责全局热键和点击穿透的覆盖层。

---

## 已知限制

- **自绘界面**（微信、QQ、ChatGPT 桌面端）没有控件树：文字靠 OCR，点击靠估算坐标，表情、图片和发言人身份都识别不了
- **任务栏**窗口标题为空，会被整屏快照过滤掉；需要时单独读任务栏
- **只支持单显示器**坐标
- 滚轮是逐格发送的，因为不少程序会把一个大的 wheel delta 合并掉

---

## 路线图

- OCR 文字框升级为一等点击目标（现在坐标是估的）
- 任务栏进入整屏快照
- 多显示器坐标
- 带修饰键、带路径避让的拖拽

---

## 许可

MIT © 2026 cwx
