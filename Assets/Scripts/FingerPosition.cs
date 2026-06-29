// FingerPosition.cs
using UnityEngine;
using System.Linq;
using TMPro;

public class FingerPosition : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Quad GameObject that has the BillboardFollow component (used to get XR camera).")]
    public GameObject billboard;

    [Header("Hand tracking")]
    [Tooltip("Name of the RightHandAnchor GameObject (used to find OVRSkeleton).")]
    public string rightHandAnchorName = "RightHandAnchor";

    [Tooltip("Bone id for the fingertip (OVRSkeleton.BoneId).")]
    public OVRSkeleton.BoneId tipId = OVRSkeleton.BoneId.Hand_IndexTip;

    [Header("Marker placement (relative to Quad)")]
    [Tooltip("Distance in meters in front of the Quad (towards the camera) where the marker should be placed.")]
    public float distanceFromQuad = 0.05f;

    [Tooltip("Additional gap in front of the color image so the marker is not hidden by the quad.")]
    public float markerFrontGapMeters = 0.005f;

    [Tooltip("Scale of the marker in meters.")]
    public float markerScale = 0.03f;

    [Tooltip("If true, marker world scale changes with camera distance so it appears the same size.")]
    public bool maintainApparentMarkerSize = true;

    [Tooltip("Distance in meters at which markerScale is used as-is.")]
    public float markerReferenceDistance = 8f;

    [Tooltip("Smallest allowed marker world scale so it does not disappear when close.")]
    public float minMarkerWorldScale = 0.004f;

    [Tooltip("Largest allowed marker world scale.")]
    public float maxMarkerWorldScale = 0.04f;

    [Tooltip("Render the marker over the image quads so it cannot be hidden by depth testing.")]
    public bool renderMarkerOnTop = true;

    [Tooltip("Smooth movement of the marker (seconds). 0 = instant.")]
    public float smoothTime = 0.03f;

    [Tooltip("If true, marker is visible; false hides it.")]
    public bool markerVisible = true;

    [Tooltip("If true, clamp marker to be between camera and quad intersection (prevents placing behind camera).")]
    public bool clampBetweenCameraAndQuad = true;

    [Tooltip("If true, keep the marker at the last valid point when the ray briefly misses the quad.")]
    public bool holdLastMarkerWhenRayMisses = true;

    [Header("Debug / last values (read-only)")]
    [Tooltip("Last measured distance from camera to the pixel intersection point.")]
    public float lastPixelDistance = 0f;

    [Tooltip("Last measured distance from camera to the center of the quad.")]
    public float lastCenterDistance = 0f;

    // internals
    private Transform xrCamera;
    private OVRSkeleton rightHandSkeleton;
    private Transform tipTransform;
    private GameObject markerInstance;
    private Vector3 velocity = Vector3.zero;
    private bool hasLastMarkerPosition = false;
    private float lastMarkerCameraDistance = 0f;
    private bool hasLastBillboardCoordinates = false;
    private Vector2 lastCenteredBillboardCoordinates = Vector2.zero;

    void Start()
    {
        // get xrCamera from BillboardFollow if possible
        if (billboard != null)
        {
            var bf = billboard.GetComponent<BillboardFollow>();
            if (bf != null)
            {
                var camField = bf.GetType().GetField("xrCamera");
                if (camField != null)
                    xrCamera = camField.GetValue(bf) as Transform;

                if (xrCamera == null)
                {
                    var camProp = bf.GetType().GetProperty("xrCamera");
                    if (camProp != null)
                        xrCamera = camProp.GetValue(bf, null) as Transform;
                }
            }
        }

        // fallback: find CenterEyeAnchor
        if (xrCamera == null)
        {
            var found = GameObject.Find("CenterEyeAnchor");
            if (found != null) xrCamera = found.transform;
        }

        TryFindSkeleton();
        CreateMarker();
        
    }

    void TryFindSkeleton()
    {
        var rightHandAnchor = GameObject.Find(rightHandAnchorName);
        if (rightHandAnchor != null)
            rightHandSkeleton = rightHandAnchor.GetComponentInChildren<OVRSkeleton>();
    }

    void CreateMarker()
    {
        // create a simple unlit red sphere as marker
        markerInstance = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        markerInstance.name = "FingerPosition_Marker";
        var col = markerInstance.GetComponent<Collider>();
        if (col != null) Destroy(col);

        var mr = markerInstance.GetComponent<Renderer>();
        Shader markerShader = renderMarkerOnTop ? Resources.Load<Shader>("AlwaysOnTopColor") : null;
        if (markerShader == null && renderMarkerOnTop) markerShader = Shader.Find("Hidden/AlwaysOnTopColor");
        if (markerShader == null) markerShader = Shader.Find("Unlit/Color");

        var mat = new Material(markerShader);
        mat.color = Color.red;
        mat.renderQueue = 5000;
        mr.material = mat;

        markerInstance.transform.localScale = Vector3.one * Mathf.Max(0.0001f, markerScale);
        markerInstance.SetActive(markerVisible);
    }

    void Update()
    {
        if (!markerVisible)
        {
            if (markerInstance != null && markerInstance.activeSelf) markerInstance.SetActive(false);
            return;
        }

        if (markerInstance != null && !markerInstance.activeSelf) markerInstance.SetActive(true);

        // ensure xrCamera
        if (xrCamera == null)
        {
            if (billboard != null)
            {
                var bf = billboard.GetComponent<BillboardFollow>();
                if (bf != null)
                {
                    var camField = bf.GetType().GetField("xrCamera");
                    if (camField != null) xrCamera = camField.GetValue(bf) as Transform;
                    else
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
        }

        // ensure skeleton and tip transform
        if (rightHandSkeleton == null) TryFindSkeleton();
        if (rightHandSkeleton != null && (rightHandSkeleton.Bones == null || rightHandSkeleton.Bones.Count == 0))
            return;

        if (rightHandSkeleton != null && tipTransform == null)
        {
            var bone = rightHandSkeleton.Bones.FirstOrDefault(b => b.Id == tipId);
            if (bone != null) tipTransform = bone.Transform;
        }

        if (xrCamera == null || tipTransform == null || billboard == null) return;

        // compute camera->finger ray
        Vector3 camPos = xrCamera.position;
        Vector3 fingerPos = tipTransform.position;
        Vector3 dir = fingerPos - camPos;
        if (dir.sqrMagnitude < 1e-6f) return;
        Vector3 dirNorm = dir.normalized;

        // compute intersection of camera ray with quad plane
        Transform quadT = billboard.transform;
        Plane quadPlane = new Plane(quadT.forward, quadT.position);
        float enter;
        bool intersects = quadPlane.Raycast(new Ray(camPos, dirNorm), out enter);
        if (!intersects)
        {
            if (holdLastMarkerWhenRayMisses && hasLastMarkerPosition)
            {
                UpdateMarkerScale(lastMarkerCameraDistance);
                return;
            }

            // no intersection: place marker halfway as fallback
            Vector3 fallbackPos = camPos + dirNorm * Mathf.Clamp(dir.magnitude * 0.5f, 0f, dir.magnitude);
            MoveMarkerTo(fallbackPos);
            lastMarkerCameraDistance = Vector3.Distance(camPos, fallbackPos);
            hasLastMarkerPosition = true;
            UpdateMarkerScale(lastMarkerCameraDistance);
            // update distances
            lastPixelDistance = Vector3.Distance(camPos, fallbackPos);
            lastCenterDistance = Vector3.Distance(camPos, quadT.position);
            return;
        }

        Vector3 hitPointOnQuad = camPos + dirNorm * enter;
        UpdateBillboardCoordinates(hitPointOnQuad, quadT);

        // compute distances
        lastPixelDistance = Vector3.Distance(camPos, hitPointOnQuad);
        lastCenterDistance = Vector3.Distance(camPos, quadT.position);

        // desired point in front of the visible color quad (towards camera)
        Vector3 desiredPoint = hitPointOnQuad - quadT.forward.normalized * GetEffectiveDistanceFromQuad();

        // project desiredPoint onto camera->finger line to ensure marker lies on that line
        float t = Vector3.Dot(desiredPoint - camPos, dirNorm);

        // clamp t so marker stays between camera and quad intersection if requested
        float tMin = 0f;
        float tMax = enter; // distance to quad along ray
        if (clampBetweenCameraAndQuad)
            t = Mathf.Clamp(t, tMin, tMax);

        Vector3 targetPos = camPos + dirNorm * t;

        // ensure marker is not placed behind camera
        if (Vector3.Dot(targetPos - camPos, xrCamera.forward) < 0f)
        {
            targetPos = camPos + xrCamera.forward.normalized * 0.01f;
        }

        // move marker (smoothed)
        MoveMarkerTo(targetPos);
        lastMarkerCameraDistance = Vector3.Distance(camPos, targetPos);
        hasLastMarkerPosition = true;
        UpdateMarkerScale(lastMarkerCameraDistance);

        // orient marker to face camera
        markerInstance.transform.rotation = Quaternion.LookRotation(markerInstance.transform.position - xrCamera.position, xrCamera.up);

    }

    void MoveMarkerTo(Vector3 targetPos)
    {
        if (smoothTime > 0f)
        {
            markerInstance.transform.position = Vector3.SmoothDamp(markerInstance.transform.position, targetPos, ref velocity, smoothTime);
        }
        else
        {
            markerInstance.transform.position = targetPos;
        }
    }

    public bool TryGetMarkerWorldPosition(out Vector3 markerWorldPosition)
    {
        markerWorldPosition = Vector3.zero;
        if (markerInstance == null || !hasLastMarkerPosition || !markerInstance.activeInHierarchy)
        {
            return false;
        }

        markerWorldPosition = markerInstance.transform.position;
        return true;
    }

    public bool TryGetCenteredBillboardCoordinates(out Vector2 centeredCoordinates)
    {
        centeredCoordinates = lastCenteredBillboardCoordinates;
        return hasLastBillboardCoordinates;
    }

    void UpdateBillboardCoordinates(Vector3 worldPoint, Transform quadTransform)
    {
        if (quadTransform == null)
        {
            return;
        }

        Vector3 localPoint = quadTransform.InverseTransformPoint(worldPoint);
        MeshFilter meshFilter = quadTransform.GetComponent<MeshFilter>();
        if (meshFilter != null && meshFilter.sharedMesh != null)
        {
            Bounds bounds = meshFilter.sharedMesh.bounds;
            float halfWidth = Mathf.Max(0.0001f, bounds.extents.x);
            float halfHeight = Mathf.Max(0.0001f, bounds.extents.y);
            lastCenteredBillboardCoordinates = new Vector2(
                (localPoint.x - bounds.center.x) / halfWidth,
                (localPoint.y - bounds.center.y) / halfHeight);
        }
        else
        {
            lastCenteredBillboardCoordinates = new Vector2(localPoint.x * 2f, localPoint.y * 2f);
        }

        lastCenteredBillboardCoordinates.x = Mathf.Clamp(lastCenteredBillboardCoordinates.x, -1f, 1f);
        lastCenteredBillboardCoordinates.y = Mathf.Clamp(lastCenteredBillboardCoordinates.y, -1f, 1f);
        hasLastBillboardCoordinates = true;
    }

    void UpdateMarkerScale(float distanceFromCamera)
    {
        if (markerInstance == null) return;

        float scale = markerScale;
        if (maintainApparentMarkerSize)
        {
            float factor = Mathf.Max(0.0001f, distanceFromCamera) / Mathf.Max(0.0001f, markerReferenceDistance);
            scale *= factor;
        }

        float minScale = Mathf.Max(0.0001f, minMarkerWorldScale);
        float maxScale = Mathf.Max(minScale, maxMarkerWorldScale);
        markerInstance.transform.localScale = Vector3.one * Mathf.Clamp(scale, minScale, maxScale);
    }

    float GetEffectiveDistanceFromQuad()
    {
        float offset = Mathf.Max(0f, distanceFromQuad);
        if (billboard != null)
        {
            BillboardFollow billboardFollow = billboard.GetComponent<BillboardFollow>();
            if (billboardFollow != null)
                offset = Mathf.Max(offset, billboardFollow.quadLayerGapMeters * 0.5f + Mathf.Max(0f, markerFrontGapMeters));
        }

        return offset;
    }
    

    void OnValidate()
    {
        distanceFromQuad = Mathf.Max(0f, distanceFromQuad);
        markerFrontGapMeters = Mathf.Max(0f, markerFrontGapMeters);
        markerScale = Mathf.Max(0.0001f, markerScale);
        markerReferenceDistance = Mathf.Max(0.0001f, markerReferenceDistance);
        minMarkerWorldScale = Mathf.Max(0.0001f, minMarkerWorldScale);
        maxMarkerWorldScale = Mathf.Max(minMarkerWorldScale, maxMarkerWorldScale);
        smoothTime = Mathf.Max(0f, smoothTime);
        if (markerInstance != null) UpdateMarkerScale(markerReferenceDistance);
    }

    void OnDestroy()
    {
        if (markerInstance != null) Destroy(markerInstance);
    }
}
