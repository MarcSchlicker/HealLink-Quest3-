using UnityEngine;
using System;
using System.Linq;
using TMPro;

/// <summary>
/// DepthDistance: sample depth from a specific depthSourceObject only.
/// Map the world hit point to the depthSourceObject's UVs and sample that texture.
/// </summary>
public class DepthDistance : MonoBehaviour
{
    [Header("Finger / Billboard link")]
    public FingerPosition fingerPositionRef;

    [Header("Depth source (use this GameObject only)")]
    [Tooltip("Drag the GameObject that holds the depth visualization (its Renderer will be used).")]
    public GameObject depthSourceObject;
    [Tooltip("If true, sample the latest AHAT Align depth frame directly so zero pixels remain invalid.")]
    public bool useLatestAhatAlignDepthFrame = true;
    [Tooltip("Optional fallback for the older test_rm_depth_ahat stream.")]
    public bool useLegacyAhatDepthFrameFallback = false;
    public string depthTextureProperty = "_RawDepth";

    [Header("Depth format")]
    public string depthChannel = "r";
    public bool depthIsNormalized = true;
    public float depthScale = 10f;

    [Header("Behavior")]
    [Tooltip("If true, UVs outside the depth source are clamped to edge so a value is always returned.")]
    public bool clampDepthUVToSource = true;

    [Header("Output")]
    public TextMeshProUGUI depthTextTarget;
    public string depthTextFormat = "Depth: {0:F2} m (raw {1:F3})";
    public Color textColor = Color.red;

    [Header("Debug")]
    public bool drawDebugRay = true;
    public Color debugRayColor = Color.yellow;

    [HideInInspector] public float lastDepthMeters = 0f;
    [HideInInspector] public float lastRawValue = 0f;
    [HideInInspector] public Vector2 lastUV = Vector2.zero;
    [HideInInspector] public Vector3 lastHitPoint = Vector3.zero;

    // Runtime state.
    private Transform xrCamera;
    private OVRSkeleton rightHandSkeleton;
    private bool skeletonReady = false;

    private Texture2D cachedDepthTex = null;
    private RenderTexture lastDepthRT = null;

    // Event for external listeners.
    public event Action<float, float, Vector2, Vector3> OnDepthSampled;

    void Start()
    {
        if (fingerPositionRef == null)
            fingerPositionRef = FindObjectOfType<FingerPosition>();

        if (depthTextTarget == null)
        {
            var tmp = FindObjectOfType<TextMeshProUGUI>();
            if (tmp != null) depthTextTarget = tmp;
        }
    }

    void Update()
    {
        if (fingerPositionRef == null) return;

        GameObject billboard = GetBillboardObject();
        if (billboard == null) return;

        if (!TryGetCameraFromBillboard(billboard)) return;
        if (!TryGetFingerTipPositionFromSkeleton(fingerPositionRef.rightHandAnchorName, fingerPositionRef.tipId, out Vector3 fingerPos)) return;

        ProcessRayAndSample(fingerPos, billboard);
    }

    public bool TryProjectWorldPointToDepth(
        Vector3 worldPoint,
        Transform cameraOverride,
        out Vector3 depthWorldPoint,
        out float depthMeters,
        out float rawValue,
        out Vector2 billboardUV)
    {
        depthWorldPoint = Vector3.zero;
        depthMeters = 0f;
        rawValue = 0f;
        billboardUV = Vector2.zero;

        GameObject billboard = GetBillboardObject();
        Transform camera = cameraOverride != null ? cameraOverride : xrCamera;
        if (camera == null && billboard != null && TryGetCameraFromBillboard(billboard))
        {
            camera = xrCamera;
        }

        if (camera == null || billboard == null || depthSourceObject == null)
        {
            return false;
        }

        Vector3 rayVector = worldPoint - camera.position;
        if (rayVector.sqrMagnitude < 1e-8f)
        {
            return false;
        }

        Vector3 rayDirection = rayVector.normalized;
        Ray cameraRay = new Ray(camera.position, rayDirection);

        if (!TryGetQuadHitUV(cameraRay, billboard.transform, out billboardUV, out Vector3 billboardHitPoint))
        {
            return false;
        }

        if (!TrySampleDepthFromSource(billboardHitPoint, billboardUV, out depthMeters, out rawValue))
        {
            return false;
        }

        if (depthMeters <= 0f || !IsFinite(depthMeters))
        {
            return false;
        }

        depthWorldPoint = camera.position + rayDirection * depthMeters;
        return IsFinite(depthWorldPoint);
    }

    bool TryGetCameraFromBillboard(GameObject billboard)
    {
        if (xrCamera != null) return true;
        if (billboard != null)
        {
            var bf = billboard.GetComponent("BillboardFollow");
            if (bf != null)
            {
                var camField = bf.GetType().GetField("xrCamera");
                if (camField != null) xrCamera = camField.GetValue(bf) as Transform;
                if (xrCamera == null)
                {
                    var camProp = bf.GetType().GetProperty("xrCamera");
                    if (camProp != null) xrCamera = camProp.GetValue(bf, null) as Transform;
                }
            }
        }
        if (xrCamera == null)
        {
            var found = GameObject.Find("CenterEyeAnchor");
            if (found != null) xrCamera = found.transform;
        }
        return xrCamera != null;
    }

    GameObject GetBillboardObject()
    {
        return fingerPositionRef != null ? fingerPositionRef.billboard : null;
    }

    bool TryGetFingerTipPositionFromSkeleton(string rightHandAnchorName, OVRSkeleton.BoneId tipId, out Vector3 fingerPos)
    {
        fingerPos = Vector3.zero;
        if (rightHandSkeleton == null)
        {
            var rightHandAnchor = GameObject.Find(rightHandAnchorName);
            if (rightHandAnchor != null)
                rightHandSkeleton = rightHandAnchor.GetComponentInChildren<OVRSkeleton>();
        }
        if (rightHandSkeleton == null) return false;
        if (!skeletonReady)
        {
            if (rightHandSkeleton.Bones != null && rightHandSkeleton.Bones.Count > 0)
                skeletonReady = true;
            else
                return false;
        }
        var bone = rightHandSkeleton.Bones.FirstOrDefault(b => b.Id == tipId);
        if (bone != null && bone.Transform != null)
        {
            fingerPos = bone.Transform.position;
            return true;
        }
        return false;
    }

    void ProcessRayAndSample(Vector3 fingerPos, GameObject billboard)
    {
        if (xrCamera == null || depthSourceObject == null) return;

        Vector3 rayDir = (fingerPos - xrCamera.position).normalized;
        Ray camRay = new Ray(xrCamera.position, rayDir);

        Transform quadT = billboard.transform;
        if (!TryGetQuadHitUV(camRay, quadT, out Vector2 uvOnBillboard, out Vector3 hitPoint)) return;

        lastUV = uvOnBillboard;
        lastHitPoint = hitPoint;

        if (!TrySampleDepthFromSource(hitPoint, uvOnBillboard, out float depthMeters, out float rawValue))
            return;

        lastDepthMeters = depthMeters;
        lastRawValue = rawValue;

        if (depthTextTarget != null)
        {
            depthTextTarget.text = string.Format(depthTextFormat, depthMeters, rawValue);
            depthTextTarget.color = textColor;
        }

        if (drawDebugRay)
            Debug.DrawLine(xrCamera.position, hitPoint, debugRayColor);

        OnDepthSampled?.Invoke(depthMeters, rawValue, lastUV, lastHitPoint);
    }

    bool TryGetQuadHitUV(Ray camRay, Transform quadT, out Vector2 uv, out Vector3 hitPoint)
    {
        uv = Vector2.zero;
        hitPoint = Vector3.zero;
        Collider quadCol = quadT.GetComponent<Collider>();
        RaycastHit hit;
        if (quadCol != null && quadCol.Raycast(camRay, out hit, Mathf.Infinity))
        {
            hitPoint = hit.point;
            MeshCollider meshCol = quadCol as MeshCollider;
            if (meshCol != null && meshCol.sharedMesh != null)
            {
                uv = hit.textureCoord;
            }
            else
            {
                Vector3 local = quadT.InverseTransformPoint(hit.point);
                Vector2 uvRaw;
                var mf = quadT.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    var bounds = mf.sharedMesh.bounds;
                    float sizeX = Mathf.Max(1e-6f, bounds.size.x);
                    float sizeY = Mathf.Max(1e-6f, bounds.size.y);
                    uvRaw = new Vector2((local.x - bounds.min.x) / sizeX, (local.y - bounds.min.y) / sizeY);
                }
                else
                {
                    uvRaw = new Vector2(local.x + 0.5f, local.y + 0.5f);
                }
                var quadR = quadT.GetComponent<Renderer>();
                if (quadR != null && quadR.sharedMaterial != null)
                {
                    Vector2 scale = quadR.sharedMaterial.mainTextureScale;
                    Vector2 offset = quadR.sharedMaterial.mainTextureOffset;
                    uv = Vector2.Scale(uvRaw, scale) + offset;
                }
                else uv = uvRaw;
                uv.x = Mathf.Clamp01(uv.x);
                uv.y = Mathf.Clamp01(uv.y);
            }
            return true;
        }

        Plane quadPlane = new Plane(quadT.forward, quadT.position);
        if (quadPlane.Raycast(camRay, out float enter))
        {
            Vector3 p = camRay.GetPoint(enter);
            hitPoint = p;
            Vector3 local = quadT.InverseTransformPoint(p);
            Vector2 uvRaw;
            var mf = quadT.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                var bounds = mf.sharedMesh.bounds;
                float sizeX = Mathf.Max(1e-6f, bounds.size.x);
                float sizeY = Mathf.Max(1e-6f, bounds.size.y);
                uvRaw = new Vector2((local.x - bounds.min.x) / sizeX, (local.y - bounds.min.y) / sizeY);
            }
            else uvRaw = new Vector2(local.x + 0.5f, local.y + 0.5f);

            var quadR = quadT.GetComponent<Renderer>();
            if (quadR != null && quadR.sharedMaterial != null)
            {
                Vector2 scale = quadR.sharedMaterial.mainTextureScale;
                Vector2 offset = quadR.sharedMaterial.mainTextureOffset;
                uv = Vector2.Scale(uvRaw, scale) + offset;
            }
            else uv = uvRaw;

            if (uv.x >= -1e-6f && uv.x <= 1f + 1e-6f && uv.y >= -1e-6f && uv.y <= 1f + 1e-6f)
            {
                hitPoint = p;
                uv = new Vector2(Mathf.Clamp01(uv.x), Mathf.Clamp01(uv.y));
                return true;
            }
        }
        return false;
    }

    // Aligned AHAT frames use billboard/PV UVs directly. Legacy sources still use the depth-source transform.
    bool TrySampleDepthFromSource(
        Vector3 worldHitPoint,
        Vector2 billboardUV,
        out float depthMeters,
        out float rawValue)
    {
        depthMeters = 0f;
        rawValue = 0f;
        if (depthSourceObject == null) return false;

        if (useLatestAhatAlignDepthFrame && rgbd_align_ahat.HasLatestAlignedDepthFrame)
        {
            return rgbd_align_ahat.TrySampleLatestAlignedDepthMeters(
                billboardUV,
                out depthMeters,
                out rawValue);
        }

        Renderer srcRenderer = depthSourceObject.GetComponent<Renderer>();
        MeshFilter srcMF = depthSourceObject.GetComponent<MeshFilter>();
        if (srcRenderer == null) return false;

        // Compute UV on depth source from world point.
        Vector2 depthUV = GetUVFromWorldPointOnTarget(worldHitPoint, depthSourceObject.transform, srcMF, srcRenderer);

        if (clampDepthUVToSource)
        {
            depthUV.x = Mathf.Clamp01(depthUV.x);
            depthUV.y = Mathf.Clamp01(depthUV.y);
        }

        if (useLegacyAhatDepthFrameFallback && test_rm_depth_ahat.HasLatestDepthFrame)
        {
            ushort depthMillimeters;
            bool hasDepth = test_rm_depth_ahat.TrySampleLatestDepthMeters(depthUV, out depthMeters, out depthMillimeters);
            rawValue = depthMillimeters;
            return hasDepth;
        }

        // Get texture from the depth source renderer.
        Texture src = srcRenderer.sharedMaterial != null ? srcRenderer.sharedMaterial.GetTexture(depthTextureProperty) : null;
        if (src == null) return false;

        if (src is Texture2D tex2D)
        {
            try
            {
                Color c = tex2D.GetPixelBilinear(depthUV.x, depthUV.y);
                rawValue = GetChannelFromColor(c, depthChannel);
                depthMeters = (tex2D.format == TextureFormat.RFloat) ? rawValue : (depthIsNormalized ? rawValue * depthScale : rawValue);
                return true;
            }
            catch (UnityException)
            {
                RenderTexture tmp = RenderTexture.GetTemporary(tex2D.width, tex2D.height, 0, RenderTextureFormat.Default);
                Graphics.Blit(tex2D, tmp);
                bool ok = ReadFromRenderTexture(tmp, depthUV, out depthMeters, out rawValue);
                RenderTexture.ReleaseTemporary(tmp);
                return ok;
            }
        }

        if (src is RenderTexture rt)
        {
            return ReadFromRenderTexture(rt, depthUV, out depthMeters, out rawValue);
        }

        return false;
    }

    Vector2 GetUVFromWorldPointOnTarget(Vector3 worldPoint, Transform target, MeshFilter mf, Renderer r)
    {
        Vector3 local = target.InverseTransformPoint(worldPoint);
        Vector2 uvRaw;
        if (mf != null && mf.sharedMesh != null)
        {
            var bounds = mf.sharedMesh.bounds;
            float sizeX = Mathf.Max(1e-6f, bounds.size.x);
            float sizeY = Mathf.Max(1e-6f, bounds.size.y);
            uvRaw = new Vector2((local.x - bounds.min.x) / sizeX, (local.y - bounds.min.y) / sizeY);
        }
        else
        {
            uvRaw = new Vector2(local.x + 0.5f, local.y + 0.5f);
        }

        if (r != null && r.sharedMaterial != null)
        {
            Vector2 scale = r.sharedMaterial.mainTextureScale;
            Vector2 offset = r.sharedMaterial.mainTextureOffset;
            Vector2 uv = Vector2.Scale(uvRaw, scale) + offset;
            return uv;
        }

        return uvRaw;
    }

    bool ReadFromRenderTexture(RenderTexture rt, Vector2 uv, out float depthMeters, out float rawValue)
    {
        depthMeters = 0f;
        rawValue = 0f;
        if (rt == null) return false;

        TextureFormat texFormat = TextureFormat.RGBA32;
        if (rt.format == RenderTextureFormat.RFloat || rt.format == RenderTextureFormat.RHalf)
            texFormat = TextureFormat.RFloat;

        if (cachedDepthTex == null || lastDepthRT != rt || cachedDepthTex.width != rt.width || cachedDepthTex.height != rt.height || cachedDepthTex.format != texFormat)
        {
            if (cachedDepthTex != null) DestroyImmediate(cachedDepthTex);
            cachedDepthTex = new Texture2D(rt.width, rt.height, texFormat, false);
            lastDepthRT = rt;
        }

        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        cachedDepthTex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        cachedDepthTex.Apply();
        RenderTexture.active = prev;

        Color c = cachedDepthTex.GetPixelBilinear(uv.x, uv.y);
        rawValue = GetChannelFromColor(c, depthChannel);
        depthMeters = (lastDepthRT != null && (lastDepthRT.format == RenderTextureFormat.RFloat || lastDepthRT.format == RenderTextureFormat.RHalf))
            ? rawValue
            : (depthIsNormalized ? rawValue * depthScale : rawValue);

        return true;
    }

    float GetChannelFromColor(Color c, string channel)
    {
        switch (channel.ToLower())
        {
            case "r": return c.r;
            case "g": return c.g;
            case "b": return c.b;
            case "a": return c.a;
            default: return c.r;
        }
    }

    static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }

    void OnDestroy()
    {
        if (cachedDepthTex != null) DestroyImmediate(cachedDepthTex);
    }
}
