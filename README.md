# CPUCK Test

一个 Windows CPU / GPU 稳定性与温度压力测试工具，目标是做成类似 AIDA64 System Stability Test 的轻量版。

## 当前功能

- CPU 满载压力测试
- NVIDIA CUDA GPU 满载压力测试
- CPU + GPU 同时测试
- CPU / GPU 温度实时显示
- CPU / GPU 功耗、负载显示
- 温度曲线
- CSV 日志
- 测试时长：1 / 2 / 5 / 10 / 15 / 30 分钟
- CPU 默认达到 100°C 自动停止
- GPU 默认达到 90°C 自动停止
- 单文件 Windows x64 EXE
- GitHub Actions 自动编译

## 直接下载 EXE

打开仓库的 **Actions** → `build-windows-exe` → 最新一次成功运行 → Artifacts → `CPUCKTest-win-x64`。

解压后直接运行：

```text
CPUCKTest.exe
```

无需安装 .NET Runtime。

## 使用方法

1. 运行 `CPUCKTest.exe`
2. 勾选 `Stress CPU`、`Stress GPU`，也可以两个一起勾选
3. 选择测试时长
4. 点击 `Start`
5. 观察 CPU / GPU 温度、功耗、负载和温度曲线
6. 达到安全温度上限会自动停止

日志保存在 EXE 同目录：

```text
Logs\CPUCK_yyyyMMdd_HHmmss.csv
```

## 注意

压力测试会让 CPU / GPU 长时间处于高负载。老化散热、积灰、硅脂失效的电脑可能很快达到温度墙。虽然程序提供自动停止保护，但第一次使用时建议先跑 1~2 分钟并观察温度。

GPU 压力测试当前使用 CUDA，因此主要支持 NVIDIA 显卡。没有检测到 CUDA GPU 时，CPU 测试仍可正常进行。

## 开发环境

- C# / .NET 8
- WinForms
- LibreHardwareMonitorLib
- ILGPU + CUDA

本地编译：

```powershell
dotnet publish CPUCKTest.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```
