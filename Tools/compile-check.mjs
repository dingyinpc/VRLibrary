// compile-check.mjs — 触发重编译并回报真实结果
// 为什么不用 Editor.log:它被多个编辑器实例共享(别的工程报错会混进来),而且是缓冲写盘,新错误经常不落盘。
// 真正的判据是 Library/Bee/tundra.log.json —— 每次编译追加一条,含 csc 的 stdout。
// 编译失败时 Library/ScriptAssemblies/*.dll 不会更新,所以 dll 时间戳只能证明"成功",不能证明"没跑"。
// 用法: node Tools/compile-check.mjs            (只等下一次编译结果)
//       node Tools/compile-check.mjs --refresh   (先让编辑器 AssetDatabase.Refresh 再等)
import { statSync, existsSync, openSync, readSync, closeSync } from 'node:fs';
import { call, discoverPort } from './bridge-lib.mjs';

const LOG = 'Library/Bee/tundra.log.json';
const doRefresh = process.argv.includes('--refresh');

function errors(s) {
  return [...new Set((s.match(/error CS\d+[^"\\]{0,200}/g) || []))];
}

if (!existsSync(LOG)) { console.error('没有 ' + LOG + '（还没编译过）'); process.exit(2); }
const st0 = statSync(LOG);
const before = st0.size, beforeM = st0.mtimeMs;

let port = Number(process.env.UNITY_BRIDGE_PORT || 0);
if (!port) { const hit = await discoverPort(process.env.UNITY_PROJECT || 'VRlibrary'); port = hit?.port ?? 0; }

if (doRefresh && port) await call(port, 'refreshAssets', {}, 180000);

let grew = false;
for (let i = 0; i < 40; i++) {
  await new Promise(r => setTimeout(r, 3000));
  const s = statSync(LOG);
  // 该文件可能整体重写(新内容更短),所以 mtime 变化也算
  if (s.size !== before || s.mtimeMs > beforeM) { grew = true; break; }
}
if (!grew) { console.log('没等到新的编译记录 —— 编辑器认为脚本没变化,或没在前台。'); process.exit(3); }

// 只看本次新增的字节:同一条错误连续两次编译都出现时,拿"新旧集合差"会漏报
const size1 = statSync(LOG).size;
const from = size1 > before ? before : 0;
const fd = openSync(LOG, 'r');
const buf = Buffer.alloc(size1 - from);
readSync(fd, buf, 0, buf.length, from);
closeSync(fd);
const fresh = errors(buf.toString('utf8'));
const dllAge = Math.round((Date.now() - statSync('Library/ScriptAssemblies/Assembly-CSharp-Editor.dll').mtimeMs) / 1000);
console.log(`编译完成。Assembly-CSharp-Editor.dll 上次更新于 ${Math.round(dllAge / 60)} 分钟前`);
if (fresh.length) { console.log('本次编译错误:'); fresh.forEach(e => console.log('  ' + e)); process.exit(1); }
console.log('本次编译无错误');
