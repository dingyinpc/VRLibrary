// compile-check.mjs — 触发重编译并回报真实结果
// 为什么不用 Editor.log:它被多个编辑器实例共享(别的工程报错会混进来),而且是缓冲写盘,新错误经常不落盘。
// 真正的判据有两条,必须一起看:
//   ① Library/Bee/tundra.log.json —— 每次 csc 调用追加一条,含 stdout(错误在这里);
//   ② Library/ScriptAssemblies/*.dll —— 编译**成功**才会被重写。
// 只等"日志变长"会假绿:Unity 还会往同一个日志里写资源导入、着色器编译等记录,
// 那些记录一变长就被误当成"本次编译跑完了"。2026-09-20 实测踩过:报"无错误",
// 而 dll 已经 5 小时没动、日志里躺着 2 个 error CS。所以判据改成:
//   有新 dll            → 成功
//   日志尾部有 error CS  → 失败,打印错误
//   两者都没有           → 明确说"没等到结果"(退出码 3),**绝不替编辑器说"无错误"**
// 用法: node Tools/compile-check.mjs            (只等下一次编译结果)
//       node Tools/compile-check.mjs --refresh   (先让编辑器 AssetDatabase.Refresh 再等)
import { statSync, existsSync, openSync, readSync, closeSync } from 'node:fs';
import { call, discoverPort } from './bridge-lib.mjs';

const LOG = 'Library/Bee/tundra.log.json';
const DLLS = ['Library/ScriptAssemblies/Assembly-CSharp.dll',
              'Library/ScriptAssemblies/Assembly-CSharp-Editor.dll'];
const TAIL = 400 * 1024;            // 只看日志尾部:整份读一遍既慢又会把几小时前的旧错误算进来
const WAIT_MS = 240_000;
const doRefresh = process.argv.includes('--refresh');

function errors(s) {
  return [...new Set((s.match(/error CS\d+:[^"\\\n]{0,180}/g) || []))];
}
function readTail(from) {
  const size = statSync(LOG).size;
  const start = Math.max(0, size - TAIL, from);
  if (start >= size) return '';            // 日志被整体重写短了(Unity 会重排这个文件)
  const fd = openSync(LOG, 'r');
  const buf = Buffer.alloc(size - start);
  readSync(fd, buf, 0, buf.length, start);
  closeSync(fd);
  return buf.toString('utf8');
}

if (!existsSync(LOG)) { console.error('没有 ' + LOG + '（还没编译过）'); process.exit(2); }

const t0 = Date.now();
const dllMtime = () => Math.max(...DLLS.filter(existsSync).map(f => statSync(f).mtimeMs));
const dllBefore = dllMtime();
const sizeBefore = statSync(LOG).size;

let port = Number(process.env.UNITY_BRIDGE_PORT || 0);
if (!port) { const hit = await discoverPort(process.env.UNITY_PROJECT || 'VRlibrary'); port = hit?.port ?? 0; }

if (doRefresh && port) await call(port, 'refreshAssets', {}, 180000);
else if (doRefresh) console.error('!! --refresh 但连不上编辑器桥,只等已有结果');

const deadline = Date.now() + WAIT_MS;
let verdict = null;
while (Date.now() < deadline && !verdict) {
  await new Promise(r => setTimeout(r, 3000));
  const m = dllMtime();
  if (m > dllBefore && m > t0 - 5000) { verdict = 'ok'; break; }          // 成功:有新 dll
  const fresh = errors(readTail(sizeBefore));                              // 只看本次新增的字节
  if (fresh.length) { verdict = { errs: fresh }; break; }
}

const age = Math.round((Date.now() - dllMtime()) / 60000);
if (!verdict) {
  // Unity 按内容哈希决定要不要重编译:只 touch 不改内容就不会再跑一次,
  // 于是上一次失败的 error CS 会一直躺在日志尾部。所以这里除了"本次新增字节",
  // 还要把整个尾部再扫一遍,否则会出现"工具说没错误、编辑器里其实红着一片"的假绿。
  const stale = errors(readTail(0));
  if (stale.length) {
    console.log(`没有新的编译动作,但日志尾部仍有 ${stale.length} 条 error CS(大概是上次失败后内容没再变,Unity 不肯重编译)。`);
    console.log(`两份 dll 最近一次更新在 ${age} 分钟前。改一下文件内容再跑,或直接在编辑器里 Ctrl+R。`);
    stale.slice(0, 20).forEach(e => console.log('  ' + e));
    process.exit(1);
  }
  console.log(`没等到编译结果(${WAIT_MS / 1000}s 内既没有新 dll,日志尾部也没有 error CS)。`);
  console.log(`两份 dll 最近一次更新在 ${age} 分钟前 —— 可能是编辑器没在前台(自动重编译被推迟)、`);
  console.log('或 Unity 认为脚本没变化。先 powershell -File Tools/restore-window.ps1 再跑一次。');
  process.exit(3);
}
if (verdict === 'ok') {
  console.log(`编译成功。两份 dll 最近一次更新于 ${age} 分钟前`);
  // 成功之后仍要扫一眼尾部:Unity 有时把 warning 与 error 混在同一条记录里
  const late = errors(readTail(sizeBefore));
  if (late.length) { console.log('但日志尾部仍有 error CS(可能是上一次留下的,核对时间):'); late.forEach(e => console.log('  ' + e)); process.exit(1); }
  console.log('本次编译无错误');
  process.exit(0);
}
console.log(`编译失败。两份 dll 最近一次更新在 ${age} 分钟前(失败不会重写 dll)`);
console.log('本次编译错误:');
verdict.errs.forEach(e => console.log('  ' + e));
process.exit(1);
