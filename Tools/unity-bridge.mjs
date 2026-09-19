// unity-bridge.mjs — 驱动"本工程"编辑器桥的命令行客户端(不依赖 MCP 是否已重载)
// 用法: node Tools/unity-bridge.mjs <action> ['{"k":v}'] [timeoutSec]
// 例:   node Tools/unity-bridge.mjs ping
//       node Tools/unity-bridge.mjs getSceneHierarchy '{}' 60
//       node Tools/unity-bridge.mjs runMenuItem '{"menuPath":"VRLibrary/一键搭建智能图书馆场景"}'
// 端口: 默认自动扫描 6400~6410 并按工程名认领(桥端口会随域重载漂移,不要硬编码);
//       设 UNITY_BRIDGE_PORT 可跳过扫描直连,设 UNITY_PROJECT 可改认领目标。
import { call, discoverPort } from './bridge-lib.mjs';

const THIS_PROJECT = process.env.UNITY_PROJECT || 'VRlibrary';
const [action, paramsJson = '{}', timeoutSec = '120'] = process.argv.slice(2);
if (!action) { console.error('usage: unity-bridge.mjs <action> [paramsJson] [timeoutSec]'); process.exit(2); }

let port = Number(process.env.UNITY_BRIDGE_PORT || 0);
if (!port) {
  const hit = await discoverPort(THIS_PROJECT);
  if (!hit) {
    console.error(`没找到工程「${THIS_PROJECT}」的活桥(6400~6410)。`
      + '\n排查:① 编辑器不能是最小化/后台(主线程不泵消息);'
      + '\n     ② 菜单 Unity Copilot → Restart Bridge Server;'
      + '\n     ③ node Tools/bridge-scan.mjs 看端口归属。');
    process.exit(5);
  }
  port = hit.port;
  if (hit.activeScene && !String(hit.activeScene).includes('SmartLibrary') && action !== 'ping') {
    console.error(`⚠ 目标工程已认领(端口 ${port}),但当前打开的场景是 ${hit.activeScene},不是 SmartLibrary —— 先确认再写。`);
    process.exit(6);
  }
}

const res = await call(port, action, JSON.parse(paramsJson), Number(timeoutSec) * 1000);
if (!res) { console.error(`端口 ${port} 无响应(工程「${THIS_PROJECT}」的编辑器可能被节流或正在编译)`); process.exit(3); }
console.log(JSON.stringify(res));
process.exit(res.success ? 0 : 1);
