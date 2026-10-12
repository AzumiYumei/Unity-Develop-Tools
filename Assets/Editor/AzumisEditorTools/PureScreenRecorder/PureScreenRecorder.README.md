# PureScreenRecorder

Editor-only camera recorder for the Unity project. It records video only, without audio. The output comes from a selected Camera rendered into an offscreen RenderTexture, so Editor windows and tool panels are never captured.

## Location

The tool lives under `Assets/Editor/AzumisEditorTools/PureScreenRecorder`, so it is available in the Unity Editor and is not included in Player builds.

## Usage

1. Open `Tools/Recording/Pure Screen Recorder`.
2. Enter Play Mode.
3. Select the Camera to record, or click `Main` to use `Camera.main`.
4. Set `Width`, `Height`, `Frame Rate`, `Duration Seconds`, `Countdown Seconds`, output folder/file, and ffmpeg path.
5. Keep `Fixed Game Time` enabled for smooth fixed-step output that is not tied to Editor realtime performance.
6. Keep `Flip Vertical` enabled for normal RenderTexture readback orientation. If your result is upside down on a specific graphics backend, turn it off.
7. Click `Start Recording`; click `Stop` to cancel.

## Exact mp4 output

The recorder captures exactly `durationSeconds * frameRate` frames and sends them to ffmpeg as constant-frame-rate raw video. For exact duration, `durationSeconds * frameRate` must be an integer.

Examples:

- `10s * 30fps = 300 frames`
- `5s * 60fps = 300 frames`

Width and height must be even because the output uses H.264 `yuv420p`. The mp4 metadata is written with the configured frame rate and exact frame count, so the file duration is `frameCount / frameRate`.

## ffmpeg

Set `ffmpeg` to `ffmpeg` if it is available in PATH, or browse to an absolute `ffmpeg.exe` path.
