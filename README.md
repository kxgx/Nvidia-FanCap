# Nvidia-FanCap

Take control of your NVIDIA GPU fan instead of letting the vBIOS curve decide:
put a **hard ceiling** on the fan speed, or let the program **build a
temperature / power curve for you**.

给 NVIDIA 显卡风扇一个**真正的上限**，或者让程序**按温度和功耗自动生成曲线**；
低于上限时完全交还驱动（空载 0 RPM 照常）。

**One self-contained 64-bit Windows binary (daemon + settings UI) · no .NET runtime · MSI installer**

---

## What it does

| mode | in one line |
| --- | --- |
| `cap` | ceiling only: anything above `cap` % is pulled back within 250 ms |
| `fixed` | hold one constant fan speed |
| `curve` | follow a temperature -> fan curve and a temperature -> power-limit curve, smoothed and rate limited |
| `auto` | you set only the power limit; the program learns the card and derives the fan curve itself |

All modes share the same safety machinery: preemption before the vBIOS can jump
to 100 %, a temperature safety valve, hot reload of the settings file, and a
clean release back to automatic control on exit.

## When this is useful

- The card's own fan curve is too aggressive (blower / turbo coolers, OEM cards,
  cards with a cross-flashed vBIOS often jump to 100 % at 80 °C).
- You want a quiet machine at a chosen temperature/power target and are willing
  to trade some performance for it.
- You want the fan behaviour to be deterministic instead of vBIOS-defined.

It is **not** a vBIOS editor and it cannot remove the driver's thermal
protection - see [Limits](#limits-and-safety).

## Requirements

- Windows 10 / 11, 64-bit.
- An NVIDIA card whose fan is controllable through NVML (the tool reports a
  clear error when the card exposes no writable fan control).
- Administrator rights for the daemon (NVML manual fan control is privileged).
  The settings GUI does not need them.

## Install

**Installer (recommended)** - run the MSI in `dist\`:

- installs to `C:\Program Files\Nvidia-FanCap\`
- adds a Start Menu shortcut (opens the settings UI)
- registers a scheduled task that starts the daemon hidden at logon with admin
  rights (no UAC prompt)
- clean uninstall from *Settings -> Apps*

**Portable** - copy the files from `dist\` anywhere, then run
`Nvidia-FanCap-x64.exe --install` from an elevated prompt (removes with
`--uninstall`).

Settings are stored next to the executable, or in
`%LOCALAPPDATA%\Nvidia-FanCap\` when the install directory is not writable
(which is the normal case under Program Files). Both the GUI and the daemon
resolve the same file.

## Usage

```
Nvidia-FanCap-x64.exe                          settings UI (no admin needed)
Nvidia-FanCap-x64.exe --daemon                 run the daemon (needs administrator)
Nvidia-FanCap-x64.exe --daemon --hidden        same, without a console window
Nvidia-FanCap-x64.exe --install                register the hidden logon task
Nvidia-FanCap-x64.exe --uninstall              remove the task again
Nvidia-FanCap-x64.exe --status                 print settings + GPU state
Nvidia-FanCap-x64.exe --set cap=45             change settings without the GUI
Nvidia-FanCap-x64.exe --daemon --log file --verbose --duration 60   debug run
```

The GUI has one checkbox per advanced mode and a slider for the power limit;
every change is written to the ini and applied by the daemon within ~5 seconds,
no restart needed.

## Settings (`Nvidia-FanCap.ini`)

| key | default | meaning |
| --- | --- | --- |
| `mode` | `cap` | `cap`, `fixed`, `curve` or `auto` |
| `cap` | 40 | fan ceiling (cap mode) or constant speed (fixed mode), percent |
| `preempt` | 75 | take control at this temperature (0 = reactive only) |
| `release` | 45 | hand control back below this temperature |
| `valve` | 90 | safety valve temperature |
| `valve_reenable` | 84 | re-arm the ceiling below this temperature |
| `valve_enabled` | 1 | keep the safety valve on |
| `power_limit` | 0 | power limit in watts (0 = leave the card alone) |
| `interval` | 250 | fastest poll period in ms while policing |
| `fan` | -1 | which fan to control (-1 = automatic) |
| `target_temp` | 78 | auto mode: temperature the fitted curve aims at |
| `curve_temp` / `curve_fan` | see ini | curve mode: temperature -> fan knots (°C / %) |
| `power_curve_temp` / `power_curve_watt` | see ini | curve mode: temperature -> power-limit knots |
| `temp_ema` | 0.25 | temperature smoothing (lower = smoother, slower) |
| `fan_rate_up` / `fan_rate_down` | 2.0 / 1.0 | max fan change per tick (percent) |
| `fan_per_degree` | 4.5 | auto mode calibration: fan % per degree °C |
| `deg_per_watt` | 0.035 | auto mode calibration: steady °C per watt |

The shipped ini documents every key and the defaults are usable as-is.

## Curve mode (`mode=curve`)

- the fan target comes from a shape-preserving (PCHIP) interpolation of the
  `curve_temp` / `curve_fan` knots, so the curve never overshoots between knots;
- temperature is exponentially smoothed (`temp_ema`) and the fan moves with
  hysteresis and per-tick rate limits (`fan_rate_up/down`), so ramps are smooth
  and inaudible as steps;
- the power limit follows its own curve: down immediately, up at most a few
  watts per tick;
- below the bottom of the curve the fan is released to the driver (0-RPM idle)
  and retaken a couple of degrees later;
- the safety valve releases both the fan and the power limit.

## Auto mode (`mode=auto`)

You set the power limit and a target temperature; the program derives the fan
curve:

1. It observes the card only at steady state (fan, temperature and power inside
   a narrow band over a ~40 s window, and the measured fan must match the
   commanded one, so a driver override never teaches it nonsense).
2. From each reliable observation it updates the **anchor** - the fan percent
   that holds the target temperature at the current power - with a damped
   correction using two calibration constants (`fan_per_degree`, `deg_per_watt`).
3. The fan curve is the line through `(target_temp, anchor)` with slope
   `1 / fan_per_degree`, clamped to 0-100 %. Changing the power limit moves the
   operating point and the anchor follows.
4. The anchor is persisted (`%LOCALAPPDATA%\Nvidia-FanCap\model.txt`) and reused
   on the next start; a fresh install starts from a value derived from the
   configured power limit.

The two calibration constants default to values measured on a reference blower
card. On a very different cooler, adjust them: `fan_per_degree` is roughly how
many fan percent move the temperature by 1 °C at steady state, `deg_per_watt` is
the steady temperature rise per watt of board power. A rough measurement can be
made by running the fan at two fixed speeds under a steady load.

## Reference measurements

These numbers come from one specific card (a 310 W blower card at 97-98 % load)
and are kept as an illustration, not as a guarantee:

| power limit | GPU temp at 40 % fan | fan forced to 100 % by the driver |
| --- | --- | --- |
| 220 W | 81-86 °C | 12 / 35 samples |
| 180 W | 79-85 °C | 2 / 35 samples |
| **150 W** | **79-84 °C** | **0** |
| 120 W | 79-85 °C | 0 |

Curve mode at 98 % load with the default curves: 79-84 °C, fan 53-54 %, maximum
change between 500 ms samples 1 %, no driver overrides. Auto mode with a 310 W
limit: anchor learned 73.8 -> 78.5 %, 77-82 °C, zero overrides.

## Troubleshooting

| symptom | check |
| --- | --- |
| GUI says "daemon not running" | install the MSI, or run `Nvidia-FanCap-x64.exe --install` from an elevated prompt |
| nothing changes after editing | the daemon re-reads the ini every ~5 s; make sure you edited the file shown in the GUI (`%LOCALAPPDATA%\Nvidia-FanCap\...` when installed) |
| fan still jumps to 100 % | the driver forces full speed above its thermal threshold; lower the power limit, or use auto mode with a lower `target_temp` |
| settings do nothing | another program owns the fan - the NVIDIA App (performance/auto-tuning), MSI Afterburner or FanControl; disable their GPU fan control |
| writes rejected | the daemon must run elevated (the installed task does this automatically) |
| need details | run the daemon with `--log file --verbose`, e.g. `Nvidia-FanCap-x64.exe --daemon --log fan.log --verbose` |

## Limits and safety

- The driver's thermal protection is not removable by software: many cards force
  the fan to maximum above a fixed temperature. A low ceiling therefore needs
  low heat (power limit) to be sustainable.
- Do not run two fan controllers for the GPU fan at the same time; the last
  writer wins and the result is unpredictable. Motherboard fans are unaffected.
- Verify memory / hot-spot temperatures after lowering the fan speed; they share
  the cooler with the GPU and the safety valve is on by default.
- The daemon restores automatic fan control on a clean exit. A force-kill leaves
  the last manual setting in place until the next launch or reboot.

## Build from source

The CI workflow (`.github/workflows/build.yml`) does exactly this on every push;
the same two commands build everything locally:

```
# single-file native binary (NativeAOT; needs the MSVC build tools)
dotnet publish Nvidia-FanCap.csproj -c Release -o dist

# MSI installer (WiX)
dotnet tool install --global wix --version 6.0.2
wix build -arch x64 Product.wxs -o dist/Nvidia-FanCap-<version>-x64.msi
```

- `Nvidia-FanCap.csproj` - daemon, GUI and installer helper in one project,
  published with **NativeAOT** to a single `Nvidia-FanCap-x64.exe`.
- `Product.wxs` - the MSI definition, built with the
  [WiX Toolset](https://wixtoolset.org/) 6.x.
- All paths are relative - the repository can live anywhere; there are no build
  scripts in the repository, the workflow and these two commands are the whole
  build.

## Uninstall

Use *Settings -> Apps*, or run `Nvidia-FanCap-x64.exe --uninstall` from an
elevated prompt for a portable install. Fan control returns to the driver/vBIOS
immediately.

---

## 中文说明

- **四种模式**
  - `cap` 上限锁定：超过 `cap`% 立即强制拉回，低于上限不干预（空载 0 RPM 保留）
  - `fixed` 固定转速
  - `curve` 温度曲线：风扇按温度曲线走，功耗墙按另一条曲线走，带平滑/滞回/限速
  - `auto` 自动拟合：你只调功耗墙和目标温度，程序自己学习并生成风扇曲线
- **安全机制**：预接管温度（默认 75 °C，赶在 vBIOS 满转前接管）、保险阀（默认 90 °C 放开上限让显卡自保，降温后重新上锁）、退出时交还自动控制
- **安装**：双击 `dist\` 里的 MSI（装到 Program Files、开始菜单、开机隐藏自启、可从"设置→应用"卸载）；绿色使用则拷贝 `dist\` 后以管理员运行 `Nvidia-FanCap-x64.exe --install`
- **改设置**：双击 `Nvidia-FanCap-x64.exe`（原生界面、跟随系统深浅色、无需管理员），约 5 秒热生效；设置文件在安装目录或 `%LOCALAPPDATA%\Nvidia-FanCap\`
- **自动拟合的两个标定参数**：`fan_per_degree`（每 1 °C 需要多少风扇百分比）和 `deg_per_watt`（每瓦功耗带来多少 °C 温升），默认值来自一块参考卡；换用差别很大的散热器时建议重新标定
- **注意**：不要同时运行其它显卡风扇控制软件（NVIDIA App 的性能调优、MSI Afterburner、FanControl），否则互相抢控制权；驱动在高温下的强制满转是硬件保护，软件无法消除，降低功耗墙才是根本手段
- **构建**：CI（`.github/workflows/build.yml`）与本地两条命令：`dotnet publish Nvidia-FanCap.csproj -c Release -o dist` + `wix build -arch x64 Product.wxs`（仓库内不再有 bat 脚本）

