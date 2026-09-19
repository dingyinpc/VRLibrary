// bridge-lib.mjs — 编辑器桥的最小公共客户端(Windows/macOS 上的 Unity 都走 ws://127.0.0.1:<port>)
// 协议对齐 Assets/Editor/UnityBridge/UnityBridgeServer.cs + UnityCommandHandler.Handle()

export const DEFAULT_TIMEOUT = 150;      // 扫描期端口超时;桥被后台节流时需要更长
export const PORT_RANGE = [6400, 6401, 6402, 6403, 6404, 6405, 6406, 6407, 6408, 6409, 6410];

/** 发一条命令并等回包;失败/超时返回 null,不抛异常。 */
export function call(port, action, params = {}, timeoutMs = 10000) {
  return new Promise((resolve) => {
    let settled = false;
    let ws;
    const timer = setTimeout(() => done(null), timeoutMs);
    const done = (v) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      try { ws && ws.close(); } catch { /* 已断开 */ }
      resolve(v);
    };
    try { ws = new WebSocket(`ws://127.0.0.1:${port}`); } catch { return done(null); }
    ws.onopen = () => ws.send(JSON.stringify({ id: 'lib', action, params }));
    ws.onmessage = (e) => {
      try { done(JSON.parse(e.data.toString())); } catch { done(null); }
    };
    ws.onerror = () => done(null);
  });
}

/** 问一个端口"你是哪个工程";僵尸监听(握手通但主线程不回复)会返回 null。 */
export async function probePort(port, timeoutMs = DEFAULT_TIMEOUT) {
  const r = await call(port, 'ping', {}, timeoutMs);
  if (!r || !r.success) return null;
  return { port, ...(r.data || {}) };
}

/** 扫描候选端口,返回所有活着的桥。 */
export async function scanBrowsers(range = PORT_RANGE, timeoutMs = DEFAULT_TIMEOUT) {
  const rs = await Promise.allSettled(range.map((p) => probePort(p, timeoutMs)));
  return rs.filter(x => x.status === 'fulfilled' && x.value).map(x => x.value);
}

/** 按工程名找到它的桥端口。 */
export async function discoverPort(projectName, range = PORT_RANGE, timeoutMs = DEFAULT_TIMEOUT) {
  const live = await scanBrowsers(range, timeoutMs);
  return live.find(b => b.projectName === projectName) || null;
}
