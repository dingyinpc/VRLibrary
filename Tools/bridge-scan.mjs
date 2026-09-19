// bridge-scan.mjs — 列出本机所有 Unity 编辑器桥的端口归属(排查"我到底连的是哪个工程")
// 用法: node Tools/bridge-scan.mjs
import { Socket } from 'node:net';
import { scanBrowsers, PORT_RANGE } from './bridge-lib.mjs';

function tcpOpens(port, timeoutMs = 400) {
  return new Promise((resolve) => {
    const s = new Socket();
    const t = setTimeout(() => { s.destroy(); resolve(false); }, timeoutMs);
    s.once('connect', () => { clearTimeout(t); s.destroy(); resolve(true); });
    s.once('error', () => { clearTimeout(t); resolve(false); });
    s.connect(port, '127.0.0.1');
  });
}

const live = await scanBrowsers();
const byPort = new Map(live.map(b => [b.port, b]));
const openFlags = await Promise.all(PORT_RANGE.map(async p => [p, await tcpOpens(p)]));

let any = false;
for (const [port, open] of openFlags) {
  if (!open) continue;
  any = true;
  const b = byPort.get(port);
  console.log(b
    ? `  ${port}  ${String(b.projectName || '?').padEnd(26)} Unity ${String(b.unityVersion || '?').padEnd(16)} 场景: ${b.activeScene || '?'}`
      + (String(b.sceneDirty) === 'true' ? '  [有未保存改动]' : '')
    : `  ${port}  ⚠ TCP 能连但 ping 无响应 —— 僵尸监听(编辑器最小化/正在编译/域重载后泄漏)`);
}
if (!any) console.log('  6400~6410 没有任何桥在监听 —— 编辑器没开,或桥没启动(菜单 Unity Copilot → Restart Bridge Server)');
