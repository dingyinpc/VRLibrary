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
  'stereo-report': 'VRLibrary/性能·⑦ 立体与ASW·现状(只报告)',
  'stereo-apply': 'VRLibrary/性能·⑦ 应用真机三件套(Multiview + 72Hz + 晚锁存)',
  'asw-on': 'VRLibrary/性能·⑦ 开 ASW(前提:已 Multiview)',
  'asw-off': 'VRLibrary/性能·⑦ 关 ASW',
  'stereo-revert': 'VRLibrary/性能·⑦ 回退(回 MultiPass + Default + 关 ASW)',
  apk: 'VRLibrary/打包PICO真机/② 一键打包APK',
  'apk-install': 'VRLibrary/打包PICO真机/③ 安装APK并启动到真机',
  'occ-report': 'VRLibrary/性能·⑧ 遮挡剔除·现状(只报告)',
  'occ-bake': 'VRLibrary/性能·⑧ 标记并烘焙遮挡剔除(改场景,可能要几分钟)',
  'occ-revert': 'VRLibrary/性能·⑧ 回退(清遮挡数据 + 去标记)',
  'bookclick': 'VRLibrary/性能·⑨ 书本改成点击阅读(去抓取 + 去刚体,改场景)',
  'bookclick-verify': 'VRLibrary/性能·⑨ 复核(只报告)',
  'turn-report': 'VRLibrary/体验·⑩ 现状(只报告)',
  'turn-apply': 'VRLibrary/体验·⑩ 转头改成按住持续转 + 力度调速(改场景)',
  'vignette-half': 'VRLibrary/体验·⑩ 防眩晕环强度减半(改场景)',
  'turn-revert': 'VRLibrary/体验·⑩ 回退(恢复 Snap 转头 + 环还原)',
};

const [short, timeoutSec = '900'] = process.argv.slice(2);
const menuPath = MENUS[short];
if (!menuPath) {
  console.error('未知短名:' + short + '\n可用:' + Object.keys(MENUS).join(' / '));
  process.exit(2);
}
const hit = await discoverPort(process.env.UNITY_PROJECT || 'VRlibrary');
if (!hit) { console.error('没找到活桥 —— 编辑器要开着且不能最小化'); process.exit(5); }

// 端口会随域重载漂(本机实测同一天在 6401/6402 之间来回),而**同时开着好几个 Unity 工程**:
// 扫描时 6402=VRlibrary、6403=MCP测试 是并存的。所以写场景之前必须**当场认一遍身份**:
// 只认 projectPath 落在 VRlibrary 且活动场景是 SmartLibrary 的那一个,别的宁可拒绝执行。
const who = await call(hit.port, 'ping', {}, 15000);
const d = who && who.success ? who.data : null;
if (!d) { console.error(`端口 ${hit.port} 无响应(僵尸监听?先 Restart Bridge Server)`); process.exit(3); }
const okProject = String(d.projectPath || '').replace(/\\/g, '/').toLowerCase().endsWith('/vrlibrary');
const okScene = String(d.activeScene || '').includes('SmartLibrary');
console.log(`[端口 ${d.bridgePort || hit.port} · ${d.projectPath} · ${d.activeScene} · dirty=${d.sceneDirty}]`);
if (!okProject || !okScene) {
  console.error(`拒绝执行:认到的不是 VRlibrary/SmartLibrary(工程=${d.projectPath} 场景=${d.activeScene})`);
  process.exit(6);
}

const res = await call(hit.port, 'runMenuItem', { menuPath }, Number(timeoutSec) * 1000);
if (!res) { console.error('端口 ' + hit.port + ' 无响应'); process.exit(3); }
if (res.data) console.log(JSON.stringify(res.data));
console.log('success=' + res.success + ' ' + (res.message || ''));
process.exit(res.success ? 0 : 1);