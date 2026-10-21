/**
 * Desktop Copilot: a one-step-at-a-time desktop driver.
 *
 * Host half. Owns the native helper process (helper/DesktopCopilot.exe) and
 * exposes three model tools:
 *   desktop_capture  - screenshot + UI Automation element list of the front window
 *   desktop_step     - show the highlight boxes and the 3-way card, then act on the choice
 *   desktop_control  - status / stop / auto switch
 *
 * The helper talks newline-delimited JSON over a local TCP socket.
 */
import { spawn } from 'node:child_process'
import { copyFileSync, existsSync, mkdirSync, readdirSync, rmSync, statSync } from 'node:fs'
import net from 'node:net'
import { homedir } from 'node:os'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import {
  CAPTURE_PARAMETERS, CAPTURE_OUTPUT, STEP_PARAMETERS, STEP_OUTPUT, CONTROL_PARAMETERS, CONTROL_OUTPUT,
} from './schemas.js'

const packageRoot = dirname(fileURLToPath(import.meta.url))
const bundledHelper = join(packageRoot, 'helper', 'DesktopCopilot.exe')

/**
 * A process launched from the workspace inherits the DSH sandbox identity: it cannot
 * write outside the workspace, cannot inject input, and cannot read other processes'
 * UI Automation trees. Running the helper from %LOCALAPPDATA% lifts all three, so the
 * host deploys a copy there and spawns that one.
 */
const deployDir = join(process.env.LOCALAPPDATA ?? join(homedir(), 'AppData', 'Local'), 'dsh-desktop-copilot')

function deployHelper() {
  try {
    mkdirSync(deployDir, { recursive: true })
    // Scripts are read on every call, so they can be replaced in place.
    for (const name of ['uia-dump.ps1', 'ocr-capture.ps1']) {
      const source = join(packageRoot, 'helper', name)
      const target = join(deployDir, name)
      const current = existsSync(target) && existsSync(source)
        && statSync(target).size === statSync(source).size
        && statSync(target).mtimeMs >= statSync(source).mtimeMs
      if (!current) copyFileSync(source, target)
    }
    // The running helper locks its own exe, so each build gets its own file name.
    const source = join(packageRoot, 'helper', 'DesktopCopilot.exe')
    const tag = Math.round(statSync(source).mtimeMs).toString(36)
    const exeName = `DesktopCopilot-${tag}.exe`
    const target = join(deployDir, exeName)
    if (!existsSync(target) || statSync(target).size !== statSync(source).size) copyFileSync(source, target)
    for (const name of readdirSync(deployDir)) {
      if (/^DesktopCopilot-.+\.exe$/.test(name) && name !== exeName) {
        try { rmSync(join(deployDir, name), { force: true }) } catch { }
      }
    }
    return { exe: target, deployed: true }
  } catch (error) {
    console.warn('[desktop-copilot] helper deployment failed, running in place:', error?.message ?? String(error))
    return { exe: bundledHelper, deployed: false }
  }
}

const MAX_ELEMENTS = 120
const MAX_ACTIONS = 3
const MAX_STEPS = 20

const DANGER = ['删除', '移至回收站', '清空', '格式化', '卸载', '安装', '注销', '退出登录', '登录', '注册', '支付', '转账', '重置', '关机', '重启',
  'delete', 'remove', 'uninstall', 'install', 'format', 'erase', 'sign out', 'log out', 'pay', 'checkout', 'transfer', 'reset']

/** One serialized client for the helper process. */
class HelperClient {
  constructor(exePath) {
    this.exePath = exePath
    this.child = undefined
    this.socket = undefined
    this.buffer = ''
    this.pending = new Map()
    this.nextId = 1
  }

  async start() {
    if (this.socket) return
    const server = net.createServer()
    await new Promise((resolve, reject) => {
      server.once('error', reject)
      server.listen(0, '127.0.0.1', resolve)
    })
    const port = server.address().port
    const token = `dc-${process.pid}-${Date.now()}`
    const child = spawn(this.exePath, ['--port', String(port), '--token', token], { stdio: 'ignore', windowsHide: true })
    child.on('exit', () => { this.child = undefined; this.socket = undefined })
    this.child = child
    const socket = await new Promise((resolve, reject) => {
      const timer = setTimeout(() => { try { server.close() } catch { } reject(new Error('desktop helper did not connect within 8s')) }, 8000)
      server.once('connection', (s) => { clearTimeout(timer); server.close(); resolve(s) })
      server.once('error', (e) => { clearTimeout(timer); reject(e) })
    })
    this.socket = socket
    socket.setNoDelay(true)
    socket.on('data', (chunk) => this.onData(chunk))
    socket.on('close', () => this.onClose())
    socket.on('error', () => { })
  }

  onClose() {
    this.socket = undefined
    for (const [, entry] of this.pending) { clearTimeout(entry.timer); entry.reject(new Error('desktop helper disconnected')) }
    this.pending.clear()
    // The helper owns the global hotkeys, so a dead helper means the stop/interject keys
    // silently stop working. Bring it back unless the plugin is unloading.
    if (this.keepAlive && !this.stopping) {
      clearTimeout(this.reviveTimer)
      this.reviveTimer = setTimeout(() => { this.start().catch(() => { }) }, 1500)
    }
  }

  onData(chunk) {
    this.buffer += chunk.toString('utf8')
    let index
    while ((index = this.buffer.indexOf('\n')) >= 0) {
      const line = this.buffer.slice(0, index)
      this.buffer = this.buffer.slice(index + 1)
      if (!line.trim()) continue
      let message
      try { message = JSON.parse(line) } catch { continue }
      if (message.type === 'event') continue
      // Unsolicited: the user typed into the interject box. No request is waiting.
      if (message.id === '__interject') {
        const text = message.result && typeof message.result.text === 'string' ? message.result.text.trim() : ''
        if (text && this.onInterject) this.onInterject(text)
        continue
      }
      const entry = this.pending.get(String(message.id))
      if (!entry) continue
      this.pending.delete(String(message.id))
      clearTimeout(entry.timer)
      if (message.ok) entry.resolve(message.result)
      else entry.reject(new Error(message.error ?? 'helper error'))
    }
  }

  request(op, params = {}, timeoutMs = 180000) {
    if (!this.socket) return Promise.reject(new Error('desktop helper is not running'))
    const id = this.nextId++
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { this.pending.delete(String(id)); reject(new Error(`desktop helper timed out on ${op}`)) }, timeoutMs)
      this.pending.set(String(id), { resolve, reject, timer })
      this.socket.write(JSON.stringify({ id, op, ...params }) + '\n')
    })
  }

  dispose() {
    try { this.socket?.destroy() } catch { }
    try { this.child?.kill() } catch { }
    this.socket = undefined
    this.child = undefined

  }
}

function summarizeAction(action, elements) {
  const element = action.id ? elements.get(action.id) : undefined
  const name = element?.name ? `【${element.name}】` : element?.ct ? `【${element.ct}】` : ''
  if (action.kind === 'click') {
    const where = name || (action.x !== undefined ? `(${action.x},${action.y})` : '')
    return `${action.button === 1 ? '右键' : action.count > 1 ? '双击' : '点击'} ${where}`.trim()
  }
  if (action.kind === 'type') return `输入 ${JSON.stringify(action.text ?? '')}${name ? ' 到 ' + name : ''}`
  if (action.kind === 'key') return `按键 ${action.keys}`
  if (action.kind === 'open') return `新标签页打开 ${action.url}`
  if (action.kind === 'scroll') {
    if (action.pages !== undefined) return `滚动约 ${action.pages} 屏`
    return `滚动 ${action.delta ?? -3} 格`
  }
  if (action.kind === 'drag') {
    const from = action.id ? `#${action.id}` : `(${action.x},${action.y})`
    const to = action.toId ? `#${action.toId}` : `(${action.toX},${action.toY})`
    return `拖拽 ${from} → ${to}`
  }
  if (action.kind === 'wait') return `等待 ${action.ms ?? 500}ms`
  return action.kind
}

function boxesFor(actions, elements) {
  const boxes = []
  actions.forEach((action, index) => {
    const element = action.id ? elements.get(action.id) : undefined
    if (element) boxes.push({ x: element.x, y: element.y, w: element.w, h: element.h, n: index + 1 })
    else if (action.x !== undefined && action.y !== undefined) boxes.push({ x: action.x - 24, y: action.y - 24, w: 48, h: 48, n: index + 1 })
  })
  return boxes
}

function isDangerous(actions, elements) {
  for (const action of actions) {
    const element = action.id ? elements.get(action.id) : undefined
    const name = (element?.name ?? '').toLowerCase()
    if (name && DANGER.some(word => name.includes(word.toLowerCase()))) return element.name
  }
  return undefined
}

/** Top-right note injected into the served page: what this plugin can and cannot drive. */
const CHIP_SCRIPT = `<script>(function(){
  if (document.getElementById('dsh-dc-chip')) return;
  var root = document.createElement('div');
  root.id = 'dsh-dc-chip';
  root.style.cssText = 'position:fixed;left:14px;bottom:56px;z-index:40;font:12px/1.5 system-ui,"Segoe UI",sans-serif;display:flex;flex-direction:column-reverse;align-items:flex-start;';
  var btn = document.createElement('button');
  btn.type = 'button';
  btn.title = '桌面副驾 · 模式';
  btn.textContent = '副驾 · 确认';
  btn.style.cssText = 'cursor:pointer;border:1px solid #2e3442;background:rgba(24,26,32,.9);color:#9aa3b2;border-radius:999px;padding:6px 13px;font:12px/1.2 system-ui,"Segoe UI",sans-serif;white-space:nowrap;';
  var panel = document.createElement('div');
  panel.style.cssText = 'display:none;margin-bottom:6px;width:300px;background:rgba(24,26,32,.98);border:1px solid #2e3442;border-radius:12px;box-shadow:0 10px 30px rgba(0,0,0,.45);padding:10px 12px;color:#e7e9ef;line-height:1.5;';
  panel.innerHTML = [
    '<div style="color:#9aa3b2;margin-bottom:6px">总开关</div>',
    '<div id="dsh-dc-power" style="display:flex;gap:6px;margin-bottom:10px"></div>',
    '<div style="color:#9aa3b2;margin-bottom:6px">执行模式</div>',
    '<div id="dsh-dc-modes" style="display:flex;gap:6px;margin-bottom:8px"></div>',
    '<div style="color:#9aa3b2;font-size:11px;margin-bottom:10px" id="dsh-dc-modehelp"></div>',
    '<div style="color:#9aa3b2;margin-bottom:6px">截图范围</div>',
    '<div id="dsh-dc-capture" style="display:flex;gap:6px;margin-bottom:8px"></div>',
    '<div style="color:#9aa3b2;font-size:11px;margin-bottom:10px" id="dsh-dc-caphelp"></div>',
    '<div style="margin:4px 0"><b style="color:#8fd18f">可读控件</b>　Edge/Chrome 网页、微信、Office、资源管理器</div>',
    '<div style="margin:4px 0"><b style="color:#e0b060">只发截图</b>　ChatGPT 客户端、游戏、画布类工具：按坐标点，可能偏</div>',
    '<div style="margin:4px 0"><b style="color:#9aa3b2">文件任务</b>　整理/重命名/批量操作走 shell，不进桌面循环</div>',
    '<div style="margin-top:8px;padding-top:8px;border-top:1px solid #2e3442;color:#9aa3b2">操控中随时按 Ctrl+Alt+Esc 停止。</div>',
    '<div style="margin-top:8px;display:flex;gap:6px">',
    '<input id="dsh-dc-interject" placeholder="插话给鲸鱼娘…" style="flex:1;min-width:0;background:#1b1f27;border:1px solid #2e3442;border-radius:8px;color:#e7e9ef;padding:6px 8px;font:inherit;font-size:12px;outline:none" />',
    '<button id="dsh-dc-send" type="button" style="cursor:pointer;font:inherit;font-size:12px;border-radius:8px;padding:6px 10px;border:1px solid #3b6fd4;background:#2a58b8;color:#fff">插话</button>',
    '</div>',
    '<div id="dsh-dc-senthint" style="color:#8fd18f;font-size:11px;height:14px;margin-top:4px"></div>'
  ].join('');
  var MODES = [['confirm', '确认'], ['auto', '自动'], ['guide', '只指路']];
  var LABEL = { confirm: '确认', auto: '自动', guide: '只指路' };
  var HELP = {
    confirm: '每步弹卡片：自己执行 / 下一步 / 驳回。',
    auto: '不再弹卡，直接执行；删除、安装、改系统设置会拦下来问。',
    guide: '只画框给指示，不点任何东西。'
  };
  var CAPS = [['window', '窗口'], ['full', '全屏']];
  var CAPHELP = {
    window: '只读最前面那个窗口，省事也省钱。',
    full: '整屏 + 所有窗口，跨窗口任务更准，但更慢也更费。'
  };
  var current = 'confirm';
  var capture = 'full';
  var powered = true;
  var POWERS = [['on', '开启'], ['off', '暂停']];
  var bar = panel.querySelector('#dsh-dc-modes');
  var capBar = panel.querySelector('#dsh-dc-capture');
  var powerBar = panel.querySelector('#dsh-dc-power');
  var help = panel.querySelector('#dsh-dc-modehelp');
  var capHelp = panel.querySelector('#dsh-dc-caphelp');
  function chipText() {
    var open = panel.style.display === 'block';
    if (open) return '收起';
    if (!powered) return '副驾 · 已暂停';
    return '副驾 · ' + (LABEL[current] || '确认') + (capture === 'full' ? ' · 全屏' : '');
  }
  function push() {
    fetch('/desktop-copilot/mode', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ mode: current, capture: capture, enabled: powered }) }).catch(function () { });
  }
  var interjectBox = panel.querySelector('#dsh-dc-interject');
  var interjectHint = panel.querySelector('#dsh-dc-senthint');
  function sendInterject() {
    var text = (interjectBox.value || '').trim();
    if (!text) return;
    interjectBox.value = '';
    fetch('/desktop-copilot/interject', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ text: text }) })
      .then(function (r) { return r.json(); })
      .then(function (d) { interjectHint.textContent = d && d.queued ? '已插话，下一步会带到' : '没发出去'; })
      .catch(function () { interjectHint.textContent = '发送失败'; });
  }
  panel.querySelector('#dsh-dc-send').onclick = sendInterject;
  interjectBox.addEventListener('keydown', function (e) {
    if (e.key === 'Enter') { e.preventDefault(); sendInterject(); }
    e.stopPropagation();
  });
  function paint() {
    powerBar.innerHTML = '';
    POWERS.forEach(function (pair) {
      var b = document.createElement('button');
      b.type = 'button';
      b.textContent = pair[1];
      var on = (pair[0] === 'on') === powered;
      b.style.cssText = 'flex:1;cursor:pointer;font:inherit;border-radius:8px;padding:5px 0;'
        + (on ? 'border:1px solid ' + (powered ? '#3f9e63' : '#b4544b') + ';background:' + (powered ? '#26794a' : '#8a3a34') + ';color:#fff;' : 'border:1px solid #2e3442;background:#1b1f27;color:#9aa3b2;');
      b.onclick = function () { powered = pair[0] === 'on'; paint(); push(); };
      powerBar.appendChild(b);
    });
    bar.innerHTML = '';
    MODES.forEach(function (pair) {
      var b = document.createElement('button');
      b.type = 'button';
      b.textContent = pair[1];
      var on = current === pair[0];
      b.style.cssText = 'flex:1;cursor:pointer;font:inherit;border-radius:8px;padding:5px 0;'
        + (on ? 'border:1px solid #3b6fd4;background:#2a58b8;color:#fff;' : 'border:1px solid #2e3442;background:#1b1f27;color:#9aa3b2;');
      b.onclick = function () { current = pair[0]; paint(); push(); };
      bar.appendChild(b);
    });
    capBar.innerHTML = '';
    CAPS.forEach(function (pair) {
      var b = document.createElement('button');
      b.type = 'button';
      b.textContent = pair[1];
      var on = capture === pair[0];
      b.style.cssText = 'flex:1;cursor:pointer;font:inherit;border-radius:8px;padding:5px 0;'
        + (on ? 'border:1px solid #3b6fb4;background:#245a8a;color:#fff;' : 'border:1px solid #2e3442;background:#1b1f27;color:#9aa3b2;');
      b.onclick = function () { capture = pair[0]; paint(); push(); };
      capBar.appendChild(b);
    });
    help.textContent = HELP[current] || '';
    capHelp.textContent = CAPHELP[capture] || '';
    btn.textContent = chipText();
  }
  paint();
  fetch('/desktop-copilot/mode').then(function (r) { return r.json() }).then(function (d) {
    if (!d) return;
    if (MODES.some(function (p) { return p[0] === d.mode })) current = d.mode;
    if (CAPS.some(function (p) { return p[0] === d.capture })) capture = d.capture;
    if (typeof d.enabled === 'boolean') powered = d.enabled;
    paint();
  }).catch(function () { });
  btn.onclick = function (event) {
    event.stopPropagation();
    var open = panel.style.display === 'block';
    panel.style.display = open ? 'none' : 'block';
    btn.textContent = chipText();
  };
  document.addEventListener('click', function (event) {
    if (panel.style.display === 'block' && !root.contains(event.target)) {
      panel.style.display = 'none';
      btn.textContent = chipText();
    }
  });
  root.appendChild(btn);
  root.appendChild(panel);
  document.body.appendChild(root);
})();</script>`

/** Model-facing policy injected into the system prompt while the plugin is mounted. */
const POLICY_ENABLED = [
  '桌面副驾（可选能力）：',
  '- 只在用户明确要求操作电脑界面时使用 desktop_capture / desktop_step（例如“点一下”“帮我填这个表”“把微信里这条发出去”“打开某个程序”）。',
  '- 纯文件读写、改代码、搜索、问答不要用桌面工具；能用 shell 或文件工具完成的文件任务也不要用桌面工具。',
  '- 每次任务先用 desktop_capture 看清现状；desktop_step 一次至多 3 个动作，优先元素 id，每步之后重新 capture 验证。',
  '- 用户说“别动我电脑 / 暂停 / 停手 / 先别操作”时，立刻 desktop_control stop，并停止调用，直到用户再次明确要求。',
  '- 用户可在页面左下角胶囊切换执行模式、截图范围和总开关。',
  '当前状态：操控已开启。',
].join('\n')

const POLICY_DISABLED = [
  '桌面副驾：用户已把桌面操控关掉。',
  '- 不要调用 desktop_capture / desktop_step；也不要建议“我帮你点点看”。',
  '- 只有用户明确要求重新开启时，才调用 desktop_control enable。',
  '当前状态：操控已关闭。',
].join('\n')

/** Services this plugin needs before it may activate. */
export const inject = ['tools', 'webServer', 'systemPrompt']

export async function apply(ctx, config = {}) {
  let exePath = config.helperPath
  const client = new HelperClient(exePath)
  /** Last captured element table, id -> element. */
  let elements = new Map()
  let steps = 0
  let lastWindow = undefined
  /** Master switch: when false, capture and step refuse to run at all. */
  let enabled = true
  /** Serializes helper calls: one card / one injection at a time. */
  let chain = Promise.resolve()
  /** Notes the user typed into the interject box; delivered with the next tool result. */
  let interjections = []
  client.onInterject = (text) => { interjections.push(text) }
  /** Deploy once, then keep the helper alive so its global hotkeys are always registered. */
  const ensureHelper = async () => {
    if (exePath === undefined) {
      exePath = deployHelper().exe
      client.exePath = exePath
    }
    client.keepAlive = true
    await client.start()
  }
  void ensureHelper().catch((error) => { console.warn('[desktop-copilot] helper warm-up failed:', error?.message ?? String(error)) })
  ctx.effect(() => () => { client.stopping = true; clearTimeout(client.reviveTimer); try { client.child?.kill() } catch { } }, 'desktop-copilot.helper')
  const takeInterjections = () => {
    if (interjections.length === 0) return []
    const notes = interjections.map(text => `【用户插话】${text}`)
    interjections = []
    return ['', ...notes, '（用户在你执行期间说的话，请在本步内考虑）']
  }

  const serial = (fn) => {
    const run = chain.then(fn, fn)
    chain = run.then(() => undefined, () => undefined)
    return run
  }

  /** Refused when the user turned desktop control off; `desktop_control` still works. */
  const assertEnabled = () => {
    if (!enabled) throw new Error('桌面操控当前已关闭（页面左下角胶囊里可以重新开启）。不要重试，先问用户。')
  }

  // Model-facing policy: when this capability applies, and how to stop using it.
  ctx.systemPrompt.section({
    name: 'desktop-copilot:policy',
    order: 210,
    text: () => (enabled ? POLICY_ENABLED : POLICY_DISABLED),
  })

  ctx.effect(() => () => { client.dispose() }, 'desktop-copilot.helper')

  /** Page-selected execution mode: confirm (card), auto (no card), guide (boxes only). */
  let mode = 'confirm'
  /** Page-selected capture scope: window (front window) or full (whole screen, every window). */
  let captureScope = 'full'
  const web = ctx.webServer
  if (web && typeof web.register === 'function') {
    const readJson = (req, done) => {
      if (req.method !== 'POST') { done({}); return }
      let body = ''
      req.on('data', (chunk) => { body += chunk; if (body.length > 4096) req.destroy() })
      req.on('end', () => { try { done(JSON.parse(body) ?? {}) } catch { done({}) } })
    }
    const respond = (res, value) => {
      res.writeHead(200, { 'content-type': 'application/json' })
      res.end(JSON.stringify(value))
    }
    ctx.effect(() => web.register({
      kind: 'exact',
      path: '/desktop-copilot/mode',
      handler: (req, res) => readJson(req, (next) => {
        if (['confirm', 'auto', 'guide'].includes(next.mode)) mode = next.mode
        if (['window', 'full'].includes(next.capture)) captureScope = next.capture
        if (typeof next.enabled === 'boolean') enabled = next.enabled
        respond(res, { mode, capture: captureScope, enabled })
      }),
    }), 'desktop-copilot.mode')

    // The page-side interject box lives here: a browser input keeps the keyboard focus that
    // the native helper cannot hold, and the text reaches the agent with the next tool result.
    ctx.effect(() => web.register({
      kind: 'exact',
      path: '/desktop-copilot/interject',
      handler: (req, res) => readJson(req, (next) => {
        const text = typeof next.text === 'string' ? next.text.trim().slice(0, 500) : ''
        if (text) interjections.push(text)
        respond(res, { queued: Boolean(text), pending: interjections.length })
      }),
    }), 'desktop-copilot.interject')
  }

  // The page-side note. The client-module graph is composed when the Host boots, so a
  // freshly installed client half waits for a restart; one script tag in the served index
  // shows the same note immediately and survives restarts.
  if (web && typeof web.tapIndex === 'function') {
    ctx.effect(() => web.tapIndex(html => html.includes('dsh-dc-chip') ? html : html.replace('</body>', `${CHIP_SCRIPT}</body>`)), 'desktop-copilot.chip')
  }

  const capture = async (scopeOverride, region, wantImage = true) => {
    const full = (scopeOverride === 'window' || scopeOverride === 'full' ? scopeOverride : captureScope) === 'full'
    if (!client.socket) { if (exePath === undefined) { exePath = deployHelper().exe; client.exePath = exePath } await client.start() }
    const zoomRegion = region && Number(region.w) > 0 && Number(region.h) > 0 ? region : undefined
    const uia = await client.request('uia', full ? { scope: 'full' } : {})
    if (!uia.elements || uia.elements.length === 0) {
      throw new Error(`无法读取前台窗口「${uia.title}」的控件树（进程 ${uia.process}）。该窗口可能不支持 UI Automation。`)
    }
    lastWindow = { title: uia.title, process: uia.process, pid: uia.pid, x: uia.x, y: uia.y, w: uia.w, h: uia.h }
    elements = new Map()
    const kept = []
    for (const element of uia.elements) {
      if (kept.length >= (full ? 400 : MAX_ELEMENTS)) break
      elements.set(element.id, element)
      kept.push(element)
    }
    const stamp = `${Date.now()}`
    // The host may be confined to a sandbox that cannot write outside its workspace,
    // so the helper returns the JPEG bytes inline instead of a temp file.
    const shot = await client.request('capture', {
      maxWidth: zoomRegion ? 1600 : (full ? 1600 : 1280), jpeg: true, zoom: Boolean(zoomRegion),
      crop: zoomRegion ? { x: Number(zoomRegion.x) || 0, y: Number(zoomRegion.y) || 0, w: Number(zoomRegion.w), h: Number(zoomRegion.h) } : { x: uia.x, y: uia.y, w: Math.max(1, uia.w), h: Math.max(1, uia.h) },
    })
    let image
    if (wantImage) {
      try {
        const bytes = Buffer.from(shot.base64, 'base64')
        image = await ctx.attachments.saveImage({ data: new Uint8Array(bytes), mediaType: 'image/jpeg', name: `desktop-${stamp}.jpg` })
      } catch (error) {
        // A text-only capture is still useful; never fail the step on the image.
        image = undefined
        console.warn('[desktop-copilot] image capture failed:', error?.message ?? String(error))
      }
    }
    return { uia, image, elements: kept }
  }

  /**
   * Re-read the live tree and rewrite each action onto the current rectangle of the
   * element it named. Matching is by control type, name, and nearest old position, so
   * duplicate labels ("关闭标签页") still resolve to the right one.
   */
  const resolveTargets = async (actions) => {
    // Full-screen mode stays on the snapshot: a fresh dump costs a full UIA walk across
    // every window and its rectangles can belong to a window that has since moved, so
    // clicks go straight to the coordinates the model saw, like a human with a mouse.
    if (captureScope === 'full') {
      const boxes = []
      const placed = actions.map((action, index) => {
        const element = action.id ? elements.get(action.id) : undefined
        if (element) {
          boxes.push({ x: element.x, y: element.y, w: element.w, h: element.h, n: index + 1 })
          return { ...action, x: Math.round(element.x + element.w / 2), y: Math.round(element.y + element.h / 2) }
        }
        if (action.x !== undefined && action.y !== undefined) {
          boxes.push({ x: action.x - 24, y: action.y - 24, w: 48, h: 48, n: index + 1 })
        }
        return action
      })
      return { actions: placed, boxes }
    }
    if (!actions.some(action => action.id)) return { actions, boxes: boxesFor(actions, elements) }
    let fresh = []
    try {
      const dump = await client.request('uia', {}, 30000)
      fresh = Array.isArray(dump.elements) ? dump.elements : []
    } catch { /* keep the captured rectangles */ }
    const pick = (old) => {
      if (!old || fresh.length === 0) return undefined
      const ox = old.x + old.w / 2
      const oy = old.y + old.h / 2
      const distance = (e) => Math.hypot(e.x + e.w / 2 - ox, e.y + e.h / 2 - oy)
      const candidates = fresh.filter(e => e.ct === old.ct && e.name === old.name)
      if (candidates.length === 0) return undefined
      return candidates.sort((a, b) => distance(a) - distance(b))[0]
    }
    const boxes = []
    const rewritten = actions.map((action, index) => {
      const old = action.id ? elements.get(action.id) : undefined
      const hit = pick(old)
      if (!hit) return action
      boxes.push({ x: hit.x, y: hit.y, w: hit.w, h: hit.h, n: index + 1 })
      return { ...action, x: Math.round(hit.x + hit.w / 2), y: Math.round(hit.y + hit.h / 2) }
    })
    return { actions: rewritten, boxes }
  }

  const captureTool = {
    name: 'desktop_capture',
    description: 'Capture the desktop and its UI Automation element list with stable ids. scope=full (default) reads the whole screen and every visible window, which is what cross-window or multi-app tasks need; scope=window reads only the front window and is cheaper. Call this before each step and again after acting, and prefer element ids over pixel coordinates.',
    parameters: CAPTURE_PARAMETERS,
    output: {
      schema: CAPTURE_OUTPUT,
      render: (args, value) => {
        const content = [{ type: 'text', text: value.summary }]
        if (value.image) content.push({ type: 'image', attachment: value.image })
        return content
      },
    },
    async execute(args) {
      return serial(async () => {
        assertEnabled()
        const result = await capture(args?.scope, args?.region, args?.image !== false)
        const full = result.uia.scope === 'full'
        const windowRows = full && Array.isArray(result.uia.windows)
          ? result.uia.windows.map((w, index) => `  ${index + 1}. ${w.title.slice(0, 60)}（${w.process}）@${w.x},${w.y} ${w.w}x${w.h}`)
          : []
        const header = full
          ? [
            `全屏：${result.uia.x},${result.uia.y} ${result.uia.w}x${result.uia.h}`,
            `窗口 ${windowRows.length} 个（前面的在上层）：`,
            ...windowRows,
            `元素：${result.elements.length} 个（按窗口与位置排序，id 只在本次截图内有效）`,
            '',
          ]
          : [
            `窗口：${result.uia.title}（${result.uia.process}，pid ${result.uia.pid}）`,
            `位置：${result.uia.x},${result.uia.y} ${result.uia.w}x${result.uia.h}`,
            `元素：${result.elements.length} 个（按屏幕位置排序，id 只在本次截图内有效）`,
            '',
          ]
        const ocrLines = Array.isArray(result.uia.ocr) ? result.uia.ocr : []
        const ocrRows = ocrLines.map((line, index) => `O${index + 1} 文字 ${JSON.stringify(line.text)} @${Math.round(line.x + line.w / 2)},${Math.round(line.y + line.h / 2)}（框 ${line.x},${line.y} ${line.w}x${line.h}）`)
        const ocrHeader = ocrRows.length > 0
          ? ['', 'OCR：该窗口没有可读控件，以下文字由截图识别（括号内坐标可当点击目标）：', ...ocrRows]
          : []
        const rows = result.elements.map(e => `#${e.id}${full && e.win ? ` [${e.win.slice(0, 24)}]` : ''} [${e.ct}] ${e.name || '（无名称）'} @${e.x},${e.y} ${e.w}x${e.h}${e.password ? ' [密码框]' : ''}${e.value ? ` value=${JSON.stringify(e.value)}` : ''}`)
        return {
          summary: takeInterjections().concat(header).concat(rows).concat(ocrHeader).join('\n'),
          ...(result.image ? { image: result.image } : {}),
        }
      })
    },
  }

  const stepTool = {
    name: 'desktop_step',
    description: 'Propose at most 3 actions for the current screen, show them to the user as numbered highlight boxes plus a 3-way card (self-execute / next / reject), then carry out the choice. Actions must target element ids from the last desktop_capture; use pixel x/y only when no element fits. In full-screen scope clicks reuse the coordinates from that snapshot with a real mouse click instead of re-reading UI Automation, so re-capture after anything moves. To visit a URL use kind "open" with "url": it opens a new tab, so never type an address into the current tab and press Enter. Returns the user choice, the execution results, and the rejection reason.',
    parameters: STEP_PARAMETERS,
    output: {
      schema: STEP_OUTPUT,
      render: (args, value) => [{ type: 'text', text: value.summary }],
    },
    async execute(args) {
      return serial(async () => {
        assertEnabled()
        if (!client.socket) throw new Error('desktop helper is not running; call desktop_capture first')
        const actions = Array.isArray(args.actions) ? args.actions.slice(0, MAX_ACTIONS) : []
        if (actions.length === 0) throw new Error('desktop_step needs at least one action')
        steps += 1
        if (steps > MAX_STEPS) throw new Error(`这个任务已经走了 ${MAX_STEPS} 步，停手。请让用户重新描述目标。`)
        const dangerous = isDangerous(actions, elements)
        // The page's mode selector wins unless this call overrides it; a dangerous
        // action always drops back to a card.
        const selected = args.mode === 'auto' ? 'auto' : (args.mode === 'manual' ? 'confirm' : mode)
        const guide = selected === 'guide'
        const stepMode = dangerous || selected !== 'auto' ? 'manual' : 'auto'
        const lines = actions.map((action, index) => `${index + 1}. ${summarizeAction(action, elements)}`)
        if (args.reason) lines.unshift(`为什么：${args.reason}`)
        // The window can scroll between the capture and the card, so re-read the tree and
        // move every id onto its current rectangle before showing anything.
        const resolved = await resolveTargets(actions)
        const result = await client.request('step', {
          title: `${lastWindow?.title ?? ''} — ${args.title}`,
          lines,
          boxes: resolved.boxes,
          actions: resolved.actions,
          mode: stepMode,
          guide,
          auto: stepMode === 'auto',
        }, 30 * 60 * 1000).catch(async (error) => {
          await client.request('hide', {}, 5000).catch(() => { });
          throw error
        })
        const summary = [
          ...takeInterjections(),
          `choice=${result.choice}${result.reason ? ` reason=${result.reason}` : ''}`,
          ...(result.results ? result.results.map(r => `  ${r.kind}: ${r.ok ? 'ok' : 'failed'}${r.note ? ` (${r.note})` : ''}`) : []),
        ].join('\n')
        return {
          summary,
          choice: result.choice,
          ...(result.reason ? { reason: result.reason } : {}),
          ...(result.results ? { results: result.results } : {}),
          ...(dangerous ? { forcedManual: dangerous } : {}),
        }
      })
    },
  }

  const controlTool = {
    name: 'desktop_control',
    description: 'Read or change the desktop helper state: status (master switch, mode, capture scope, pending card), stop (release control and hide overlays), auto_on / auto_off, enable / disable (the master switch), focus (bring a pid to the front).',
    parameters: CONTROL_PARAMETERS,
    output: {
      schema: CONTROL_OUTPUT,
      render: (args, value) => [{ type: 'text', text: value.summary }],
    },
    async execute(args) {
      return serial(async () => {
        if (args.action === 'enable' || args.action === 'disable') {
          enabled = args.action === 'enable'
          if (!enabled) {
            try {
              if (client.socket) {
                await client.request('hide', {})
                await client.request('setauto', { value: false })
              }
            } catch { /* the switch still holds even if the helper is gone */ }
          }
          steps = 0
          return { summary: enabled ? '桌面操控已开启。' : '桌面操控已关闭：截图和步骤都会被拒绝，直到重新开启。' }
        }
        if (args.action === 'status') {
          const state = `操控=${enabled ? '开' : '关'} · 页面模式=${mode} · 截图=${captureScope === 'full' ? '全屏' : '窗口'}`
          if (!client.socket) return { summary: `helper 未启动（还没有调用过 desktop_capture）· ${state}` }
          const status = await client.request('status', {})
          return { summary: `helper 运行中 · 自动=${status.auto ? '开' : '关'} · 卡片${status.busy ? '等待中' : '空闲'} · ${state}` }
        }
        if (args.action === 'stop') {
          if (!client.socket) return { summary: 'helper 未启动，无需停止' }
          await client.request('hide', {})
          await client.request('setauto', { value: false })
          steps = 0
          return { summary: '已停止：覆盖层已隐藏，自动模式已关闭。' }
        }
        if (args.action === 'focus') {
          if (!client.socket) { if (exePath === undefined) { exePath = deployHelper().exe; client.exePath = exePath } await client.start() }
          const target = args.pid ? await client.request('focus', { pid: args.pid }) : { foreground: undefined }
          return { summary: target.foreground === false ? '未能把该窗口切到前台' : '已切到前台' }
        }
        const value = args.action === 'auto_on'
        mode = value ? 'auto' : 'confirm'
        if (!client.socket) { if (exePath === undefined) { exePath = deployHelper().exe; client.exePath = exePath } await client.start() }
        await client.request('setauto', { value })
        steps = 0
        return { summary: value ? '自动模式已开启：后续步骤不再弹卡（危险动作仍会拦截）。' : '自动模式已关闭。' }
      })
    },
  }

  ctx.tools.register(captureTool)
  ctx.tools.register(stepTool)
  ctx.tools.register(controlTool)
}
