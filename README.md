# 离线搜题

Windows 本地截图搜题程序。手动框选一道题，使用系统中文 OCR 和本地 DOCX 题库检索，答案浮窗只显示答案。

[下载 Windows 64 位版](https://github.com/zeqi-li/offline-question-search/releases/latest) · [验证范围](docs/VALIDATION.md)

## 使用

1. 从 Releases 下载 `offline-question-search-v1.0.0-win-x64.zip`，完整解压。
2. 双击 `离线搜题.exe`。程序同级目录需有符合下面格式的 DOCX 题库。
3. 点 **截图搜题**，拖选一道题的题干及完整选项，松开鼠标后检索。
4. 点 **结束检测** 取消识别并关闭答案。框选时可按 **Esc** 或鼠标右键取消。

主界面只有两个功能按钮。每次点击重新框选，空闲时不轮询屏幕。选择题结果如 `C、3项`，多选题每个正确选项单独一行；判断和填空只显示相应答案。

支持选项顺序打乱、缺失标记的恢复、数字短选项检查和题型约束。定位不可靠时最多增强识别重试一次；仍不可靠则不显示答案。截图和 OCR 正文只在内存中处理。

运行要求：Windows 10/11 64 位、.NET Framework 4.8，以及 Windows 简体中文 OCR 组件。程序不需要 Python、独立大模型或云服务。若系统缺少中文文字识别组件，需先通过 Windows 语言选项安装该组件；运行过程可以离线。

## 题库格式

题库在启动时读取程序同级目录的 `.docx` 文件，更换后重启程序。选择题示例：

```text
单选题
1、观察四个整数：2、4、6、9。其中有几项属于偶数？
A、1项
B、2项
C、3项
D、4项
答案：C
```

多选答案示例为 `答案：ABD`；填空题使用 `答案：循环`；判断题使用 `答案：正确` 或 `答案：错误`。填空、判断题可在题干末尾加 `(填空题)`、`(判断题)`。

公开仓库及下载包**不包含原始用户题库、运行日志、真实题面截图或包含原题内容的测试报告**。`examples/` 和下载包仅提供独立编写的 8 道演示题，供功能验证。实际使用请自行提供题库；建议移走演示题库，避免与自己的题库同时加载。

## 从源码构建

无需安装 .NET SDK，使用 Windows 自带的 Framework 编译器与 WinMetadata：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

生成的程序为项目根目录的 `离线搜题.exe`。复制 `examples/demo-bank.docx` 到该目录即可体验；发布包已经带有演示题库。

生成完整 ZIP：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Package-Release.ps1 -Version 1.0.0
```

输出位于 `dist/`，包含程序、配置、演示题库、说明及 ZIP 校验和。

## 验证

在带有演示题库的程序目录中运行：

```powershell
$p = Start-Process '.\离线搜题.exe' -ArgumentList '--self-test' -Wait -PassThru
$p.ExitCode
```

返回 `0` 表示当前测试通过，报告写入 `verification/report.json`。自检覆盖题库回查、选项倒序、生成图片 OCR、题型/数字约束和取消操作。测试使用随附演示题库，包括用于短选项回归的第 14 题。

生成图片与纯文本测试不代表任意真实截图的准确率；低清晰度、特殊字体、过大的选区可能影响效果。性能数字及历史真实桌面验证的边界见 [VALIDATION.md](docs/VALIDATION.md)。

## 文件

- `src/`：WinForms 界面、截图、OCR、匹配与自检源码。
- `examples/`：公开演示数据与 DOCX。
- `scripts/`：演示题库生成、发布打包脚本。
- `logs/runtime.log`：使用时产生的状态、耗时、匹配分数、题型及题号；不记录截图与 OCR 正文，达到约 256 KB 后清空。

`logs/`、`verification/`、编译产物和一般 DOCX 题库已被 Git 忽略，避免后续提交时带入自己的数据。
