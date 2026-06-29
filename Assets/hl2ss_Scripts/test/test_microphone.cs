using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

public class test_microphone : MonoBehaviour
{
    public GameObject audio_source_object;
    public float startBufferSeconds = 0.25f;
    [Tooltip("Gain applied to received HL2SS microphone samples before playback.")]
    public float playbackGain = 4f;
    public bool logAudioStatus = false;
    public bool saveAudioDiagnosticsToFile = false;
    public string audioDiagnosticsFolderName = "AudioDiagnostics";
    [Range(0.1f, 1f)]
    [Tooltip("Ceiling used by the soft limiter after playbackGain. Lower values reduce distortion and clipping.")]
    public float playbackLimiterCeiling = 0.9f;
    public bool recordIncomingAudio = false;
    public string audioWavFolderName = "AudioWavCaptures";
    public string incomingAudioWavSubfolderName = "HL2SSReceiver";
    public string recordFileName = "hl2ss_microphone_incoming.wav";
    public bool timestampAudioWavFiles = true;
    [Tooltip("Keep this off in the Unity Editor if stopping Play Mode crashes while closing the native HL2SS stream.")]
    public bool disposeHl2ssStreamInEditor = false;

    private readonly object bufferLock = new object();
    private hl2ss.shared.source source_microphone;
    private AudioSource audio_source;
    private long index;
    private List<float> buffer;
    private int startBufferSampleValues;
    private float nextStatusLogTime;
    private int receivedPacketCount;
    private float lastInputRms;
    private float lastInputPeak;
    private float[] outputProbeBuffer;
    private StreamWriter audioDiagnosticsWriter;
    private float nextAudioDiagnosticsFlushTime;
    private Pcm16WavWriter incomingAudioWavWriter;
    private int incomingAudioWavSampleRate;
    private int incomingAudioWavChannels;
    private bool isShuttingDown;

    void Start()
    {
        var configuration = new hl2ss.ulm.configuration_microphone();

        try
        {
            hl2ss.svc.open_stream(run_once.host_address, hl2ss.stream_port.MICROPHONE, 1000, configuration, true, out source_microphone);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("test_microphone: Could not open HL2SS microphone stream: " + e.Message);
            enabled = false;
            return;
        }

        index = -1;
        buffer = new List<float>();
        startBufferSampleValues = Mathf.Max(1, Mathf.RoundToInt(hl2ss.parameters_microphone.SAMPLE_RATE * hl2ss.parameters_microphone.CHANNELS * Mathf.Max(0f, startBufferSeconds)));

        audio_source = EnsureAudioSource();
        if (audio_source == null)
        {
            Debug.LogWarning("test_microphone: Could not create an AudioSource for HL2SS microphone playback.");
            enabled = false;
            return;
        }

        audio_source.clip = AudioClip.Create("audio_mc", 4 * hl2ss.parameters_microphone.GROUP_SIZE_AAC, hl2ss.parameters_microphone.CHANNELS, (int)hl2ss.parameters_microphone.SAMPLE_RATE, true, OnAudioRead);
        EnsureUsableAudioListener();
    }

    void OnAudioRead(float[] data)
    {
        if (isShuttingDown)
        {
            FillSilence(data);
            return;
        }

        int count = 0;

        lock (bufferLock)
        {
            while ((count < data.Length) && (count < buffer.Count))
            {
                data[count] = buffer[count];
                count++;
            }

            if (count > 0)
            {
                buffer.RemoveRange(0, count);
            }
        }

        while (count < data.Length)
        {
            data[count] = 0.0f;
            count++;
        }
    }

    void Update()
    {
        if (isShuttingDown)
        {
            return;
        }

        if (source_microphone == null || audio_source == null)
        {
            return;
        }

        try
        {
            using var packet = source_microphone.get_by_index(index);

            switch (packet.status)
            {
                case hl2ss.mt.status.DISCARDED: index = -1; return;
                case hl2ss.mt.status.WAIT: LogIdleStatus("waiting for HL2SS microphone packets"); return;
                case hl2ss.mt.status.OK: index = packet.frame_stamp + 1; break;
            }

            packet.unpack<float>(out hl2ss.map_microphone region);

            uint regionSampleCountRaw = region.count;
            int regionSampleCount = regionSampleCountRaw > (uint)int.MaxValue ? int.MaxValue : (int)regionSampleCountRaw;
            var samples = new float[regionSampleCount];
            Marshal.Copy(region.samples, samples, 0, samples.Length);
            MeasureFloatSamples(samples, samples.Length, out lastInputRms, out lastInputPeak);

            int queuedSampleValues;
            lock (bufferLock)
            {
                float[] packedSamples = hl2ss.microphone_planar_to_packed<float>(samples, hl2ss.parameters_microphone.CHANNELS);
                WriteIncomingAudioWav(packedSamples, packedSamples.Length, (int)hl2ss.parameters_microphone.SAMPLE_RATE, hl2ss.parameters_microphone.CHANNELS);
                ApplyGain(packedSamples, playbackGain, playbackLimiterCeiling);
                buffer.AddRange(packedSamples);
                queuedSampleValues = buffer.Count;
            }

            receivedPacketCount++;
            MeasureAudioSourceOutput(audio_source, ref outputProbeBuffer, out float outputRms, out float outputPeak);
            WriteAudioDiagnosticsRow("packet", receivedPacketCount, regionSampleCount, queuedSampleValues, audio_source.isPlaying, lastInputRms, lastInputPeak, outputRms, outputPeak);

            if (!audio_source.isPlaying && queuedSampleValues >= startBufferSampleValues)
            {
                audio_source.Play();
            }

            if (logAudioStatus && Time.unscaledTime >= nextStatusLogTime)
            {
                nextStatusLogTime = Time.unscaledTime + 2f;
                Debug.Log("test_microphone: packets=" + receivedPacketCount +
                          ", regionSamples=" + regionSampleCount +
                          ", queuedSampleValues=" + queuedSampleValues +
                          ", sourcePlaying=" + audio_source.isPlaying +
                          ", inputRms=" + lastInputRms.ToString("F4") +
                          ", inputPeak=" + lastInputPeak.ToString("F4") +
                          ", outputRms=" + outputRms.ToString("F4") +
                          ", outputPeak=" + outputPeak.ToString("F4"));
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("test_microphone: HL2SS microphone stream stopped after an error: " + e.Message);
            CloseMicrophoneStream();
            enabled = false;
        }
    }

    private void LogIdleStatus(string message)
    {
        if (!logAudioStatus || Time.unscaledTime < nextStatusLogTime)
        {
            return;
        }

        nextStatusLogTime = Time.unscaledTime + 2f;
        int queuedSampleValues = 0;
        if (buffer != null)
        {
            lock (bufferLock)
            {
                queuedSampleValues = buffer.Count;
            }
        }

        MeasureAudioSourceOutput(audio_source, ref outputProbeBuffer, out float outputRms, out float outputPeak);
        WriteAudioDiagnosticsRow("wait", receivedPacketCount, 0, queuedSampleValues, audio_source != null && audio_source.isPlaying, 0f, 0f, outputRms, outputPeak);

        Debug.Log("test_microphone: " + message +
                  ", packets=" + receivedPacketCount +
                  ", queuedSampleValues=" + queuedSampleValues +
                  ", sourcePlaying=" + (audio_source != null && audio_source.isPlaying) +
                  ", outputRms=" + outputRms.ToString("F4") +
                  ", outputPeak=" + outputPeak.ToString("F4"));
    }

    private AudioSource EnsureAudioSource()
    {
        GameObject sourceObject = audio_source_object != null ? audio_source_object : gameObject;
        if (sourceObject == null)
        {
            return null;
        }

        AudioSource source = sourceObject.GetComponent<AudioSource>();
        if (source == null)
        {
            source = sourceObject.AddComponent<AudioSource>();
        }

        ConfigureAudioSource(source);
        return source;
    }

    private static void ConfigureAudioSource(AudioSource source)
    {
        if (source == null)
        {
            return;
        }

        source.playOnAwake = false;
        source.loop = true;
        source.volume = 1f;
        source.spatialBlend = 0f;
        source.spatialize = false;
        source.dopplerLevel = 0f;
        source.panStereo = 0f;
        source.bypassEffects = true;
        source.bypassListenerEffects = true;
        source.bypassReverbZones = true;
        source.ignoreListenerPause = true;
    }

    private static void ApplyGain(float[] samples, float gain, float limiterCeiling)
    {
        if (samples == null)
        {
            return;
        }

        gain = Mathf.Max(0f, gain);
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = ApplySoftLimiter(samples[i] * gain, limiterCeiling);
        }
    }

    private static void FillSilence(float[] data)
    {
        if (data == null)
        {
            return;
        }

        for (int i = 0; i < data.Length; i++)
        {
            data[i] = 0f;
        }
    }

    private void OnDisable()
    {
        isShuttingDown = true;
        StopAudioPlayback();
        CloseMicrophoneStream();
        CloseAudioDiagnosticsWriter();
        CloseIncomingAudioWavWriter();
    }

    private void OnDestroy()
    {
        isShuttingDown = true;
        StopAudioPlayback();
        CloseMicrophoneStream();
        CloseAudioDiagnosticsWriter();
        CloseIncomingAudioWavWriter();
    }

    private void CloseMicrophoneStream()
    {
        if (source_microphone == null)
        {
            return;
        }

        hl2ss.shared.source source = source_microphone;
        source_microphone = null;

#if UNITY_EDITOR
        if (!disposeHl2ssStreamInEditor)
        {
            System.GC.SuppressFinalize(source);
            return;
        }
#endif

        try
        {
            source.Dispose();
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("test_microphone: Could not close HL2SS microphone stream cleanly: " + e.Message);
        }
    }

    private void StopAudioPlayback()
    {
        if (audio_source == null)
        {
            return;
        }

        try
        {
            audio_source.Stop();
            audio_source.clip = null;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("test_microphone: Could not stop AudioSource cleanly: " + e.Message);
        }
    }

    private void WriteIncomingAudioWav(float[] samples, int sampleCount, int sampleRate, int channels)
    {
        if (!recordIncomingAudio || samples == null || sampleCount <= 0)
        {
            return;
        }

        try
        {
            sampleRate = Mathf.Clamp(sampleRate, 8000, 48000);
            channels = Mathf.Clamp(channels, 1, 2);
            if (incomingAudioWavWriter == null ||
                incomingAudioWavSampleRate != sampleRate ||
                incomingAudioWavChannels != channels)
            {
                CloseIncomingAudioWavWriter();
                string directory = GetAudioWavDirectory(incomingAudioWavSubfolderName);
                string filePath = BuildAudioWavPath(directory, recordFileName, timestampAudioWavFiles);
                incomingAudioWavWriter = new Pcm16WavWriter(filePath, sampleRate, channels);
                incomingAudioWavSampleRate = sampleRate;
                incomingAudioWavChannels = channels;
                Debug.Log("test_microphone incoming audio WAV file: " + filePath);
            }

            incomingAudioWavWriter.WriteFloatSamples(samples, sampleCount);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("test_microphone: Could not write incoming audio WAV: " + e.Message);
            CloseIncomingAudioWavWriter();
            recordIncomingAudio = false;
        }
    }

    private void CloseIncomingAudioWavWriter()
    {
        if (incomingAudioWavWriter == null)
        {
            return;
        }

        try
        {
            incomingAudioWavWriter.Dispose();
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("test_microphone: Could not close incoming audio WAV cleanly: " + e.Message);
        }

        incomingAudioWavWriter = null;
        incomingAudioWavSampleRate = 0;
        incomingAudioWavChannels = 0;
    }

    private string GetAudioWavDirectory(string subfolderName)
    {
#if UNITY_EDITOR
        string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
#else
        string root = Application.persistentDataPath;
#endif
        string folderName = string.IsNullOrEmpty(audioWavFolderName) ? "AudioWavCaptures" : audioWavFolderName;
        string directory = Path.Combine(root, folderName);
        if (!string.IsNullOrEmpty(subfolderName))
        {
            directory = Path.Combine(directory, subfolderName);
        }

        return directory;
    }

    private static string BuildAudioWavPath(string directory, string fileName, bool includeTimestamp)
    {
        Directory.CreateDirectory(directory);
        string safeFileName = string.IsNullOrEmpty(fileName) ? "audio.wav" : fileName;
        string extension = Path.GetExtension(safeFileName);
        if (string.IsNullOrEmpty(extension))
        {
            extension = ".wav";
        }

        string name = Path.GetFileNameWithoutExtension(safeFileName);
        if (string.IsNullOrEmpty(name))
        {
            name = "audio";
        }

        if (includeTimestamp)
        {
            name += "_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        }

        return Path.Combine(directory, name + extension);
    }

    private void EnsureUsableAudioListener()
    {
        AudioListener.pause = false;
        AudioListener.volume = 1f;
        if (FindObjectOfType<AudioListener>() != null)
        {
            return;
        }

        Camera camera = Camera.main != null ? Camera.main : FindObjectOfType<Camera>();
        GameObject listenerObject = camera != null ? camera.gameObject : gameObject;
        listenerObject.AddComponent<AudioListener>();
        Debug.LogWarning("test_microphone: Added a missing AudioListener so received HL2SS audio can be heard on device.");
    }

    private static float ApplySoftLimiter(float value, float ceiling)
    {
        ceiling = Mathf.Clamp(ceiling, 0.1f, 1f);
        float abs = Mathf.Abs(value);
        if (abs <= ceiling)
        {
            return value;
        }

        float sign = value < 0f ? -1f : 1f;
        float excess = abs - ceiling;
        float limited = ceiling + (1f - ceiling) * (excess / (excess + 1f));
        return sign * Mathf.Min(limited, 1f);
    }

    private void WriteAudioDiagnosticsRow(string eventName, int packetCount, int regionSamples, int queuedSampleValues, bool sourcePlaying, float inputRms, float inputPeak, float outputRms, float outputPeak)
    {
        StreamWriter writer = GetAudioDiagnosticsWriter();
        if (writer == null)
        {
            return;
        }

        writer.WriteLine(string.Join(",",
            FormatFloat(Time.unscaledTime),
            eventName,
            packetCount.ToString(CultureInfo.InvariantCulture),
            regionSamples.ToString(CultureInfo.InvariantCulture),
            queuedSampleValues.ToString(CultureInfo.InvariantCulture),
            sourcePlaying ? "1" : "0",
            FormatFloat(inputRms),
            FormatFloat(inputPeak),
            FormatFloat(outputRms),
            FormatFloat(outputPeak)));

        FlushAudioDiagnosticsIfNeeded(writer);
    }

    private StreamWriter GetAudioDiagnosticsWriter()
    {
        if (!saveAudioDiagnosticsToFile)
        {
            return null;
        }

        if (audioDiagnosticsWriter != null)
        {
            return audioDiagnosticsWriter;
        }

        try
        {
            string directory = GetAudioDiagnosticsDirectory();
            Directory.CreateDirectory(directory);
            string filePath = Path.Combine(directory, "hl2ss_microphone_audio.csv");
            bool writeHeader = !File.Exists(filePath) || new FileInfo(filePath).Length == 0;
            audioDiagnosticsWriter = new StreamWriter(filePath, true);
            if (writeHeader)
            {
                audioDiagnosticsWriter.WriteLine("time,event,packetCount,regionSamples,queuedSampleValues,sourcePlaying,inputRms,inputPeak,outputRms,outputPeak");
            }

            Debug.Log("test_microphone audio diagnostics file: " + filePath);
            return audioDiagnosticsWriter;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("test_microphone: Could not open audio diagnostics file: " + e.Message);
            saveAudioDiagnosticsToFile = false;
            return null;
        }
    }

    private string GetAudioDiagnosticsDirectory()
    {
#if UNITY_EDITOR
        string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
#else
        string root = Application.persistentDataPath;
#endif
        string folderName = string.IsNullOrEmpty(audioDiagnosticsFolderName) ? "AudioDiagnostics" : audioDiagnosticsFolderName;
        return Path.Combine(root, folderName);
    }

    private void FlushAudioDiagnosticsIfNeeded(StreamWriter writer)
    {
        if (Time.unscaledTime < nextAudioDiagnosticsFlushTime)
        {
            return;
        }

        nextAudioDiagnosticsFlushTime = Time.unscaledTime + 1f;
        writer.Flush();
    }

    private void CloseAudioDiagnosticsWriter()
    {
        if (audioDiagnosticsWriter == null)
        {
            return;
        }

        try
        {
            audioDiagnosticsWriter.Flush();
            audioDiagnosticsWriter.Dispose();
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("test_microphone: Could not close audio diagnostics file cleanly: " + e.Message);
        }

        audioDiagnosticsWriter = null;
    }

    private static string FormatFloat(float value)
    {
        return value.ToString("F6", CultureInfo.InvariantCulture);
    }

    private static void MeasureAudioSourceOutput(AudioSource source, ref float[] buffer, out float rms, out float peak)
    {
        rms = 0f;
        peak = 0f;
        if (source == null || source.clip == null)
        {
            return;
        }

        if (buffer == null || buffer.Length != 256)
        {
            buffer = new float[256];
        }

        try
        {
            source.GetOutputData(buffer, 0);
            MeasureFloatSamples(buffer, buffer.Length, out rms, out peak);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("test_microphone: Could not read AudioSource output data: " + e.Message);
        }
    }

    private static void MeasureFloatSamples(float[] samples, int count, out float rms, out float peak)
    {
        rms = 0f;
        peak = 0f;
        if (samples == null || count <= 0)
        {
            return;
        }

        double sumSquares = 0.0;
        int safeCount = Mathf.Min(count, samples.Length);
        for (int i = 0; i < safeCount; i++)
        {
            float abs = Mathf.Abs(samples[i]);
            if (abs > peak)
            {
                peak = abs;
            }

            sumSquares += samples[i] * samples[i];
        }

        rms = Mathf.Sqrt((float)(sumSquares / safeCount));
    }

    private void OnValidate()
    {
        startBufferSeconds = Mathf.Max(0f, startBufferSeconds);
        playbackGain = Mathf.Max(0f, playbackGain);
        playbackLimiterCeiling = Mathf.Clamp(playbackLimiterCeiling, 0.1f, 1f);
    }

    private sealed class Pcm16WavWriter : System.IDisposable
    {
        private readonly FileStream stream;
        private readonly BinaryWriter writer;
        private long dataByteCount;
        private bool disposed;

        public Pcm16WavWriter(string filePath, int sampleRate, int channels)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
            stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            writer = new BinaryWriter(stream);
            WriteHeader(sampleRate, channels);
        }

        public void WriteFloatSamples(float[] samples, int count)
        {
            if (disposed || samples == null || count <= 0)
            {
                return;
            }

            int safeCount = Mathf.Min(count, samples.Length);
            for (int i = 0; i < safeCount; i++)
            {
                writer.Write(FloatToPcm16(samples[i]));
            }

            dataByteCount += safeCount * 2L;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            writer.Flush();
            stream.Seek(4, SeekOrigin.Begin);
            writer.Write(ToUInt32Saturated(36 + dataByteCount));
            stream.Seek(40, SeekOrigin.Begin);
            writer.Write(ToUInt32Saturated(dataByteCount));
            writer.Dispose();
            stream.Dispose();
        }

        private void WriteHeader(int sampleRate, int channels)
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write((uint)36);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            writer.Write((uint)16);
            writer.Write((ushort)1);
            writer.Write((ushort)channels);
            writer.Write((uint)sampleRate);
            writer.Write((uint)(sampleRate * channels * 2));
            writer.Write((ushort)(channels * 2));
            writer.Write((ushort)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write((uint)0);
        }

        private static short FloatToPcm16(float sample)
        {
            sample = Mathf.Clamp(sample, -1f, 1f);
            return sample < 0f
                ? (short)Mathf.RoundToInt(sample * 32768f)
                : (short)Mathf.RoundToInt(sample * 32767f);
        }

        private static uint ToUInt32Saturated(long value)
        {
            if (value <= 0)
            {
                return 0;
            }

            return value > uint.MaxValue ? uint.MaxValue : (uint)value;
        }
    }
}
