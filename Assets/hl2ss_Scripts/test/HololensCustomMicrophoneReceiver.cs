using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class HololensCustomMicrophoneReceiver : MonoBehaviour
{
    private const uint PacketMagic = 0x434D584D; // MXMC
    private const ushort ProtocolVersion = 1;
    private const int HeaderByteCount = 32;

    [Header("Connection")]
    public string host;
    public int port = 5066;
    public int connectionTimeoutMs = 1000;
    public bool connectOnStart = true;
    public bool logStatus = true;

    [Header("Playback")]
    public GameObject audio_source_object;
    public float startBufferSeconds = 0.25f;
    public int clipBufferSeconds = 4;
    public int maxBufferSeconds = 3;
    [Tooltip("Gain applied to samples received on the custom HoloLens microphone port.")]
    public float playbackGain = 1f;
    [Range(0.1f, 1f)]
    public float playbackLimiterCeiling = 0.95f;

    private readonly object bufferLock = new object();
    private readonly List<float> sampleBuffer = new List<float>();

    private Thread receiverThread;
    private TcpClient client;
    private volatile bool receiverRunning;
    private volatile bool connected;
    private volatile string pendingLog;
    private AudioSource audioSource;
    private int receivedSampleRate = 48000;
    private int receivedChannels = 1;
    private int formatRevision;
    private int appliedFormatRevision = -1;
    private int startBufferSampleValues;
    private float nextStatusLogTime;
    private int packetCount;
    private uint lastSequence;
    private float lastInputRms;
    private float lastInputPeak;

    private void Start()
    {
        audioSource = EnsureAudioSource();
        if (connectOnStart)
        {
            StartReceiver();
        }
    }

    private void Update()
    {
        LogPendingMessage();
        EnsurePlaybackClip();

        if (audioSource != null && !audioSource.isPlaying && GetQueuedSampleValueCount() >= startBufferSampleValues)
        {
            audioSource.Play();
        }

        if (logStatus && Time.unscaledTime >= nextStatusLogTime)
        {
            nextStatusLogTime = Time.unscaledTime + 2f;
            Debug.Log("HololensCustomMicrophoneReceiver host=" + ResolveHost() +
                      ", port=" + port +
                      ", connected=" + connected +
                      ", packets=" + packetCount +
                      ", queuedSampleValues=" + GetQueuedSampleValueCount() +
                      ", sourcePlaying=" + (audioSource != null && audioSource.isPlaying) +
                      ", inputRms=" + lastInputRms.ToString("F4") +
                      ", inputPeak=" + lastInputPeak.ToString("F4"));
        }
    }

    private void OnDisable()
    {
        StopReceiver();
        StopPlayback();
    }

    private void OnDestroy()
    {
        StopReceiver();
        StopPlayback();
    }

    private void OnAudioRead(float[] data)
    {
        int count = 0;

        lock (bufferLock)
        {
            while (count < data.Length && count < sampleBuffer.Count)
            {
                data[count] = sampleBuffer[count];
                count++;
            }

            if (count > 0)
            {
                sampleBuffer.RemoveRange(0, count);
            }
        }

        while (count < data.Length)
        {
            data[count++] = 0f;
        }
    }

    public void StartReceiver()
    {
        if (receiverRunning)
        {
            return;
        }

        receiverRunning = true;
        receiverThread = new Thread(ReceiverLoop)
        {
            IsBackground = true,
            Name = "HololensCustomMicrophoneReceiver"
        };
        receiverThread.Start();
    }

    public void StopReceiver()
    {
        receiverRunning = false;
        connected = false;

        try
        {
            client?.Close();
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        if (receiverThread != null && receiverThread.IsAlive)
        {
            receiverThread.Join(500);
        }

        receiverThread = null;
        client = null;

        lock (bufferLock)
        {
            sampleBuffer.Clear();
        }
    }

    private void ReceiverLoop()
    {
        while (receiverRunning)
        {
            string targetHost = ResolveHost();
            if (string.IsNullOrEmpty(targetHost))
            {
                pendingLog = "HololensCustomMicrophoneReceiver waiting for a host address.";
                Thread.Sleep(1000);
                continue;
            }

            try
            {
                using (TcpClient tcpClient = Connect(targetHost, port, connectionTimeoutMs))
                using (NetworkStream stream = tcpClient.GetStream())
                {
                    client = tcpClient;
                    connected = true;
                    pendingLog = "HololensCustomMicrophoneReceiver connected to " + targetHost + ":" + port;

                    while (receiverRunning && tcpClient.Connected)
                    {
                        ReadAndQueuePacket(stream);
                    }
                }
            }
            catch (Exception e)
            {
                if (receiverRunning)
                {
                    pendingLog = "HololensCustomMicrophoneReceiver reconnecting after: " + e.Message;
                    Thread.Sleep(1000);
                }
            }
            finally
            {
                connected = false;
                client = null;
            }
        }
    }

    private static TcpClient Connect(string targetHost, int targetPort, int timeoutMs)
    {
        TcpClient tcpClient = new TcpClient();
        IAsyncResult result = tcpClient.BeginConnect(targetHost, targetPort, null, null);
        bool connected = result.AsyncWaitHandle.WaitOne(Math.Max(100, timeoutMs));
        if (!connected)
        {
            tcpClient.Close();
            throw new TimeoutException("connection timeout");
        }

        tcpClient.EndConnect(result);
        tcpClient.NoDelay = true;
        tcpClient.ReceiveBufferSize = 64 * 1024;
        return tcpClient;
    }

    private void ReadAndQueuePacket(NetworkStream stream)
    {
        byte[] header = ReadExact(stream, HeaderByteCount);
        uint magic = BitConverter.ToUInt32(header, 0);
        ushort version = BitConverter.ToUInt16(header, 4);
        ushort channels = BitConverter.ToUInt16(header, 6);
        int sampleRate = BitConverter.ToInt32(header, 8);
        uint sequence = BitConverter.ToUInt32(header, 12);
        int frameCount = BitConverter.ToInt32(header, 16);
        int payloadByteCount = BitConverter.ToInt32(header, 20);

        if (magic != PacketMagic)
        {
            throw new InvalidOperationException("invalid microphone packet magic");
        }

        if (version != ProtocolVersion)
        {
            throw new InvalidOperationException("unsupported microphone packet version " + version);
        }

        if (sampleRate < 8000 || sampleRate > 96000 || channels < 1 || channels > 8 || frameCount <= 0 || payloadByteCount <= 0 || payloadByteCount > 1024 * 1024)
        {
            throw new InvalidOperationException("invalid microphone packet format");
        }

        byte[] payload = ReadExact(stream, payloadByteCount);
        float[] decoded = DecodePcm16(payload, payloadByteCount, playbackGain, playbackLimiterCeiling);
        MeasureSamples(decoded, decoded.Length, out lastInputRms, out lastInputPeak);
        UpdateFormatIfNeeded(sampleRate, channels);

        lock (bufferLock)
        {
            sampleBuffer.AddRange(decoded);
            int maxSampleValues = Mathf.Max(sampleRate * channels, sampleRate * channels * Mathf.Max(1, maxBufferSeconds));
            if (sampleBuffer.Count > maxSampleValues)
            {
                sampleBuffer.RemoveRange(0, sampleBuffer.Count - maxSampleValues);
            }
        }

        packetCount++;
        lastSequence = sequence;
    }

    private void UpdateFormatIfNeeded(int sampleRate, int channels)
    {
        if (sampleRate == receivedSampleRate && channels == receivedChannels)
        {
            return;
        }

        lock (bufferLock)
        {
            sampleBuffer.Clear();
            receivedSampleRate = sampleRate;
            receivedChannels = channels;
            formatRevision++;
        }
    }

    private static byte[] ReadExact(NetworkStream stream, int byteCount)
    {
        byte[] data = new byte[byteCount];
        int offset = 0;
        while (offset < byteCount)
        {
            int read = stream.Read(data, offset, byteCount - offset);
            if (read <= 0)
            {
                throw new InvalidOperationException("stream closed");
            }

            offset += read;
        }

        return data;
    }

    private void EnsurePlaybackClip()
    {
        if (audioSource == null)
        {
            audioSource = EnsureAudioSource();
        }

        if (audioSource == null || appliedFormatRevision == formatRevision)
        {
            return;
        }

        if (audioSource.isPlaying)
        {
            audioSource.Stop();
        }

        int sampleRate = Mathf.Clamp(receivedSampleRate, 8000, 96000);
        int channels = Mathf.Clamp(receivedChannels, 1, 8);
        int clipSampleCount = sampleRate * Mathf.Clamp(clipBufferSeconds, 1, 10);
        audioSource.clip = AudioClip.Create("custom_hololens_microphone", clipSampleCount, channels, sampleRate, true, OnAudioRead);
        ConfigureAudioSource(audioSource);
        startBufferSampleValues = Mathf.Max(1, Mathf.RoundToInt(sampleRate * channels * Mathf.Max(0f, startBufferSeconds)));
        appliedFormatRevision = formatRevision;
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

    private void StopPlayback()
    {
        if (audioSource != null && audioSource.isPlaying)
        {
            audioSource.Stop();
        }
    }

    private int GetQueuedSampleValueCount()
    {
        lock (bufferLock)
        {
            return sampleBuffer.Count;
        }
    }

    private string ResolveHost()
    {
        string explicitHost = TrimToNull(host);
        if (!string.IsNullOrEmpty(explicitHost))
        {
            return explicitHost;
        }

        return TrimToNull(run_once.host_address);
    }

    private void LogPendingMessage()
    {
        string message = pendingLog;
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        pendingLog = null;
        Debug.Log(message);
    }

    private static float[] DecodePcm16(byte[] payload, int payloadByteCount, float gain, float limiterCeiling)
    {
        int sampleValueCount = payloadByteCount / 2;
        float[] samples = new float[sampleValueCount];
        gain = Mathf.Max(0f, gain);

        for (int i = 0; i < sampleValueCount; i++)
        {
            int byteIndex = i * 2;
            short pcm = (short)(payload[byteIndex] | (payload[byteIndex + 1] << 8));
            float sample = pcm < 0 ? pcm / 32768f : pcm / 32767f;
            samples[i] = ApplySoftLimiter(sample * gain, limiterCeiling);
        }

        return samples;
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

    private static void MeasureSamples(float[] samples, int sampleValueCount, out float rms, out float peak)
    {
        double sumSquares = 0.0;
        peak = 0f;

        for (int i = 0; i < sampleValueCount; i++)
        {
            float sample = samples[i];
            sumSquares += sample * sample;
            float abs = Mathf.Abs(sample);
            if (abs > peak)
            {
                peak = abs;
            }
        }

        rms = sampleValueCount > 0 ? Mathf.Sqrt((float)(sumSquares / sampleValueCount)) : 0f;
    }

    private static string TrimToNull(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private void OnValidate()
    {
        port = Mathf.Clamp(port, 1, 65535);
        connectionTimeoutMs = Mathf.Clamp(connectionTimeoutMs, 100, 10000);
        startBufferSeconds = Mathf.Max(0f, startBufferSeconds);
        clipBufferSeconds = Mathf.Clamp(clipBufferSeconds, 1, 10);
        maxBufferSeconds = Mathf.Clamp(maxBufferSeconds, 1, 30);
        playbackGain = Mathf.Max(0f, playbackGain);
        playbackLimiterCeiling = Mathf.Clamp(playbackLimiterCeiling, 0.1f, 1f);
    }
}
