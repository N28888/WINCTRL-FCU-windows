# WINCTRL 32 FCU 控制器

把 `WINCTRL 32 FCU` 用作 Windows 11 的音量和显示器亮度控制器。

## 功能

- FCU 按钮、旋钮方向和推拉动作自由学习映射
- 同一控件 30ms 内最多触发一次，避免按键抖动或重复 HID 上报
- 控制当前默认音频输出的主音量与静音
- 任意 FCU 按键可绑定指定音频输出设备，并用 LOC/AP1/AP2/A/THR/EXPED/APPR LED 显示当前默认设备
- 任意 FCU 按键可绑定本机 `.exe` 应用程序或 `.lnk` 快捷方式
- 控制笔记本内屏亮度，以及支持 DDC/CI 的外接显示器
- 多显示器相对增减、操作浮层、热插拔恢复
- 手动启动、最小化/关闭到托盘、托盘右键退出
- 检测 SimAppPro、MobiFlight 等飞行软件后自动清屏并释放 FCU
- SPD/HDG/ALT/V/S 数码窗及六个 LED 的自由输出绑定
- ALT、V/S 旋钮方向可分别学习为 FCU LCD 亮度和按键背光调节；默认在对应的 ALT、V/S 数码窗显示当前百分比
- 按 WINCTRL 协议维持硬件心跳，避免数码窗输出会话超时冻结

## 使用

1. 完全退出 SimAppPro、MobiFlight 等会占用 FCU 的程序。
2. 连接 WINCTRL 32 FCU，运行 `FcuControl.exe`。
3. 打开“输入映射”，点击某个动作后的“学习”，再操作 FCU 控件。
4. 打开“音频切换”，添加绑定并选择目标输出设备、联动 LED，再点击“学习”录入按键。被选中的 LED 会覆盖“FCU 输出”页中该灯的普通绑定。
5. 打开“启动软件”，添加绑定、选择软件，再点击“学习”录入按键。
6. 在“显示器”中勾选需要控制的屏幕。
7. 如需用 ALT 旋钮调 LCD 亮度、V/S 旋钮调按键背光，请在“输入映射”中分别学习“FCU LCD 亮度增加/减少”和“FCU 背光增加/减少”。
8. “FCU 输出”页可以调整硬件亮度的旋钮步长，并给数码窗和其余 LED 选择数据来源。默认 ALT 窗显示 LCD 亮度，V/S 窗显示按键背光亮度。

首页步长表示每次有效 HID 触发所改变的实际百分点，例如设置为 `1%` 时只改变一个百分点。

设置和最近七天日志位于 `%LocalAppData%\FcuControl`。程序不会设置开机启动，也不需要管理员权限。

外接显示器必须在显示器菜单中启用 DDC/CI。显示器不支持标准亮度命令时，程序会标记为不可控制，不使用 Gamma 软件调暗。

## 构建

需要 .NET 10 SDK：

```powershell
dotnet restore .\FcuControl.slnx
dotnet test .\FcuControl.slnx -c Release
dotnet publish .\src\FcuControl.App\FcuControl.App.csproj -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -o .\artifacts\FcuControl-win-x64
```

本项目面向个人、非商业用途。第三方组件及 FCU 协议参考见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
