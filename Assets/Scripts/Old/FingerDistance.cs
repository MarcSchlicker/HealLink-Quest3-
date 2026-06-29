// FingerDistance_TMP.cs
using UnityEngine;
using System.Linq;
using System.IO;
using System;
#if UNITY_EDITOR
using UnityEditor;
#endif
using TMPro;

public class FingerDistance : MonoBehaviour
{
    [Header("Referenzen")]
    public BillboardFollow billboard;                     // Quad mit BillboardFollow (PV wird dort angezeigt)
    public Transform xrCamera;                            // CenterEyeAnchor (optional)
    public string rightHandAnchorName = "RightHandAnchor";
    public OVRSkeleton.BoneId tipId = OVRSkeleton.BoneId.Hand_IndexTip;

    [Header("PV (Marker)")]
    public string materialTexturePropertyPV = "_MainTex"; // Property im Billboard-Material (PV)
    public int markerSizePixels = 12;
    public Color markerColor = Color.red;

    [Header("Depth Quelle (auf diesem GameObject)")]
    public Texture depthTexture;                          // Depth-Texture (falls leer: versucht Renderer.material texture dieses GameObjects)
    public string depthTextureProperty = "_MainTex";      // falls Depth in Material liegt, Property-Name
    public string depthChannel = "r";                     // Kanal in der Depth-Texture
    public bool depthIsNormalized = true;                 // true: depth in [0..1]
    public float depthScale = 10f;                        // wenn normalized: depth_m = value * depthScale

    [Header("TextMeshPro UI Ziel (nur TMP)")]
    public TextMeshProUGUI textTargetTMPUI;               // Zieh hier dein TMP UI Textfeld rein
    public Color textColor = Color.red;

    [Header("Overlay Fallback")]
    public bool useOverlayFallback = true;
    public GameObject overlayPrefab;                      // optional: kleines rotes Quad prefab

    [Header("Export")]
    public bool enablePeriodicExport = true;
    public float exportIntervalSeconds = 10f;             // Intervall in Sekunden
    public string exportFolderName = "DepthExport";       // Assets/DepthExport
    public string exportFilePrefix = "depth_";

    [Header("Smoothing / Limits")]
    public float smoothTime = 0.06f;
    public float minDistance = 0.01f;
    public float maxDistance = 20f;

    // intern
    private OVRSkeleton rightHandSkeleton;
    private bool skeletonReady = false;
    private float currentDistance = 0f;
    private float velocity = 0f;

    private Renderer quadRendererPV;
    private Texture originalPVTextureRef;
    private Texture2D editablePVCopy = null;   // copy of PV texture we draw marker on

    private Texture2D cachedDepthTex = null;   // copy for depth sampling (if depthTexture is RT)
    private RenderTexture lastDepthRT = null;

    private GameObject overlayInstance = null;
    private int frameCounter = 0;

    void Start()
    {
        if (xrCamera == null)
        {
            var found = GameObject.Find("CenterEyeAnchor");
            if (found != null) xrCamera = found.transform;
        }

        TryFindSkeleton();

        if (billboard != null)
            quadRendererPV = billboard.GetComponent<Renderer>();

        // Wenn depthTexture nicht gesetzt, versuche Material auf diesem GameObject
        if (depthTexture == null)
        {
            var rend = GetComponent<Renderer>();
            if (rend != null)
            {
                var tex = rend.material.GetTexture(depthTextureProperty);
                if (tex != null) depthTexture = tex;
            }
        }

        if (enablePeriodicExport)
            InvokeRepeating(nameof(ExportDepthImageNow), 1f, exportIntervalSeconds);

        // TMP fallback: falls du das TMP-Feld nicht ziehen kannst, versuchen wir, eines im Scene-Canvas zu finden
        if (textTargetTMPUI == null)
        {
            var foundTMP = FindObjectOfType<TextMeshProUGUI>();
            if (foundTMP != null)
            {
                textTargetTMPUI = foundTMP;
                Debug.Log("FingerDistance_TMP: textTargetTMPUI automatisch gefunden: " + foundTMP.name);
            }
            else
            {
                Debug.LogWarning("FingerDistance_TMP: textTargetTMPUI ist nicht gesetzt und kein TMP-UI im Scene gefunden.");
            }
        }
    }

    void TryFindSkeleton()
    {
        var rightHandAnchor = GameObject.Find(rightHandAnchorName);
        if (rightHandAnchor != null)
            rightHandSkeleton = rightHandAnchor.GetComponentInChildren<OVRSkeleton>();
    }

    void Update()
    {
        if (billboard == null) return;
        if (xrCamera == null)
        {
            var found = GameObject.Find("CenterEyeAnchor");
            if (found != null) xrCamera = found.transform;
            else return;
        }

        if (rightHandSkeleton == null) TryFindSkeleton();
        if (rightHandSkeleton == null) return;

        if (!skeletonReady)
        {
            if (rightHandSkeleton.Bones != null && rightHandSkeleton.Bones.Count > 0)
                skeletonReady = true;
            else
                return;
        }

        var tipBone = rightHandSkeleton.Bones.FirstOrDefault(b => b.Id == tipId);
        if (tipBone == null) return;

        Vector3 fingerPos = tipBone.Transform.position;

        // Ray von Kamera in Richtung Finger
        Vector3 rayDir = (fingerPos - xrCamera.position).normalized;
        Ray camRay = new Ray(xrCamera.position, rayDir);

        Transform quadT = billboard.transform;

        // 1) Schnittpunkt mit Quad (Collider bevorzugt)
        bool hitQuad = false;
        Vector2 uv = Vector2.zero;
        Vector3 preciseHitWorld = Vector3.zero;

        Collider quadCol = quadT.GetComponent<Collider>();
        RaycastHit hit;
        if (quadCol != null)
        {
            if (quadCol.Raycast(camRay, out hit, maxDistance))
            {
                hitQuad = true;
                preciseHitWorld = hit.point;
                MeshCollider meshCol = quadCol as MeshCollider;
                if (meshCol != null && meshCol.sharedMesh != null)
                    uv = hit.textureCoord;
                else
                {
                    Vector3 local = quadT.InverseTransformPoint(hit.point);
                    uv = new Vector2(local.x + 0.5f, local.y + 0.5f);
                }
            }
        }

        // 2) Falls kein Collider: Ebene schneiden und UV berechnen
        if (!hitQuad)
        {
            Plane quadPlane = new Plane(quadT.forward, quadT.position);
            float enter;
            if (quadPlane.Raycast(camRay, out enter))
            {
                Vector3 hitPoint = camRay.GetPoint(enter);
                Vector3 local = quadT.InverseTransformPoint(hitPoint);
                if (Mathf.Abs(local.x) <= 0.5f * quadT.localScale.x + 1e-6f &&
                    Mathf.Abs(local.y) <= 0.5f * quadT.localScale.y + 1e-6f)
                {
                    hitQuad = true;
                    preciseHitWorld = hitPoint;
                    uv = new Vector2(local.x + 0.5f, local.y + 0.5f);
                }
            }
        }

        if (!hitQuad) return;

        // Debug
        Debug.Log($"FingerDistance_TMP: UV={uv} hitWorld={preciseHitWorld}");

        // 3) Depthwert an UV aus depthTexture lesen
        if (!TryReadDepthAtUV(uv, out float depthMeters, out string depthDebug))
        {
            Debug.LogWarning("FingerDistance_TMP: Depth read failed: " + depthDebug);
            if (useOverlayFallback) ShowOverlayMarker(preciseHitWorld);
            return;
        }

        // 4) lateral r
        Vector3 camToHit = preciseHitWorld - xrCamera.position;
        float along = Vector3.Dot(camToHit, xrCamera.forward.normalized);
        Vector3 pointOnForwardAxis = xrCamera.position + xrCamera.forward.normalized * along;
        float r = Vector3.Distance(preciseHitWorld, pointOnForwardAxis);

        // 5) d_center berechnen
        float dPixel = depthMeters;
        float r2 = r * r;
        float dCenter = (dPixel * dPixel <= r2) ? Mathf.Max(minDistance, Mathf.Sqrt(Mathf.Max(0f, dPixel * dPixel - r2))) : Mathf.Sqrt(dPixel * dPixel - r2);

        // 6) Glätten und setzen
        float target = Mathf.Clamp(dCenter, minDistance, maxDistance);
        currentDistance = (smoothTime > 0f) ? Mathf.SmoothDamp(currentDistance, target, ref velocity, smoothTime) : target;
        billboard.distance = currentDistance;

        // 7) Marker auf PV malen (oder Overlay)
        if (EnsureEditablePVCopyExists())
            DrawMarkerOnPVTexture(uv);
        else if (useOverlayFallback)
            ShowOverlayMarker(preciseHitWorld);

        // 8) TMP UI schreiben (rot)
        if (textTargetTMPUI != null)
        {
            textTargetTMPUI.text = $"{dPixel:F2} m";
            textTargetTMPUI.color = textColor;
        }

        Debug.DrawLine(xrCamera.position, preciseHitWorld, Color.yellow);
        Debug.DrawRay(xrCamera.position, xrCamera.forward * currentDistance, Color.cyan);
        Debug.Log($"FingerDistance_TMP: depth={dPixel:F3} r={r:F3} dCenter={dCenter:F3} set={currentDistance:F3}");
    }

    bool TryReadDepthAtUV(Vector2 uv, out float depthMeters, out string debugMsg)
    {
        depthMeters = 0f;
        debugMsg = "";

        if (depthTexture == null)
        {
            debugMsg = "depthTexture ist null";
            return false;
        }

        // Texture2D
        if (depthTexture is Texture2D t2)
        {
            try
            {
                Color c = t2.GetPixelBilinear(uv.x, uv.y);
                float v = GetChannelFromColor(c, depthChannel);
                depthMeters = depthIsNormalized ? v * depthScale : v;
                debugMsg = $"Texture2D sample={v:F4} size={t2.width}x{t2.height}";
                return true;
            }
            catch (UnityException e)
            {
                debugMsg = "Texture2D not readable: " + e.Message;
                return false;
            }
        }

        // RenderTexture
        if (depthTexture is RenderTexture rt)
        {
            if (cachedDepthTex == null || lastDepthRT != rt)
            {
                cachedDepthTex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                lastDepthRT = rt;
            }

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            cachedDepthTex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            cachedDepthTex.Apply();
            RenderTexture.active = prev;

            Color c = cachedDepthTex.GetPixelBilinear(uv.x, uv.y);
            float v = GetChannelFromColor(c, depthChannel);
            depthMeters = depthIsNormalized ? v * depthScale : v;
            debugMsg = $"RenderTexture sample={v:F4} size={rt.width}x{rt.height}";
            return true;
        }

        debugMsg = "depthTexture type not supported: " + depthTexture.GetType().Name;
        return false;
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

    bool EnsureEditablePVCopyExists()
    {
        if (quadRendererPV == null) quadRendererPV = billboard.GetComponent<Renderer>();
        if (quadRendererPV == null) return false;

        Texture tex = quadRendererPV.material.GetTexture(materialTexturePropertyPV);
        if (tex == null) return false;

        if (editablePVCopy != null) return true;

        if (tex is Texture2D t2)
        {
            try
            {
                editablePVCopy = new Texture2D(t2.width, t2.height, TextureFormat.RGBA32, false);
                editablePVCopy.SetPixels(t2.GetPixels());
                editablePVCopy.Apply();
                quadRendererPV.material.SetTexture(materialTexturePropertyPV, editablePVCopy);
                originalPVTextureRef = tex;
                return true;
            }
            catch (UnityException)
            {
                RenderTexture tmp = RenderTexture.GetTemporary(t2.width, t2.height, 0, RenderTextureFormat.Default);
                Graphics.Blit(t2, tmp);
                Texture2D copy = new Texture2D(tmp.width, tmp.height, TextureFormat.RGBA32, false);
                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = tmp;
                copy.ReadPixels(new Rect(0, 0, tmp.width, tmp.height), 0, 0);
                copy.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(tmp);

                editablePVCopy = copy;
                quadRendererPV.material.SetTexture(materialTexturePropertyPV, editablePVCopy);
                originalPVTextureRef = tex;
                return true;
            }
        }

        if (tex is RenderTexture rt)
        {
            Texture2D copy = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            copy.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            copy.Apply();
            RenderTexture.active = prev;

            editablePVCopy = copy;
            quadRendererPV.material.SetTexture(materialTexturePropertyPV, editablePVCopy);
            originalPVTextureRef = tex;
            return true;
        }

        return false;
    }

    void DrawMarkerOnPVTexture(Vector2 uv)
    {
        if (editablePVCopy == null) return;
        int px = Mathf.RoundToInt(uv.x * (editablePVCopy.width - 1));
        int py = Mathf.RoundToInt(uv.y * (editablePVCopy.height - 1));
        int r = Mathf.Max(1, markerSizePixels / 2);
        for (int y = -r; y <= r; y++)
            for (int x = -r; x <= r; x++)
                if (x * x + y * y <= r * r)
                {
                    int sx = Mathf.Clamp(px + x, 0, editablePVCopy.width - 1);
                    int sy = Mathf.Clamp(py + y, 0, editablePVCopy.height - 1);
                    editablePVCopy.SetPixel(sx, sy, markerColor);
                }
        editablePVCopy.Apply();
        quadRendererPV.material.SetTexture(materialTexturePropertyPV, editablePVCopy);
    }

    void ShowOverlayMarker(Vector3 worldPos)
    {
        if (overlayPrefab == null)
        {
            if (overlayInstance == null)
            {
                overlayInstance = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                overlayInstance.GetComponent<Collider>().enabled = false;
                var mr = overlayInstance.GetComponent<Renderer>();
                mr.material = new Material(Shader.Find("Unlit/Color"));
                mr.material.color = markerColor;
                overlayInstance.transform.localScale = Vector3.one * 0.03f;
            }
        }
        else
        {
            if (overlayInstance == null) overlayInstance = Instantiate(overlayPrefab);
        }

        overlayInstance.transform.position = worldPos + billboard.transform.forward * 0.001f;
        overlayInstance.transform.rotation = Quaternion.LookRotation(overlayInstance.transform.position - xrCamera.position, xrCamera.up);
    }

    // Export-Funktion: speichert PNGs unter Assets/DepthExport und refreshed das Project Window (Editor only)
    public void ExportDepthImageNow()
    {
        if (depthTexture == null)
        {
            Debug.LogWarning("FingerDistance_TMP: depthTexture ist nicht gesetzt.");
            return;
        }

        Texture2D copy = null;

        if (depthTexture is RenderTexture rt)
        {
            copy = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            copy.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            copy.Apply();
            RenderTexture.active = prev;
        }
        else if (depthTexture is Texture2D t2)
        {
            try
            {
                copy = new Texture2D(t2.width, t2.height, TextureFormat.RGBA32, false);
                copy.SetPixels(t2.GetPixels());
                copy.Apply();
            }
            catch (UnityException)
            {
                RenderTexture tmp = RenderTexture.GetTemporary(t2.width, t2.height, 0, RenderTextureFormat.Default);
                Graphics.Blit(t2, tmp);
                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = tmp;
                copy = new Texture2D(tmp.width, tmp.height, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, tmp.width, tmp.height), 0, 0);
                copy.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(tmp);
            }
        }
        else
        {
            Debug.LogWarning("FingerDistance_TMP: depthTexture type not supported for export.");
            return;
        }

        if (copy != null)
        {
            Texture2D gray = ConvertDepthToGrayscale(copy);
            string assetsFolder = Application.dataPath;
            string folder = Path.Combine(assetsFolder, exportFolderName);
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string filePath = Path.Combine(folder, exportFilePrefix + timestamp + ".png");
            byte[] png = gray.EncodeToPNG();
            File.WriteAllBytes(filePath, png);
            Debug.Log($"FingerDistance_TMP: Depth snapshot saved to {filePath}");

#if UNITY_EDITOR
            string relativePath = "Assets/" + exportFolderName + "/" + exportFilePrefix + timestamp + ".png";
            AssetDatabase.ImportAsset(relativePath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();
            Debug.Log("FingerDistance_TMP: AssetDatabase refreshed, Datei sichtbar im Project Window.");
#endif
            DestroyImmediate(copy);
            DestroyImmediate(gray);
        }
    }

    Texture2D ConvertDepthToGrayscale(Texture2D src)
    {
        int w = src.width;
        int h = src.height;
        Texture2D outTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        Color[] srcPixels = src.GetPixels();
        Color[] outPixels = new Color[srcPixels.Length];
        for (int i = 0; i < srcPixels.Length; i++)
        {
            float v = GetChannelFromColor(srcPixels[i], depthChannel);
            if (depthIsNormalized) v = Mathf.Clamp01(v);
            float gray = Mathf.Clamp01(v);
            outPixels[i] = new Color(gray, gray, gray, 1f);
        }
        outTex.SetPixels(outPixels);
        outTex.Apply();
        return outTex;
    }
}
