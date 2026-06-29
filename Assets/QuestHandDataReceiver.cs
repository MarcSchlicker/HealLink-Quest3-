using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class QuestHandDataReceiver : MonoBehaviour
{
    [Header("Network")]
    public int listenPort = 5055;
    public bool startReceiverOnEnable = true;

    [Header("MRTK hand rigs")]
    public Transform leftHandRoot;
    public Transform rightHandRoot;
    public bool autoFindHandRoots = true;
    public bool applyWorldPose = true;
    public bool hideWhenNotTracked = true;

    [Header("Remote hand look")]
    public bool applyRemoteHandMaterial = true;
    public Color remoteHandColor = new Color(0.1f, 0.65f, 1f, 0.38f);

    private readonly object packetLock = new object();
    private readonly Dictionary<string, Transform> leftBones = new Dictionary<string, Transform>();
    private readonly Dictionary<string, Transform> rightBones = new Dictionary<string, Transform>();
    private UdpClient udpClient;
    private Thread receiveThread;
    private volatile bool running;
    private string latestPacketJson;
    private Material remoteHandMaterial;

    private void OnEnable()
    {
        if (autoFindHandRoots)
        {
            leftHandRoot = leftHandRoot != null ? leftHandRoot : FindTransformByName("L_Hand_MRTK_Rig");
            rightHandRoot = rightHandRoot != null ? rightHandRoot : FindTransformByName("R_Hand_MRTK_Rig");
        }

        RebuildBoneMaps();
        ApplyRemoteHandStyle();

        if (startReceiverOnEnable)
        {
            StartReceiver();
        }
    }

    private void OnDisable()
    {
        StopReceiver();
    }

    private void OnDestroy()
    {
        if (remoteHandMaterial != null)
        {
            Destroy(remoteHandMaterial);
            remoteHandMaterial = null;
        }
    }

    private void Update()
    {
        string json = null;
        lock (packetLock)
        {
            if (!string.IsNullOrEmpty(latestPacketJson))
            {
                json = latestPacketJson;
                latestPacketJson = null;
            }
        }

        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        HandFramePacket packet;
        try
        {
            packet = JsonUtility.FromJson<HandFramePacket>(json);
        }
        catch (Exception e)
        {
            Debug.LogWarning("QuestHandDataReceiver could not parse packet: " + e.Message);
            return;
        }

        if (packet == null || packet.hands == null)
        {
            return;
        }

        for (int i = 0; i < packet.hands.Length; i++)
        {
            ApplyHand(packet.hands[i]);
        }
    }

    public void StartReceiver()
    {
        if (running)
        {
            return;
        }

        try
        {
            udpClient = new UdpClient(listenPort);
            running = true;
            receiveThread = new Thread(ReceiveLoop);
            receiveThread.IsBackground = true;
            receiveThread.Start();
        }
        catch (Exception e)
        {
            running = false;
            Debug.LogError("QuestHandDataReceiver could not listen on UDP port " + listenPort + ": " + e.Message);
        }
    }

    public void StopReceiver()
    {
        running = false;

        if (udpClient != null)
        {
            udpClient.Close();
            udpClient = null;
        }

        receiveThread = null;
    }

    private void ReceiveLoop()
    {
        IPEndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (running)
        {
            try
            {
                byte[] bytes = udpClient.Receive(ref any);
                string json = Encoding.UTF8.GetString(bytes);
                lock (packetLock)
                {
                    latestPacketJson = json;
                }
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception e)
            {
                Debug.LogWarning("QuestHandDataReceiver receive error: " + e.Message);
            }
        }
    }

    private void ApplyHand(HandFrame hand)
    {
        if (hand == null)
        {
            return;
        }

        bool isLeft = string.Equals(hand.handedness, "Left", StringComparison.OrdinalIgnoreCase);
        Transform root = isLeft ? leftHandRoot : rightHandRoot;
        Dictionary<string, Transform> bones = isLeft ? leftBones : rightBones;
        if (root == null)
        {
            return;
        }

        if (hideWhenNotTracked)
        {
            root.gameObject.SetActive(hand.isTracked);
        }

        if (!hand.isTracked)
        {
            return;
        }

        if (hand.wrist.isValid)
        {
            ApplyPose(root, hand.wrist);
        }

        if (hand.joints == null)
        {
            return;
        }

        for (int i = 0; i < hand.joints.Length; i++)
        {
            JointPose joint = hand.joints[i];
            string key = NormalizeJointName(joint.name, isLeft);
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            Transform bone;
            if (bones.TryGetValue(key, out bone) && bone != null)
            {
                ApplyPose(bone, joint.pose);
            }
        }
    }

    private void ApplyPose(Transform target, PoseData pose)
    {
        if (!pose.isValid)
        {
            return;
        }

        if (applyWorldPose)
        {
            target.position = pose.position;
            target.rotation = pose.rotation;
        }
        else
        {
            target.localPosition = pose.position;
            target.localRotation = pose.rotation;
        }
    }

    private void ApplyRemoteHandStyle()
    {
        if (!applyRemoteHandMaterial)
        {
            return;
        }

        if (remoteHandMaterial != null)
        {
            Destroy(remoteHandMaterial);
        }

        remoteHandMaterial = CreateRemoteHandMaterial(remoteHandColor);
        ApplyMaterialToHand(leftHandRoot, remoteHandMaterial);
        ApplyMaterialToHand(rightHandRoot, remoteHandMaterial);
    }

    private static void ApplyMaterialToHand(Transform root, Material material)
    {
        if (root == null || material == null)
        {
            return;
        }

        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] == null)
            {
                continue;
            }

            renderers[i].sharedMaterial = material;
            renderers[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderers[i].receiveShadows = false;
        }
    }

    private static Material CreateRemoteHandMaterial(Color color)
    {
        Shader shader = FindRemoteHandShader();
        if (shader == null)
        {
            return null;
        }

        Material material = new Material(shader);
        ConfigureTransparentMaterial(material, color);
        return material;
    }

    private static Shader FindRemoteHandShader()
    {
        string[] shaderNames =
        {
            "Standard",
            "Mixed Reality Toolkit/Standard",
            "Universal Render Pipeline/Unlit",
            "Unlit/Color"
        };

        for (int i = 0; i < shaderNames.Length; i++)
        {
            Shader shader = Shader.Find(shaderNames[i]);
            if (shader != null)
            {
                return shader;
            }
        }

        return null;
    }

    private static void ConfigureTransparentMaterial(Material material, Color color)
    {
        if (material == null)
        {
            return;
        }

        if (material.HasProperty("_BaseColor"))
        {
            material.SetColor("_BaseColor", color);
        }

        if (material.HasProperty("_Color"))
        {
            material.SetColor("_Color", color);
        }

        if (material.HasProperty("_Mode"))
        {
            material.SetFloat("_Mode", 3f);
        }

        if (material.HasProperty("_Surface"))
        {
            material.SetFloat("_Surface", 1f);
        }

        if (material.HasProperty("_SrcBlend"))
        {
            material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        }

        if (material.HasProperty("_DstBlend"))
        {
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        }

        if (material.HasProperty("_ZWrite"))
        {
            material.SetInt("_ZWrite", 0);
        }

        material.DisableKeyword("_ALPHATEST_ON");
        material.EnableKeyword("_ALPHABLEND_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
    }

    private void RebuildBoneMaps()
    {
        leftBones.Clear();
        rightBones.Clear();
        BuildBoneMap(leftHandRoot, true, leftBones);
        BuildBoneMap(rightHandRoot, false, rightBones);
    }

    private static void BuildBoneMap(Transform root, bool isLeft, Dictionary<string, Transform> map)
    {
        if (root == null)
        {
            return;
        }

        Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < transforms.Length; i++)
        {
            string key = NormalizeJointName(transforms[i].name, isLeft);
            if (!string.IsNullOrEmpty(key) && !map.ContainsKey(key))
            {
                map.Add(key, transforms[i]);
            }
        }
    }

    private static Transform FindTransformByName(string targetName)
    {
        Transform[] transforms = Resources.FindObjectsOfTypeAll<Transform>();
        for (int i = 0; i < transforms.Length; i++)
        {
            if (transforms[i] != null && transforms[i].name == targetName)
            {
                return transforms[i];
            }
        }

        return null;
    }

    private static string NormalizeJointName(string source, bool isLeft)
    {
        if (string.IsNullOrEmpty(source))
        {
            return null;
        }

        string value = source.ToLowerInvariant()
            .Replace("hand_", string.Empty)
            .Replace("hand", string.Empty)
            .Replace("finger", string.Empty)
            .Replace("root", string.Empty)
            .Replace("tip", "end")
            .Replace("index", "pointer");

        value = value.Replace(isLeft ? "l_" : "r_", string.Empty)
            .Replace("left_", string.Empty)
            .Replace("right_", string.Empty)
            .Replace("-", "_")
            .Replace(" ", "_");

        if (value.Contains("wrist") || value.Contains("palm"))
        {
            return "wrist";
        }

        string finger = null;
        if (value.Contains("thumb")) finger = "thumb";
        else if (value.Contains("pointer")) finger = "pointer";
        else if (value.Contains("middle")) finger = "middle";
        else if (value.Contains("ring")) finger = "ring";
        else if (value.Contains("pinky") || value.Contains("little")) finger = "pinky";

        if (finger == null)
        {
            return null;
        }

        if (value.Contains("end") || value.Contains("distalend"))
        {
            return finger + "_end";
        }

        if (value.Contains("0") || value.Contains("metacarpal"))
        {
            return finger + "_1";
        }

        if (value.Contains("1") || value.Contains("proximal"))
        {
            return finger + "_1";
        }

        if (value.Contains("2") || value.Contains("intermediate"))
        {
            return finger + "_2";
        }

        if (value.Contains("3") || value.Contains("distal"))
        {
            return finger + "_3";
        }

        return null;
    }

    [Serializable]
    private sealed class HandFramePacket
    {
        public HandFrame[] hands;
    }

    [Serializable]
    private sealed class HandFrame
    {
        public string handedness;
        public bool isTracked;
        public PoseData wrist;
        public JointPose[] joints;
    }

    [Serializable]
    private struct JointPose
    {
        public string name;
        public PoseData pose;
    }

    [Serializable]
    private struct PoseData
    {
        public bool isValid;
        public Vector3 position;
        public Quaternion rotation;
    }
}
