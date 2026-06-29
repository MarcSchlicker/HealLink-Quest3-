using System.Collections;
using Microsoft.MixedReality.WebRTC;
using Microsoft.MixedReality.WebRTC.Unity;
using UnityEngine;
using WebRtcAudioRenderer = Microsoft.MixedReality.WebRTC.Unity.AudioRenderer;
using UnityPeerConnection = Microsoft.MixedReality.WebRTC.Unity.PeerConnection;

/// <summary>
/// Creates one bidirectional, audio-only WebRTC peer at runtime.
/// </summary>
[DisallowMultipleComponent]
public sealed class WebRtcAudioPeer : MonoBehaviour
{
    public bool startOnEnable;
    public bool isOfferer;
    public string remoteHost = "";
    public int localSignalingPort = 5077;
    public int remoteSignalingPort = 5076;
    public float startupDelaySeconds = 0.5f;
    public float outputVolume = 1f;
    public bool logStatus = true;
    public bool logAudioStats = true;
    public float audioStatsIntervalSeconds = 2f;

    private GameObject runtimeRoot;
    private UnityPeerConnection peerConnection;
    private MicrophoneSource microphoneSource;
    private AudioReceiver audioReceiver;
    private WebRtcAudioRenderer audioRenderer;
    private AudioSource outputSource;
    private AudioClip outputCarrierClip;
    private LanWebRtcSignaler signaler;
    private Coroutine startupCoroutine;
    private bool started;
    private bool audioStatsRequestPending;
    private float nextAudioStatsTime;
    private ulong lastAudioBytesSent;
    private uint lastAudioPacketsSent;

    public bool IsConnected => signaler != null && signaler.IsConnected;

    private void OnEnable()
    {
        if (startOnEnable)
        {
            StartPeer();
        }
    }

    private void OnDisable()
    {
        StopPeer();
    }

    private void OnDestroy()
    {
        StopPeer();
    }

    private void Update()
    {
        if (!logAudioStats ||
            audioStatsRequestPending ||
            !IsConnected ||
            peerConnection == null ||
            peerConnection.Peer == null ||
            Time.unscaledTime < nextAudioStatsTime)
        {
            return;
        }

        nextAudioStatsTime =
            Time.unscaledTime + Mathf.Max(0.5f, audioStatsIntervalSeconds);
        LogAudioStatsAsync();
    }

    public void ConfigureAndStart(
        bool offerer,
        string targetHost,
        int localPort,
        int targetPort)
    {
        isOfferer = offerer;
        remoteHost = targetHost;
        localSignalingPort = localPort;
        remoteSignalingPort = targetPort;
        StartPeer();
    }

    public void StartPeer()
    {
        if (started || startupCoroutine != null)
        {
            return;
        }

        startupCoroutine = StartCoroutine(StartPeerAfterDelay());
    }

    public void StopPeer()
    {
        if (startupCoroutine != null)
        {
            StopCoroutine(startupCoroutine);
            startupCoroutine = null;
        }

        started = false;
        if (outputSource != null)
        {
            outputSource.Stop();
        }

        if (runtimeRoot != null)
        {
            Destroy(runtimeRoot);
            runtimeRoot = null;
        }

        if (outputCarrierClip != null)
        {
            Destroy(outputCarrierClip);
            outputCarrierClip = null;
        }

        peerConnection = null;
        microphoneSource = null;
        audioReceiver = null;
        audioRenderer = null;
        outputSource = null;
        signaler = null;
        audioStatsRequestPending = false;
        nextAudioStatsTime = 0f;
        lastAudioBytesSent = 0;
        lastAudioPacketsSent = 0;
    }

    private IEnumerator StartPeerAfterDelay()
    {
        float delay = Mathf.Max(0f, startupDelaySeconds);
        if (delay > 0f)
        {
            yield return new WaitForSecondsRealtime(delay);
        }

        startupCoroutine = null;
        BuildRuntimePeer();
    }

    private void BuildRuntimePeer()
    {
        if (started)
        {
            return;
        }

#if UNITY_WSA && !UNITY_EDITOR
        if (System.IntPtr.Size != 4)
        {
            Debug.LogError(
                "WebRtcAudioPeer cannot start in an ARM64 UWP build. " +
                "MixedReality-WebRTC 2.0.2 provides its HoloLens native plugin for ARM (32-bit). " +
                "Rebuild the HoloLens application with UWP Architecture set to ARM.");
            return;
        }
#endif

        runtimeRoot = new GameObject(isOfferer
            ? "QuestWebRtcAudioPeer"
            : "HoloLensWebRtcAudioPeer");
        runtimeRoot.transform.SetParent(transform, false);
        runtimeRoot.SetActive(false);

        peerConnection = runtimeRoot.AddComponent<UnityPeerConnection>();
        peerConnection.AutoCreateOfferOnRenegotiationNeeded = true;
        peerConnection.AutoLogErrorsToUnityConsole = true;
        peerConnection.IceServers.Clear();

        microphoneSource = runtimeRoot.AddComponent<MicrophoneSource>();
        audioReceiver = runtimeRoot.AddComponent<AudioReceiver>();
        outputSource = runtimeRoot.AddComponent<AudioSource>();
        ConfigureOutputSource(outputSource);
        audioRenderer = runtimeRoot.AddComponent<WebRtcAudioRenderer>();
        audioRenderer.PadWithSine = false;

        MediaLine audioLine = peerConnection.AddMediaLine(MediaKind.Audio);
        audioLine.Source = microphoneSource;
        audioLine.Receiver = audioReceiver;
        audioLine.SenderTrackName = isOfferer ? "quest_audio" : "hololens_audio";

        audioReceiver.AudioStreamStarted.AddListener(HandleRemoteAudioStarted);
        audioReceiver.AudioStreamStopped.AddListener(HandleRemoteAudioStopped);

        signaler = runtimeRoot.AddComponent<LanWebRtcSignaler>();
        signaler.PeerConnection = peerConnection;
        signaler.isOfferer = isOfferer;
        signaler.remoteHost = remoteHost;
        signaler.localPort = localSignalingPort;
        signaler.remotePort = remoteSignalingPort;
        signaler.logStatus = logStatus;
        signaler.requiredLocalSource = microphoneSource;

        EnsureAudioListener();
        runtimeRoot.SetActive(true);
        started = true;

        if (logStatus)
        {
            Debug.Log("WebRtcAudioPeer started bidirectional audio, role=" +
                      (isOfferer ? "offerer" : "answerer") +
                      ", localSignalingPort=" + localSignalingPort +
                      ", remoteHost=" + (string.IsNullOrEmpty(remoteHost) ? "(learn from offer)" : remoteHost) +
                      ", remoteSignalingPort=" + remoteSignalingPort + ".");
        }
    }

    private void HandleRemoteAudioStarted(IAudioSource source)
    {
        if (audioRenderer == null || outputSource == null)
        {
            return;
        }

        audioRenderer.StartRendering(source);
        EnsureOutputCarrierClip();
        outputSource.Play();

        if (logStatus)
        {
            Debug.Log("WebRtcAudioPeer remote audio is now routed through the device AudioSource.");
        }
    }

    private void HandleRemoteAudioStopped(IAudioSource source)
    {
        if (outputSource != null)
        {
            outputSource.Stop();
        }

        if (audioRenderer != null)
        {
            audioRenderer.StopRendering(source);
        }
    }

    private void EnsureOutputCarrierClip()
    {
        if (outputCarrierClip == null)
        {
            int sampleRate = Mathf.Max(8000, AudioSettings.outputSampleRate);
            outputCarrierClip = AudioClip.Create(
                "WebRtcRemoteAudioCarrier",
                sampleRate,
                1,
                sampleRate,
                true,
                FillCarrierAudio);
        }

        outputSource.clip = outputCarrierClip;
        outputSource.loop = true;
    }

    private static void FillCarrierAudio(float[] samples)
    {
        if (samples != null)
        {
            System.Array.Clear(samples, 0, samples.Length);
        }
    }

    private void ConfigureOutputSource(AudioSource source)
    {
        source.playOnAwake = false;
        source.loop = true;
        source.volume = Mathf.Max(0f, outputVolume);
        source.spatialBlend = 0f;
        source.spatialize = false;
        source.dopplerLevel = 0f;
        source.panStereo = 0f;
        source.bypassEffects = false;
        source.bypassListenerEffects = false;
        source.bypassReverbZones = true;
        source.ignoreListenerPause = true;
    }

    private void EnsureAudioListener()
    {
        AudioListener.pause = false;
        AudioListener.volume = 1f;
        if (FindObjectOfType<AudioListener>() != null)
        {
            return;
        }

        Camera mainCamera = Camera.main;
        GameObject listenerObject = mainCamera != null ? mainCamera.gameObject : gameObject;
        listenerObject.AddComponent<AudioListener>();
        Debug.LogWarning("WebRtcAudioPeer added a missing AudioListener for device playback.");
    }

    private async void LogAudioStatsAsync()
    {
        audioStatsRequestPending = true;
        try
        {
            using (Microsoft.MixedReality.WebRTC.PeerConnection.StatsReport report =
                   await peerConnection.Peer.GetSimpleStatsAsync())
            {
                foreach (Microsoft.MixedReality.WebRTC.PeerConnection.AudioSenderStats stats in
                         report.GetStats<Microsoft.MixedReality.WebRTC.PeerConnection.AudioSenderStats>())
                {
                    ulong byteDelta = stats.BytesSent >= lastAudioBytesSent
                        ? stats.BytesSent - lastAudioBytesSent
                        : stats.BytesSent;
                    uint packetDelta = stats.PacketsSent >= lastAudioPacketsSent
                        ? stats.PacketsSent - lastAudioPacketsSent
                        : stats.PacketsSent;

                    lastAudioBytesSent = stats.BytesSent;
                    lastAudioPacketsSent = stats.PacketsSent;

                    Debug.Log(
                        "WebRtcAudioPeer outgoing audio stats: track=" +
                        stats.TrackIdentifier +
                        ", audioLevel=" +
                        stats.AudioLevel.ToString("F4") +
                        ", totalAudioEnergy=" +
                        stats.TotalAudioEnergy.ToString("F4") +
                        ", packetsSent=" +
                        stats.PacketsSent +
                        " (+" +
                        packetDelta +
                        "), bytesSent=" +
                        stats.BytesSent +
                        " (+" +
                        byteDelta +
                        ").");
                }
            }
        }
        catch (System.Exception exception)
        {
            if (logStatus)
            {
                Debug.LogWarning(
                    "WebRtcAudioPeer could not read outgoing audio stats: " +
                    exception.Message);
            }
        }
        finally
        {
            audioStatsRequestPending = false;
        }
    }
}
