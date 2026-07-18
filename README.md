# ConnectPad

Windows 上的 Android USB / Wi-Fi 低延迟投屏工具。无需安装 Android 应用。

## 使用

1. 解压发布包并运行 `ConnectPad.exe`。
2. 在 Android 设备上开启“开发者选项”。

### USB

1. 开启“USB 调试”，用数据线连接电脑。
2. 在设备上允许本机调试。
3. 在 ConnectPad 选择设备，点击“启动投屏”。

### Wi-Fi（Android 11+）

1. 电脑和设备连接同一可信 Wi-Fi，打开“无线调试”。
2. 在“使用配对码配对设备”页面，将配对地址和六位码填入 ConnectPad，点击“配对”。
3. 若设备没有自动出现，将无线调试主页显示的连接地址填入“连接地址”，点击“连接”。
4. 选择设备，点击“启动投屏”。

配对地址与连接地址通常使用不同端口。

无线启动时，ConnectPad 会自动检测设备编码器：存在硬件 H.265 时使用经本机 2.4 GHz 三轮测试选出的 H.265 4 Mbps；否则使用 H.264 8 Mbps。两条路径都固定最长边 1920、最高 60 FPS，且禁止编码失败时静默降低分辨率。Wi-Fi 状态读取或编码器检测失败不会阻止投屏，状态栏会显示实际编码、码率、频段和回退原因。USB 路径仍使用原有 H.264 参数。

## 操作

- 点击和拖动：触摸操作
- 右键：返回
- 中键：主页
- `F11`：全屏
- `Win+Shift+S`：Windows 截图

## 常见问题

- 未检测到 USB 设备：确认数据线支持传输、USB 调试已开启，并安装设备厂商的 Windows USB 驱动；若此前使用过无线连接，请点击“刷新”，ConnectPad 会自动重启一次内置 ADB 并重新扫描 USB。
- 设备未授权：解锁设备并接受 USB 调试授权。
- 无线连接失败：确认位于同一 Wi-Fi，并核对无线调试主页当前显示的地址。
- 小米等部分设备无法键鼠控制：开启“USB 调试（安全设置）”后重启设备。
- 首次运行被 SmartScreen 拦截：当前便携包未签名，请确认文件来源后选择继续运行。

## 隐私

ConnectPad 无账号、无遥测。USB 数据不经过互联网；无线调试仅用于当前局域网。请勿在不可信网络开启无线调试。

第三方组件见 `THIRD_PARTY_NOTICES.md` 和 `tools/scrcpy/LICENSE.txt`。

## 无线基准测试（开发者）

先关闭现有 scrcpy 窗口，再在项目根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\benchmark-wireless.ps1 -Serial 192.168.1.8:37123
```

默认依次测试 H.264 8 Mbps 与 H.265 6/5/4 Mbps，每种配置运行三次、每次五分钟；脚本会打开系统设置并持续往返滚动，生成可重复的动态画面。录像、FPS 日志、Ping、Wi-Fi 前后快照和汇总表写入 `artifacts/benchmarks/<时间戳>`。目录中的 `latency-samples.csv` 用于录入高速相机采集的显示延迟样本。若要手动播放固定测试片源进行 VMAF 对比，请加入 `-NoUiExercise`。当前设备已根据三轮帧率、跳帧和抽帧清晰度结果选择 H.265 4 Mbps。
