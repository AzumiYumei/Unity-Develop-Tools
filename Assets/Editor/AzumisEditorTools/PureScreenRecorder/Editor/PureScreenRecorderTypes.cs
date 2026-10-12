using System;
using UnityEngine;

namespace EPT
{
    public enum ScreenRecorderState
    {
        Idle,
        CountingDown,
        Recording,
        Encoding,
        Completed,
        Failed,
        Cancelled
    }

    [Serializable]
    public class ScreenRecordingSettings
    {
        public int width = 1920;
        public int height = 1080;
        public int frameRate = 30;
        public float durationSeconds = 10;
        public float countdownSeconds = 0;
        public string outputDirectory = "";
        public string outputFileName = "";
        public string ffmpegPath = "ffmpeg";

        [Range(0, 51)]
        public int crf = 18;
    }
}
