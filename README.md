# Desktop Copilot · 桌面副驾

**English** | [简体中文](README.zh-CN.md)

**A human-in-the-loop computer-use layer for the real Windows desktop.**
Screenshot → the model proposes ≤3 actions → you confirm → it executes → it looks again.

![version](https://img.shields.io/badge/version-1.2.1-blue)
![platform](https://img.shields.io/badge/platform-Windows%2010%2F11-lightgrey)
![license](https://img.shields.io/badge/license-MIT-green)
![made by](https://img.shields.io/badge/packaged%20%26%20pushed-by%20an%20AI%20agent-8A2BE2)

> ### 🤖 从打包到提交，全程由 AI 完成（除登录步骤外）
> **人类只负责「grill me」和「yes」。**
>
> *Packaged, documented and pushed by an AI agent — end to end, except the one login step.*
> *The human only showed up to grill it and say yes.*

Unlike a screenshot-every-second agent, every step is one deliberate turn: one snapshot, at most three
actions, one visible result. That keeps the token cost low and keeps a human in the loop by default.

---

## What it does

Ask in plain language, from any DSH conversation:

- "Fill in this form for me"
- "Send that PDF on my desktop to my file-transfer chat"
- "Open the unread WeChat conversation and see what it says"

The plugin exposes three tools:

| Tool | Purpose |
| --- | --- |
| `desktop_capture` | Snapshot the front window or the whole screen, return the UI Automation tree (and OCR text when there is no tree) |
| `desktop_step` | Run up to 3 actions (`click` / `type` / `key` / `scroll` / `drag` / `wait` / `open`), optionally behind a confirmation card |
| `desktop_control` | `status`, `stop`, `focus`, `auto_on` / `auto_off`, `enable` / `disable` |

A floating card, a global kill switch, and a page pill control all of it.

---

## Features

- **Three-choice card** — `1 Do it myself` / `2 Next step` / `3 Reject` (with a reason, which re-plans on the same screen).
- **Global hotkeys** — `Ctrl+Alt+1/2/3` choose without leaving the mouse; `Ctrl+Alt+Esc` stops everything.
- **Execution modes** — Confirm (default) / Auto / Guide-only (draws the boxes, touches nothing).
- **Capture scope** — front window (cheap) or full screen with every visible window tagged by title.
- **OCR fallback** — WeChat 4.x, Electron shells and other self-drawn UIs expose no controls; the plugin
  runs the Windows OCR engine and hands back text plus coordinates.
- **Cheap polling** — `image: false` returns text only, for background "has anything changed?" loops.
- **Danger guard** — delete / install / system settings / password fields / UAC always fall back to a human.
- **Master switch** — pause the whole capability from the page pill; both tools refuse while paused.

---

## Install

```bash
dsh plugin --profile <profile> add link:<path-to-this-repo>
```

Requirements:

- Windows 10/11 with .NET Framework 4.8
- a DSH profile with the `web` UI
- the native helper is deployed automatically to `%LOCALAPPDATA%\dsh-desktop-copilot\` on first use
  (it must live outside the workspace: a process started inside the workspace cannot inject input or
  read cross-process UI Automation)

Refresh the page once after installing — the control pill is injected into the DSH index HTML.

---

## Usage

| Control | Where | What |
| --- | --- | --- |
| Mode pill | bottom-left of the page | master switch, execution mode, capture scope |
| Confirmation card | next to the target | three choices, auto-mode toggle |
| `Ctrl+Alt+1 / 2 / 3` | global | do it myself / next step / reject |
| `Ctrl+Alt+Esc` | global | stop: release input, hide overlays |

---

## Safety rules

- No auto-Enter when the target conversation cannot be read from the window title.
- ≤20 steps per task; retry a failed action once; stop when three steps in a row change nothing.
- Destructive actions (delete, install, system settings, sign-in, payment) always require a human.
- Passwords are never typed by the model.

---

## How it works

```
index.js             host half: the three tools, the step loop, mode routing, page-pill injection
schemas.js           tool parameter / output schemas (plain JSON Schema)
helper/Helper.cs     native helper: overlays, card form, hotkeys, capture, input, clipboard
helper/Uia.cs        legacy in-process UIA (Chromium hides from it; superseded by uia-dump.ps1)
helper/Input.cs      SendInput, clipboard paste, user-activity detection
helper/uia-dump.ps1  PowerShell UIA snapshot (the only path that reads Chromium's MSAA bridge)
helper/ocr-capture.ps1  Windows OCR of one PNG, returning text with bounding boxes
dev/                 offline drivers and probes, not part of the package
```

The host talks to the helper over a local TCP socket (NDJSON), the helper runs as a separate
`winexe` with a message-loop form for global hotkeys and click-through overlay windows.

---

## Known limits

- **Self-drawn UIs** (WeChat, QQ, ChatGPT desktop) have no control tree: text comes from OCR, clicks go
  to estimated coordinates. Emoji, images and message senders are not identified.
- **Taskbar** windows have empty titles and are filtered out of the full-screen snapshot; read the
  taskbar explicitly when you need it.
- **Single monitor** coordinates only.
- Scrolling is sent notch by notch, because several apps coalesce one large wheel delta.

---

## Roadmap

- OCR boxes as first-class click targets (currently coordinates are estimated)
- Taskbar in the full-screen snapshot
- Multi-monitor coordinates
- Drag with modifier keys and path avoidance

---

## 中文说明

一步一步操作真实 Windows 桌面：截图 → 模型出 ≤3 个动作 → 卡片三选一 → 执行 → 复检。

- **执行模式**：确认（默认，每步弹卡）/ 自动（不弹卡，危险动作仍拦截）/ 只指路（只画框）
- **快捷键**：`Ctrl+Alt+1` 自己执行、`Ctrl+Alt+2` 下一步、`Ctrl+Alt+3` 驳回、`Ctrl+Alt+Esc` 停止
- **截图范围**：窗口 / 全屏（全屏会带上窗口归属，适合跨窗口任务）
- **OCR 兜底**：窗口没有可读控件时，自动识别截图文字连同坐标交给模型
- **总开关**：页面左下角胶囊可暂停，暂停后两个工具直接拒绝
- **操控提示条**：执行期间屏幕顶部出现一条低调提示「鲸鱼娘在操控屏幕 · Ctrl+Alt+Esc 停止 · Ctrl+Alt+I 插话」，执行结束自动收起
- **插话快捷键**：`Ctrl+Alt+I` 弹出小输入框，写完按 Enter；内容不打断当前动作，而是在下一步的工具结果里交给模型参考

v1.2 的实测修正：滚轮逐格发送、`pages` 一屏 24 格、纯文字截图 `image: false`、helper 按构建号部署。

---

## License

MIT © 2026 cwx
