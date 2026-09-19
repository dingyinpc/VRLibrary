#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成一块"密度均匀"的无缝科技地砖,替换 Blender 那块面板设计图。

产物直接覆盖 Assets/Textures/TechFloor/ 下同名四张图,通道排布保持与原图一致,
所以 Unity 里的材质和导入设置都不用改:
  T_TechFloor_BaseColor.png  sRGB 底色
  T_TechFloor_URPMask.png    线性  R=金属度 G=AO B=0 A=平滑度
  T_TechFloor_Normal.png     线性  Unity 约定(绿朝下)
  T_TechFloor_Emissive.png   sRGB  自发光,默认在 Unity 里是关的

设计原则(针对"东一块西一块"这个毛病):
  整块砖就是**一个平面盘**,细节均匀散布,不留大片素面、不堆棋盘格、不留黑色空白区;
  砖缝凹槽**中心压在贴图边界上**,相邻砖拼起来正好合成一条完整凹槽 → 天然无缝。
用法: python Tools/techfloor_tile_bake.py [输出目录]
"""
import os
import sys

import numpy as np
from PIL import Image

SIZE = 1024
OUT = sys.argv[1] if len(sys.argv) > 1 else "Assets/Textures/TechFloor"

rng = np.random.default_rng(20260920)

y, x = np.mgrid[0:SIZE, 0:SIZE].astype(np.float32)
u = x / SIZE
v = y / SIZE

# ── 高度场:先定几何,再由它推法线和 AO ────────────────────────────────
h = np.zeros((SIZE, SIZE), np.float32)

# 1) 砖缝凹槽:中心压在边界上,半条在头、半条在尾,拼起来是一条完整缝
GROOVE_HALF = 16.0            # 像素;1.96m 砖上约 3cm 半宽 → 6cm 缝
edge = np.minimum(np.minimum(x, SIZE - x), np.minimum(y, SIZE - y)).astype(np.float32)
groove = np.clip(1.0 - edge / GROOVE_HALF, 0.0, 1.0) ** 1.5
h -= groove * 1.0

# 2) 板内一圈浅台阶(离边界远一点,让面板有"装上去"的层次,但保持整圈均匀)
ring = np.clip(1.0 - np.abs(edge - 96.0) / 10.0, 0.0, 1.0)
h -= ring * 0.18

# 3) 四角沉头螺钉:均匀分布在四个角,不在任何一侧堆料
screw = np.zeros_like(h)
for sx in (168, SIZE - 168):
    for sy in (168, SIZE - 168):
        d = np.hypot(x - sx, y - sy)
        screw += np.clip(1.0 - d / 26.0, 0.0, 1.0) ** 2
        screw -= np.clip(1.0 - np.abs(d - 34.0) / 6.0, 0.0, 1.0) * 0.5   # 沉头倒角圈
h += np.clip(screw, -0.4, 1.0) * 0.55

# 4) 中央一块浅压印方框(每块砖都有,位置一致 → 不产生随机团块)
cx, cy = SIZE / 2.0, SIZE / 2.0
box = np.clip(1.0 - np.abs(np.maximum(np.abs(x - cx), np.abs(y - cy)) - 150.0) / 7.0, 0.0, 1.0)
h -= box * 0.12

# 5) 均匀磨纹:各向同性的细噪声铺满整块,保证"每平方厘米细节量"一致
grain = rng.standard_normal((SIZE, SIZE)).astype(np.float32)
# 轻度模糊一下,免得单像素噪声在 mipmap 下直接糊掉
k = np.array([[1, 2, 1], [2, 4, 2], [1, 2, 1]], np.float32) / 16.0
from numpy.lib.stride_tricks import as_strided
pad = np.pad(grain, 1, mode='wrap')
sh = (SIZE + 2 - 2, SIZE + 2 - 2, 3, 3)
st = pad.strides * 2
grain_s = (as_strided(pad, sh, st) * k).sum(axis=(2, 3))
h += grain_s * 0.05

# 6) 几条均匀分布的浅划痕(数量固定、位置对称,避免随机团块)
for i in range(6):
    ang = i * np.pi / 3.0 + 0.26
    px = (x - cx) * np.cos(ang) + (y - cy) * np.sin(ang)
    py = -(x - cx) * np.sin(ang) + (y - cy) * np.cos(ang)
    line = np.clip(1.0 - np.abs(py - 46.0 * np.sin(px / 130.0)) / 1.6, 0.0, 1.0)
    line *= np.clip(1.0 - np.abs(px) / 330.0, 0.0, 1.0)
    h -= line * 0.06

# ── 法线(Unity 约定:绿朝下) ─────────────────────────────────────────
STRENGTH = 2.4
dx = (np.roll(h, -1, axis=1) - np.roll(h, 1, axis=1)) * STRENGTH
dy = (np.roll(h, -1, axis=0) - np.roll(h, 1, axis=0)) * STRENGTH
nz = np.ones_like(h)
n = np.stack([dx, dy, nz], axis=-1)          # 绿朝下:dy 不取反
n /= np.linalg.norm(n, axis=-1, keepdims=True)
normal = ((n * 0.5 + 0.5) * 255.0).clip(0, 255).astype(np.uint8)
normal[..., 2] = 255                          # Unity 只读 RG,补 Z=1 更稳

# ── AO:凹槽与台阶处压暗,用高度场反推 ─────────────────────────────────
cav = np.clip(-h, 0.0, 1.2)
pad = np.pad(cav, 5, mode='wrap')
k5 = np.ones((11, 11), np.float32) / 121.0
sh = (SIZE, SIZE, 11, 11)
st = pad.strides * 2
cav_blur = (as_strided(pad, sh, st) * k5).sum(axis=(2, 3))
ao = np.clip(1.0 - cav_blur * 1.35 - groove * 0.35, 0.18, 1.0)

# ── BaseColor:一块均匀金属盘 + 缝里更暗 ───────────────────────────────
base = np.array([0.46, 0.475, 0.505], np.float32)          # 与原图均值(~0.49)同档
plate = base[None, None, :] * (1.0 + grain_s * 0.30)[..., None]
shade = (0.30 + 0.70 * np.clip(h + 1.0, 0.0, 1.0))[..., None]   # 高处亮、凹槽暗
base_rgb = np.clip(plate * shade * ao[..., None] ** 0.35, 0.0, 1.0)
# 螺钉给一点不锈钢色
screw_mask = np.clip(screw, 0.0, 1.0)
base_rgb = base_rgb * (1 - screw_mask[..., None]) + np.array([0.62, 0.63, 0.65], np.float32) * screw_mask[..., None]

# 原图存的就是显示值(sRGB 均值约 0.49),这里不再做一次线性→sRGB 编码,
# 否则同一张图会被压亮一倍。
disp = base_rgb.clip(0, 1)
base_img = (disp * 255 + 0.5).astype(np.uint8)

# ── Mask: R=金属 G=AO B=0 A=平滑 ─────────────────────────────────────
metal = np.clip(0.70 - groove * 0.34 + screw_mask * 0.20, 0.0, 1.0)
smooth = np.clip(0.80 - groove * 0.45 + screw_mask * 0.10 - cav_blur * 0.25, 0.0, 1.0)
mask = np.zeros((SIZE, SIZE, 4), np.uint8)
mask[..., 0] = (metal * 255 + 0.5).astype(np.uint8)
mask[..., 1] = (ao * 255 + 0.5).astype(np.uint8)
mask[..., 2] = 0
mask[..., 3] = (smooth * 255 + 0.5).astype(np.uint8)

# ── Emissive:沿浅台阶那一圈给一条细光带(默认关着,要开就正好压在砖的轮廓上)──
emi_line = np.clip(1.0 - np.abs(edge - 96.0) / 5.0, 0.0, 1.0)
emi = np.zeros((SIZE, SIZE, 3), np.float32)
c = np.array([0.10, 0.62, 0.78], np.float32)
emi = emi_line[..., None] * c[None, None, :]
emi_img = (emi.clip(0, 1) * 255 + 0.5).astype(np.uint8)

# ── 落盘 ─────────────────────────────────────────────────────────────
os.makedirs(OUT, exist_ok=True)
Image.fromarray(base_img, 'RGB').save(os.path.join(OUT, 'T_TechFloor_BaseColor.png'))
Image.fromarray(mask, 'RGBA').save(os.path.join(OUT, 'T_TechFloor_URPMask.png'))
Image.fromarray(normal, 'RGB').save(os.path.join(OUT, 'T_TechFloor_Normal.png'))
Image.fromarray(emi_img, 'RGB').save(os.path.join(OUT, 'T_TechFloor_Emissive.png'))


# ── 自检:密度均匀性 + 无缝性 + 通道数值 ───────────────────────────────
def blocks(a, g=8):
    return a.reshape(g, a.shape[0] // g, g, a.shape[1] // g).mean(axis=(1, 3))


lum = disp.mean(axis=2)
bl = blocks(lum)
print('已写出 4 张到', OUT)
print('亮度 均值 %.3f  8x8 分块极差 %.4f  (越小越均匀)' % (lum.mean(), bl.max() - bl.min()))
print('纯黑像素占比 %.2f%%  (原图是 10.3%%)' % (100.0 * (lum < 0.047).mean()))
print('金属 R 均值 %.3f  平滑 A 均值 %.3f  AO G 均值 %.3f' % (
    mask[..., 0].mean() / 255, mask[..., 3].mean() / 255, mask[..., 1].mean() / 255))
# 无缝性:左右边界应连续(凹槽压在边界上 → 边界两侧应同为暗/同为亮)
seam_lr = np.abs(lum[:, 0] - lum[:, -1]).mean()
seam_tb = np.abs(lum[0, :] - lum[-1, :]).mean()
print('无缝检查  左右边界亮度差 %.4f  上下边界亮度差 %.4f  (同侧都是凹槽中心,应很小)' % (seam_lr, seam_tb))
nb = blocks(normal[..., 0].astype(np.float32))
print('法线 R 分块极差 %.3f  (几何分布均匀性)' % (nb.max() - nb.min()))
