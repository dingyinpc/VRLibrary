// shot.mjs — 截取编辑器 Scene 视图并落盘(本工程没有相机控制工具,验收截图靠这个)
// 用法: node Tools/shot.mjs [文件名]        默认写到 Captures/shot-<时间戳>.png
import { discoverPort, call } from './bridge-lib.mjs';
import fs from 'node:fs';

// 域重载会在旧端口留一个"TCP 能连、ping 不答"的僵尸监听,扫描因此常报找不到桥;
// 设 UNITY_BRIDGE_PORT 可跳过扫描直连(与 unity-bridge.mjs 同一套逃生口)。
const envPort = Number(process.env.UNITY_BRIDGE_PORT || 0);
const hit = envPort ? { port: envPort } : await discoverPort(process.env.UNITY_PROJECT || 'VRlibrary');
if (!hit) { console.error('没找到本工程编辑器桥(编辑器被最小化?);先 node Tools/bridge-scan.mjs 排查'); process.exit(5); }

const name = process.argv[2] || new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
const out = `Captures/${name}.png`;
const r = await call(hit.port, 'captureScreenshot', {}, 120000);
if (!r || !r.success || !r.data || !r.data.imageBase64) {
  console.error('截图失败:', r ? (r.message || JSON.stringify(r).slice(0, 200)) : '无响应'); process.exit(3);
}
fs.mkdirSync('Captures', { recursive: true });
fs.writeFileSync(out, Buffer.from(r.data.imageBase64, 'base64'));
console.log(`${out}  (${r.data.width}x${r.data.height}, 端口 ${hit.port})`);
process.exit(0);
