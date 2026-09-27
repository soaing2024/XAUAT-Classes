# 第三方说明

## 配色

界面配色（porcelain 青瓷蓝 / graphite 石墨暗色）取自开源 skill **lieflat-charts**
（https://github.com/larashero3-dotcom/lieflat-charts ，由「躺在废墟里」开发，MIT）。
本项目只引用了其中的颜色数值与「明度即数据、不用渐变与阴影」的视觉约定，
图标、窗口、动画与全部绘制代码均为本项目自行实现，未复制该 skill 的模板代码。

## 运行环境

- .NET 10 / WPF / Windows Forms（仅托盘 `NotifyIcon`）/ Win32 互操作（DWM、DPAPI）
- 无任何第三方 NuGet 依赖

## 图标

`Resources/app.ico` 由本项目的 `tools/gen-icon.mjs` 生成（圆角蓝底 + 白色课表格），
多尺寸 16/24/32/48/64/128/256。
