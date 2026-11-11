# 二维码扫描（附属工具）

悬浮球右键菜单「二维码扫描」拉起的小工具：**截屏 → 全屏框选 → 解码 → 结果窗（自动复制）**。

## 架构边界（为什么独立）

- 与语音主程序 `voice_mikon` **零 import**：双向都不知道对方存在。
- 主程序侧只有一根线：菜单点击 → `subprocess` 拉起本工具（`python -m qr_scanner`，独立进程）。工具崩溃 / 被强杀不影响语音主程序。
- UI 走 tkinter（选区 + 结果窗），**不碰 pywebview**——不与主程序的透明 / 裁形那套纠缠。
- 坐标全程物理像素：进程先声明 per-monitor DPI aware，mss / tkinter / 选区一套坐标。

## 单独使用

依赖装在主项目同一个 venv（见主项目 `pyproject.toml` 的 `qr` 可选组）：

```powershell
pip install -e ".[qr]"
python -m qr_scanner              # 截屏 → 框选 → 识别
python -m qr_scanner --image a.png  # 直接识别一张图片
python -m qr_scanner --selftest   # 不弹窗：截屏 + 全图解码，链路自检
```

## 文件

| 文件 | 职责 |
|---|---|
| `__main__.py` | 入口与编排（依赖探测、`--image` / `--selftest`） |
| `capture.py` | mss 截取整个虚拟桌面（物理像素） |
| `selector.py` | tkinter 全屏选区窗：变暗底图 + 框内贴回原图 + 尺寸标签 |
| `decode.py` | pyzbar 解码；原图 → 反色 → 放大一倍逐级补救 |
| `result.py` | 结果窗 + win32 直写剪贴板（tk 的延迟提交在进程退出后会丢内容，所以不走它） |

## 已知取舍

- 解码只做轻补救（反色 / 放大）：截图场景的码清晰正对，zbar 一次就中；小码请框紧一点。
- 结果窗不自动关闭：读完自己关（Esc / 关闭按钮）。复制是 win32 直写，关了也不丢。
