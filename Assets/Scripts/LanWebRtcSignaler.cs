using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.MixedReality.WebRTC;
using Microsoft.MixedReality.WebRTC.Unity;
using UnityEngine;

/// <summary>
/// Exchanges WebRTC SDP and ICE messages directly between two devices over UDP.
/// Media does not pass through this socket; after signaling, WebRTC carries audio peer-to-peer.
/// </summary>
[DisallowMultipleComponent]
public sealed class LanWebRtcSignaler : Signaler
{
    public bool isOfferer;
    public string remoteHost = "";
    public int localPort = 5077;
    public int remotePort = 5076;
    public float retryIntervalSeconds = 0.75f;
    public bool logStatus = true;
    public MediaTrackSource requiredLocalSource;

    private readonly ConcurrentQueue<ReceivedSignal> receivedSignals =
        new ConcurrentQueue<ReceivedSignal>();
    private readonly Queue<IceCandidate> pendingIceCandidates = new Queue<IceCandidate>();
    private readonly HashSet<string> processedMessageIds = new HashSet<string>();
    private readonly object remoteEndPointLock = new object();

    private UdpClient socket;
    private Thread receiveThread;
    private volatile bool receiving;
    private IPEndPoint remoteEndPoint;
    private byte[] cachedOffer;
    private byte[] cachedAnswer;
    private string sessionId;
    private int nextMessageSequence;
    private float nextRetryTime;
    private bool peerInitialized;
    private bool connectionStarted;
    private bool remoteDescriptionApplied;
    private bool processingSignal;
    private bool wasConnected;
    private bool loggedWaitingForLocalSource;

    public bool IsConnected => _nativePeer != null && _nativePeer.IsConnected;

    protected override void OnEnable()
    {
        base.OnEnable();
        PeerConnection.OnInitialized.AddListener(HandlePeerInitialized);
        PeerConnection.OnShutdown.AddListener(HandlePeerShutdown);
        StartSocket();
    }

    protected override void Update()
    {
        base.Update();

        if (!processingSignal && receivedSignals.TryDequeue(out ReceivedSignal received))
        {
            processingSignal = true;
            ProcessSignalAsync(received);
        }

        if (isOfferer && peerInitialized && !connectionStarted)
        {
            if (requiredLocalSource != null && !requiredLocalSource.IsLive)
            {
                if (!loggedWaitingForLocalSource && logStatus)
                {
                    loggedWaitingForLocalSource = true;
                    Debug.Log(
                        "LanWebRtcSignaler is waiting for the local microphone source before creating the offer.");
                }
            }
            else if (TryResolveRemoteEndPoint())
            {
                loggedWaitingForLocalSource = false;
                connectionStarted = true;
                sessionId = Guid.NewGuid().ToString("N");
                try
                {
                    PeerConnection.StartConnection();
                }
                catch (Exception e)
                {
                    connectionStarted = false;
                    Debug.LogWarning("LanWebRtcSignaler could not create the WebRTC offer: " + e.Message);
                }
            }
        }

        bool connected = IsConnected;
        if (connected && !wasConnected && logStatus)
        {
            Debug.Log("LanWebRtcSignaler WebRTC audio connection established.");
        }

        wasConnected = connected;
        if (!connected && Time.unscaledTime >= nextRetryTime)
        {
            nextRetryTime = Time.unscaledTime + Mathf.Max(0.1f, retryIntervalSeconds);
            if (isOfferer && cachedOffer != null)
            {
                SendDatagram(cachedOffer);
            }
            else if (!isOfferer && cachedAnswer != null)
            {
                SendDatagram(cachedAnswer);
            }
        }
    }

    protected override void OnDisable()
    {
        PeerConnection.OnInitialized.RemoveListener(HandlePeerInitialized);
        PeerConnection.OnShutdown.RemoveListener(HandlePeerShutdown);
        StopSocket();
        base.OnDisable();
    }

    public override Task SendMessageAsync(SdpMessage message)
    {
        SignalWireMessage wireMessage = new SignalWireMessage
        {
            protocol = SignalWireMessage.Protocol,
            session = sessionId,
            id = NextMessageId(),
            type = message.Type == SdpMessageType.Offer ? "offer" : "answer",
            content = message.Content
        };

        byte[] bytes = Encode(wireMessage);
        if (message.Type == SdpMessageType.Offer)
        {
            cachedOffer = bytes;
        }
        else
        {
            cachedAnswer = bytes;
        }

        SendDatagram(bytes);
        return Task.CompletedTask;
    }

    public override Task SendMessageAsync(IceCandidate candidate)
    {
        SignalWireMessage wireMessage = new SignalWireMessage
        {
            protocol = SignalWireMessage.Protocol,
            session = sessionId,
            id = NextMessageId(),
            type = "ice",
            content = candidate.Content,
            sdpMid = candidate.SdpMid,
            sdpMlineIndex = candidate.SdpMlineIndex
        };

        byte[] bytes = Encode(wireMessage);
        SendDatagram(bytes);
        SendDatagram(bytes);
        return Task.CompletedTask;
    }

    private void HandlePeerInitialized()
    {
        peerInitialized = true;
    }

    private void HandlePeerShutdown()
    {
        peerInitialized = false;
        connectionStarted = false;
        remoteDescriptionApplied = false;
        cachedOffer = null;
        cachedAnswer = null;
        pendingIceCandidates.Clear();
        processedMessageIds.Clear();
    }

    private void StartSocket()
    {
        if (socket != null)
        {
            return;
        }

        try
        {
            socket = new UdpClient(new IPEndPoint(IPAddress.Any, Mathf.Clamp(localPort, 1, 65535)));
            receiving = true;
            receiveThread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = "MedXR WebRTC Signaling"
            };
            receiveThread.Start();

            if (logStatus)
            {
                Debug.Log("LanWebRtcSignaler listening on UDP port " + localPort + ".");
            }
        }
        catch (Exception e)
        {
            Debug.LogError("LanWebRtcSignaler could not open UDP port " + localPort + ": " + e.Message);
            StopSocket();
        }
    }

    private void StopSocket()
    {
        receiving = false;
        if (socket != null)
        {
            try
            {
                socket.Close();
            }
            catch
            {
            }

            socket = null;
        }

        receiveThread = null;
        while (receivedSignals.TryDequeue(out _))
        {
        }
    }

    private void ReceiveLoop()
    {
        IPEndPoint sender = new IPEndPoint(IPAddress.Any, 0);
        while (receiving)
        {
            try
            {
                byte[] bytes = socket.Receive(ref sender);
                if (bytes == null || bytes.Length == 0)
                {
                    continue;
                }

                receivedSignals.Enqueue(new ReceivedSignal
                {
                    json = Encoding.UTF8.GetString(bytes),
                    sender = new IPEndPoint(sender.Address, sender.Port)
                });
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception e)
            {
                if (receiving)
                {
                    Debug.LogWarning("LanWebRtcSignaler receive failed: " + e.Message);
                }
            }
        }
    }

    private async void ProcessSignalAsync(ReceivedSignal received)
    {
        try
        {
            SignalWireMessage message = JsonUtility.FromJson<SignalWireMessage>(received.json);
            if (message == null ||
                !string.Equals(message.protocol, SignalWireMessage.Protocol, StringComparison.Ordinal))
            {
                return;
            }

            lock (remoteEndPointLock)
            {
                if (remoteEndPoint == null || !isOfferer)
                {
                    remoteEndPoint = received.sender;
                }
            }

            if (string.Equals(message.type, "offer", StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(message.session))
                {
                    sessionId = message.session;
                }

                if (!processedMessageIds.Add(message.id))
                {
                    if (cachedAnswer != null)
                    {
                        SendDatagram(cachedAnswer);
                    }

                    return;
                }

                SdpMessage offer = new SdpMessage
                {
                    Type = SdpMessageType.Offer,
                    Content = message.content
                };
                await PeerConnection.HandleConnectionMessageAsync(offer);
                remoteDescriptionApplied = true;
                FlushPendingIceCandidates();
                _nativePeer.CreateAnswer();
                return;
            }

            if (string.Equals(message.type, "answer", StringComparison.Ordinal))
            {
                if (!processedMessageIds.Add(message.id))
                {
                    return;
                }

                SdpMessage answer = new SdpMessage
                {
                    Type = SdpMessageType.Answer,
                    Content = message.content
                };
                await PeerConnection.HandleConnectionMessageAsync(answer);
                remoteDescriptionApplied = true;
                FlushPendingIceCandidates();
                cachedOffer = null;
                return;
            }

            if (string.Equals(message.type, "ice", StringComparison.Ordinal))
            {
                if (!processedMessageIds.Add(message.id))
                {
                    return;
                }

                IceCandidate candidate = new IceCandidate
                {
                    Content = message.content,
                    SdpMid = message.sdpMid,
                    SdpMlineIndex = message.sdpMlineIndex
                };
                if (remoteDescriptionApplied && _nativePeer != null)
                {
                    _nativePeer.AddIceCandidate(candidate);
                }
                else
                {
                    pendingIceCandidates.Enqueue(candidate);
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("LanWebRtcSignaler could not process a signaling message: " + e.Message);
        }
        finally
        {
            processingSignal = false;
        }
    }

    private void FlushPendingIceCandidates()
    {
        if (_nativePeer == null)
        {
            return;
        }

        while (pendingIceCandidates.Count > 0)
        {
            _nativePeer.AddIceCandidate(pendingIceCandidates.Dequeue());
        }
    }

    private bool TryResolveRemoteEndPoint()
    {
        lock (remoteEndPointLock)
        {
            if (remoteEndPoint != null)
            {
                return true;
            }
        }

        string host = string.IsNullOrWhiteSpace(remoteHost) ? null : remoteHost.Trim();
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        try
        {
            IPAddress address;
            if (!IPAddress.TryParse(host, out address))
            {
                IPAddress[] addresses = Dns.GetHostAddresses(host);
                address = Array.Find(addresses, item => item.AddressFamily == AddressFamily.InterNetwork);
            }

            if (address == null)
            {
                return false;
            }

            lock (remoteEndPointLock)
            {
                remoteEndPoint = new IPEndPoint(address, Mathf.Clamp(remotePort, 1, 65535));
            }

            return true;
        }
        catch (Exception e)
        {
            if (logStatus)
            {
                Debug.LogWarning("LanWebRtcSignaler could not resolve remote host '" + host + "': " + e.Message);
            }

            return false;
        }
    }

    private void SendDatagram(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0 || socket == null)
        {
            return;
        }

        IPEndPoint target;
        lock (remoteEndPointLock)
        {
            target = remoteEndPoint;
        }

        if (target == null && !TryResolveRemoteEndPoint())
        {
            return;
        }

        lock (remoteEndPointLock)
        {
            target = remoteEndPoint;
        }

        try
        {
            socket.Send(bytes, bytes.Length, target);
        }
        catch (Exception e)
        {
            if (logStatus)
            {
                Debug.LogWarning("LanWebRtcSignaler send failed: " + e.Message);
            }
        }
    }

    private string NextMessageId()
    {
        nextMessageSequence++;
        return sessionId + "-" + nextMessageSequence;
    }

    private static byte[] Encode(SignalWireMessage message)
    {
        return Encoding.UTF8.GetBytes(JsonUtility.ToJson(message));
    }

    private sealed class ReceivedSignal
    {
        public string json;
        public IPEndPoint sender;
    }

    [Serializable]
    private sealed class SignalWireMessage
    {
        public const string Protocol = "MedXR-WebRTC-Audio-v1";

        public string protocol;
        public string session;
        public string id;
        public string type;
        public string content;
        public string sdpMid;
        public int sdpMlineIndex;
    }
}
