// perf-menu.mjs — 跑本工程的性能体检菜单。
// 为什么要这个壳:PowerShell 传中文 argv 给 node 会被控制台代码页吃掉,JSON.parse 直接炸。
// 菜单名写在这个 **UTF-8 文件里**,命令行只传 ASCII 的短名,编码问题就绕过去了。
// 用法: node Tools/perf-menu.mjs <short> [timeoutSec]
//   static / tex / raise / terrace / hud / sanity / smoke-live ...
import { call, discoverPort } from './bridge-lib.mjs';

const MENUS = {
  static: 'VRLibrary/性能·静态体检(只报告)',
  tex: 'VRLibrary/性能·贴图账(只报告)',
  terrace: 'VRLibrary/性能·露台观影子树账(只报告)',
  sweep: 'VRLibrary/性能·② 定点实测(进Play自动跑)',
  'batch-mark': 'VRLibrary/性能·① 静态合批补标(改场景并自动保存)',
  'batch-verify': 'VRLibrary/性能·① 复核(只报告)',
  'pixel-report': 'VRLibrary/性能·③ 像素与阴影档位·现状(只报告)',
  'pixel-apply': 'VRLibrary/性能·③ 应用像素档位(renderScale1.0 MSAA2 阴影2048 25m 级联1)',
  'pixel-revert': 'VRLibrary/性能·③ 回退像素档位(renderScale1.25 MSAA4 阴影4096 50m 级联4)',
  'ffr-on': 'VRLibrary/性能·④ 开固定视场渲染 FFR(改场景:High)',
  'ffr-off': 'VRLibrary/性能·④ 关固定视场渲染 FFR(改场景:None)',
  'ffr-verify': 'VRLibrary/性能·④ 复核(只报告)',
  'bake-pre': 'VRLibrary/性能·⑥ 烘焙前置体检(只报告)',
  'bake-mark': 'VRLibrary/性能·⑥ 打 ContributeGI 标记(改场景)',
  'bake-config': 'VRLibrary/性能·⑥ 配置烘焙参数(轻量档)',
  'bake-go': 'VRLibrary/性能·⑥ 开始烘焙(异步)',
  'bake-result': 'VRLibrary/性能·⑥ 烘焙结果(只报告)',
  'bake-revert': 'VRLibrary/性能·⑥ 回退(清光照数据 + 灯回 Realtime + 去 GI 标记)',
  'bake-uv1': 'VRLibrary/性能·⑥ 给模型生成 UV1(改导入设置)',
  'bake-probes': 'VRLibrary/性能·⑥ 布光探针(改场景)',
  'bake-lights': 'VRLibrary/性能·⑥ 灯全部改 Baked(改场景)',
};

const [short, timeoutSec = '900'] = process.argv.slice(2);
const menuPath = MENUS[short];
if (!menuPath) {
  console.error('未知短名:' + short + '\n可用:' + Object.keys(MENUS).join(' / '));
  process.exit(2);
}
const hit = await discoverPort(process.env.UNITY_PROJECT || 'VRlibrary');
if (!hit) { console.error('没找到活桥 —— 编辑器要开着且不能最小化'); process.exit(5); }
const res = await call(hit.port, 'runMenuItem', { menuPath }, Number(timeoutSec) * 1000);
if (!res) { console.error('端口 ' + hit.port + ' 无响应'); process.exit(3); }
if (res.data) console.log(JSON.stringify(res.data));
console.log('success=' + res.success + ' ' + (res.message || ''));
process.exit(res.success ? 0 : 1);