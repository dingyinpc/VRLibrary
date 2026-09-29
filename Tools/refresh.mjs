// refresh.mjs — 让编辑器 AssetDatabase.Refresh 一次(改完脚本要它才会重编译)
import { call, discoverPort } from './bridge-lib.mjs';
const hit = await discoverPort(process.env.UNITY_PROJECT || 'VRlibrary');
if (!hit) { console.error('没找到活桥'); process.exit(5); }
const r = await call(hit.port, 'refreshAssets', {}, 180000);
console.log('端口 ' + hit.port + ' refresh → success=' + (r && r.success) + ' ' + (r && r.message));
process.exit(r && r.success ? 0 : 1);