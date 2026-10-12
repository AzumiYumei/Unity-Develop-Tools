# Pure Screen Recorder

在 Play Mode 中将指定相机渲染到离屏 RenderTexture，按配置帧率和固定帧数编码视频。仅记录视频，不录制音频或编辑器窗口。

## 入口与依赖

- 菜单：`Tools/Recording/Pure Screen Recorder`。
- 脚本：[PureScreenRecorderWindow.cs](Editor/PureScreenRecorderWindow.cs)、[PureScreenRecorderTypes.cs](Editor/PureScreenRecorderTypes.cs)。
- 需要 ffmpeg，支持 `libx264` 编码；设置为 PATH 中的 `ffmpeg` 或完整可执行文件路径。

## 使用步骤

1. 进入 Play Mode，指定 Camera，或点击 `Main` 使用 `Camera.main`。
2. 设置 Width、Height、Frame Rate、Duration Seconds 和 Countdown Seconds。
3. 设置 Folder、File Name、ffmpeg 路径与 CRF，CRF 越低质量越高，通常文件也更大。
4. 默认保留 `Fixed Game Time`，使游戏时间按采集帧率推进；检查 `Flip Vertical` 保证方向正确。
5. 点击 `Start Recording`，等待倒计时、捕获和编码完成。`Stop` 用于提前取消；退出 Play Mode 也会取消。

## 帧数与输出

- `durationSeconds × frameRate` 必须是整数，例如 10 秒 × 30 fps = 300 帧，5 秒 × 60 fps = 300 帧。
- 完整录制输出时长为总帧数 / 帧率；宽高需为偶数以适配 H.264 `yuv420p`，窗口会规范化尺寸。
- 输出无音频 MP4，可选择完成后显示文件目录。
- 开始录制前会直接删除同名目标文件，不弹出覆盖确认；需要保留旧视频时更改文件名或输出目录。

## 使用限制

固定游戏时间模式会影响录制期间的时间推进，实际等待时长不一定等于成片时长。退出 Play Mode、取消或编码失败不能视为成功得到目标时长的视频。相机脚本、后处理及所用图形后端会影响离屏捕获效果；方向反了可切换 Flip Vertical。

需要轨迹预设和机位运镜时使用 [截图与录屏工具](../ScreenshotTool/README.md)。

[返回项目首页](../../../../README.md)
