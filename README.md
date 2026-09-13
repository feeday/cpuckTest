# CPUCK Test

一个 Windows CPU / GPU / RAM / SSD 稳定性与硬件监控工具，目标是做成类似 AIDA64 System Stability Test 的轻量版。

## 当前功能

- CPU 满载压力测试
- NVIDIA CUDA GPU 满载压力测试
- RAM 读写压力测试与实时带宽
- SSD Stress / Benchmark 两种模式
- 多硬盘选择：显示并测试所选物理盘
- CPU / GPU 温度、功耗、频率、负载实时显示
- RAM 占用、频率，以及设备支持时显示 RAM 温度
- SSD 温度、读写速度、活动率、单次累计写入量
- CSV 测试日志导出
- 测试时长：1 / 2 / 5 / 10 / 15 / 30 分钟
- CPU 默认达到 100°C 自动停止
- GPU 默认达到 90°C 自动停止
- SSD 达到 80°C 自动停止
- 固定尺寸界面，启动时直接显示最终布局
- 单文件 Windows x64 EXE
- GitHub Actions 自动编译

> 温度历史曲线和悬浮窗已移除，避免无效占位、启动闪烁和重复硬件轮询。

## 直接下载 EXE

打开仓库的 **Actions** → `build-windows-exe` → 最新一次成功运行 → Artifacts → `CPUCKTest-win-x64`。

解压后直接运行：

```text
CPUCKTest.exe
```

无需安装 .NET Runtime。

## 使用方法

1. 运行 `CPUCKTest.exe`
2. 勾选需要测试的 CPU / GPU / RAM / SSD
3. SSD 测试时选择目标硬盘与 `Stress` 或 `Benchmark`
4. 选择测试时长
5. 点击 `Start`
6. 观察实时温度、功耗、频率、负载、内存/硬盘吞吐
7. 达到安全温度上限时程序自动停止

日志保存在 EXE 同目录：

```text
Logs\CPUCK_yyyyMMdd_HHmmss.csv
```

## SSD 模式

- `Stress`：WriteThrough 持续压力，单次最多写入 8 GB，主要观察温度、稳定性与持续性能。
- `Benchmark`：缓存顺序 I/O，单次最多写入 4 GB，主要观察峰值读写性能。
- 临时测试文件位于所选磁盘的 `CPUCKTestTemp`，停止后会自动删除。

## 注意

压力测试会让 CPU / GPU / RAM / SSD 长时间处于高负载。老化散热、积灰、硅脂失效的电脑可能很快达到温度墙。虽然程序提供自动停止保护，但第一次使用时建议先跑 1~2 分钟并观察温度。

GPU 压力测试当前使用 CUDA，因此主要支持 NVIDIA 显卡。没有检测到 CUDA GPU 时，CPU / RAM / SSD 测试仍可正常进行。

CPU 温度等底层数据当前仍由 LibreHardwareMonitor 读取。

## 开发环境

- C# / .NET 8
- WinForms
- LibreHardwareMonitorLib
- ILGPU + CUDA

本地编译：

```powershell
dotnet publish CPUCKTest.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```
