# Nvidia-FanCap

Hard ceiling enforcement for NVIDIA GPU fans - the fan may spin up to the limit
you set, and anything above it is forced back down within 250 ms.

用软件把 NVIDIA 显卡风扇**上限硬锁死**：转速超过设定值立即抢回手动控制并强制压回，
低于上限时完全不干预（空载 0 RPM 照常）。

**Single 2 MB native binary (daemon + settings UI) · 64-bit · Windows 10/11 · MSI installer**

---

## Why

Some cards (blower / turbo cards, cross-flashed vBIOS) run their fan to 100% as
soon as the GPU passes ~80 °C. This tool takes fan control away from the vBIOS
curve before that happens and keeps the fan at your ceiling.

涡轮卡、刷了别家 vBIOS 的卡常在 80 °C 直接满转。本工具提前接管风扇并锁死上限。

## Features

- **Ceiling, not a floor** - below the limit the driver keeps full control (idle
  0 RPM works); only over-limit fan speeds are pulled back.
- **Preempt** - takes control at a temperature you choose (default 75 °C), so the
  vBIOS never gets the chance to jump to 100% at 80 °C.
- **Safety valve** - at 90 °C (configurable) the ceiling is disarmed so the card
  can protect itself; it re-arms when cool again. On by default.
- **Live settings** - edit `Nvidia-FanCap.ini` or use the GUI; the daemon
  hot-reloads within ~5 seconds, no restart.
- **One tiny binary** - daemon + settings UI + installer helper in a single
  NativeAOT executable: no .NET runtime, no UI framework, ~30 MB RAM (22 MB of
  which is NVIDIA's `nvml.dll`), ~0.2% CPU at idle, zero disk writes unless you
  enable logging.
- **Native system UI** - the settings window uses stock Win32 controls and
  follows the Windows light/dark preference; no admin rights needed.

## Install

**Installer (recommended)** - run `dist\Nvidia-FanCap-1.0.2-x64.msi`:

- installs to `C:\Program Files\Nvidia-FanCap\`
- adds a Start Menu shortcut (opens the settings UI)
- registers a scheduled task that starts the daemon hidden at logon with admin
  rights (no UAC prompt)
- full uninstall support (Settings → Apps)

**Portable** - copy `dist\` anywhere and run `install-task.bat` as administrator.

Change settings any time by double clicking `Nvidia-FanCap-x64.exe` (or the
Start Menu shortcut).

## Usage

```
Nvidia-FanCap-x64.exe                    settings UI (no admin needed)
Nvidia-FanCap-x64.exe --daemon           runs the daemon (needs administrator)
Nvidia-FanCap-x64.exe --daemon --hidden  same, without a console window
Nvidia-FanCap-x64.exe --install          register the hidden logon task
Nvidia-FanCap-x64.exe --uninstall        remove the task again
Nvidia-FanCap-x64.exe --status           print settings + GPU state
Nvidia-FanCap-x64.exe --set cap=45       change settings without the GUI
Nvidia-FanCap-x64.exe --daemon --log file --verbose --duration 60   debug
```

## Settings (`Nvidia-FanCap.ini`)

| key | default | meaning |
| --- | --- | --- |
| `cap` | 40 | fan ceiling in percent |
| `mode` | cap | `cap` = ceiling only, `fixed` = constant speed |
| `preempt` | 75 | take control at this temperature (0 = reactive only) |
| `release` | 45 | hand control back below this temperature |
| `valve` | 90 | safety valve temperature |
| `valve_reenable` | 84 | re-arm the ceiling below this temperature |
| `valve_enabled` | 1 | keep the safety valve on |
| `power_limit` | 0 | power limit in watts applied at start (0 = unchanged) |
| `interval` | 250 | fastest poll period in ms while policing |
| `fan` | -1 | which fan to control (-1 = automatic) |

## Build from source

```
build.bat
```

- `Nvidia-FanCap.csproj` - everything (daemon, GUI, installer helper), published
  with **NativeAOT** to one native `Nvidia-FanCap-x64.exe`.
- `Product.wxs` + `make-installer.bat` - the MSI, built with the
  [WiX Toolset](https://wixtoolset.org/) (v5, MIT) which `build.bat` fetches into
  `.tools\` on first run.
- All paths are relative (`%~dp0`) - the repository can live anywhere, nothing
  is hardcoded.

## Verify

To confirm the core promise, pin the fan to a fixed speed (e.g. 60% with
`--daemon --mode fixed --cap 60`), then run the daemon with a lower ceiling
(`--daemon --cap 25 --preempt-temp 0 --release-temp 20 --verbose --log check.log`)
- the log must show `CEILING ENGAGED (fan 60% > cap 25%)`, i.e. the over-limit
  fan was detected and forced back down.

## Uninstall

Use "Nvidia-FanCap" in Settings → Apps, or run `uninstall-task.bat` for the
portable install. Fan control returns to the driver/vBIOS immediately.

## Safety

- The daemon needs administrator rights (NVML manual fan control is a
  privileged operation); the scheduled task runs elevated without a UAC prompt.
- Do not run MSI Afterburner / FanControl GPU fan control at the same time -
  they will fight over the fan. Motherboard fans are fine.
- The GPU memory (GDDR6X) and VRM are cooled by the same fan: after lowering the
  ceiling, check memory temperature and hot spot in GPU-Z occasionally (keep
  them below ~95 °C). The safety valve is on by default.

---

## 中文说明

- **风扇上限**：超过就强制拉回（250ms 内接管），低于上限不干预，空载 0RPM 保留
- **预接管温度**：默认 75 °C，抢在 vBIOS 80 °C 满转之前接管
- **保险阀**：默认 90 °C 自动放开上限让显卡自保，降温后重新上锁
- 安装：双击 `dist\Nvidia-FanCap-1.0.2-x64.msi`（装到 Program Files、开始菜单、
  开机隐藏自启、可从"设置→应用"完整卸载）；绿色使用则拷贝 `dist\` 后运行 `install-task.bat`
- 改设置：双击 `Nvidia-FanCap-x64.exe`（原生界面、跟随系统深浅色、无需管理员），约 5 秒热生效
- 构建：`build.bat`（生成单文件 exe + MSI 安装包）
