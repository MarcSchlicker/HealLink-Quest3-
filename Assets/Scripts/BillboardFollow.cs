using UnityEngine;

/// <summary>
/// Billboard that faces the XR camera and can derive its center distance from a DepthDistance sample.
/// The center distance is clamped by minDistance/maxDistance and smoothed as a single depth value so
/// child quads keep their original camera angles through the parent transform.
/// </summary>
public class BillboardFollow : MonoBehaviour
{
    [Header("Camera")]
    [Tooltip("XR camera transform (e.g. CenterEyeAnchor). If null, the script will try to find one at Start.")]
    public Transform xrCamera;

    [Header("Distance")]
    [Tooltip("Current center distance of the billboard (meters).")]
    public float distance = 5f;

    [Tooltip("Minimum allowed center distance of the billboard (meters).")]
    public float minDistance = 0.3f;

    [Tooltip("Maximum allowed center distance of the billboard (meters).")]
    public float maxDistance = 15.7f;

    [Tooltip("If true, read depth values from depthSource and update distance automatically.")]
    public bool useDepthSource = true;

    [Tooltip("Optional DepthDistance component to read lastDepthMeters and lastHitPoint from.")]
    public DepthDistance depthSource;

    [Tooltip("Fallback distance (meters) when no depth sample is available.")]
    public float defaultDistance = 8f;

    [Tooltip("Keep the last valid distance when depth sampling briefly fails. This avoids jumps back to defaultDistance.")]
    public bool holdLastDistanceWhenDepthMissing = true;

    [Tooltip("Ignore tiny depth changes below this threshold (meters) to reduce jitter.")]
    public float depthDeadband = 0.01f;

    [Tooltip("Manual multiplier for measured depth before it drives the billboard. 1 keeps measured meters unchanged.")]
    public float depthInputScale = 1f;

    [Tooltip("Manual offset in meters added to measured depth before it drives the billboard.")]
    public float depthInputOffsetMeters = 0f;

    [Header("Flip")]
    public Vector3 flip = Vector3.one; // e.g. (1, -1, 1) to mirror Y

    [Header("Apparent size")]
    [Tooltip("If true: scale the billboard proportional to distance so apparent size stays constant.")]
    public bool maintainApparentSize = true;
    [Tooltip("Base distance in meters at which baseScale applies. Use 8 for the original 12.8 x 7.2 billboard size.")]
    public float baseDistance = 8f;
    [Tooltip("Smoothing time for scale changes in legacy mode (0 = immediate).")]
    public float scaleSmoothTime = 0.03f;

    [Header("Smoothing")]
    [Tooltip("Smoothing time for distance updates (0 = immediate). Position and scale are derived from the same smoothed distance.")]
    public float positionSmoothTime = 0.02f;

    [Tooltip("Slower smoothing specifically for depth-driven distance changes. Higher values make depth jumps less visible.")]
    public float distanceSmoothTime = 0.18f;

    [Tooltip("Maximum distance change speed in meters per second. Set <= 0 for no speed limit.")]
    public float maxDistanceChangePerSecond = 1.5f;

    [Header("Camera angle lock")]
    [Tooltip("If true, the billboard pose is rebuilt from the current camera every frame. Child quads keep their parent-relative angles.")]
    public bool lockCornersToCamera = true;

    [Header("Camera framing")]
    [Tooltip("Horizontal camera-space offset in meters at framingReferenceDistance. Positive values move the billboard right in view.")]
    public float horizontalOffsetMeters = 0f;

    [Tooltip("Vertical camera-space offset in meters at framingReferenceDistance. Positive values move the billboard up in view.")]
    public float verticalOffsetMeters = 0f;

    [Tooltip("Distance in meters at which the horizontal and vertical framing offsets are used as-is.")]
    public float framingReferenceDistance = 1f;

    [Header("Hand alignment calibration")]
    [Tooltip("Manual hand correction at the exact billboard center and one meter depth. X moves right, Y moves up.")]
    public Vector2 handCenterCorrectionAtOneMeter = Vector2.zero;

    [Tooltip("Additional hand correction at the billboard edges and one meter depth. The sign reverses across the center.")]
    public Vector2 handEdgeCorrectionAtOneMeter = new Vector2(0.06f, -0.06f);

    [Tooltip("Show a green marker at the exact neutral center used by the alignment formula.")]
    public bool showCalibrationCenterMarker = true;

    [Tooltip("Apparent marker diameter in meters at calibrationMarkerReferenceDistance.")]
    public float calibrationCenterMarkerSize = 0.012f;

    [Tooltip("Reference distance for the green marker size.")]
    public float calibrationMarkerReferenceDistance = 1f;

    [Tooltip("Distance in front of the color quad so the green marker remains visible.")]
    public float calibrationMarkerFrontOffset = 0.008f;

    [Header("Quad layering")]
    [Tooltip("Color quad that should render just in front of the depth quad. If null, a child named Quad_PV is used.")]
    public Transform colorQuad;

    [Tooltip("Depth quad that should stay just behind the color quad. If null, a child named rm_depth_ahat_z is used.")]
    public Transform depthQuad;

    [Tooltip("Child name used as color quad fallback.")]
    public string colorQuadName = "Quad_PV";

    [Tooltip("Child name used as depth quad fallback.")]
    public string depthQuadName = "rm_depth_ahat_z";

    [Tooltip("World-space gap between color and depth image in meters. Small values keep the RGB image barely in front.")]
    public float quadLayerGapMeters = 0.005f;

    [Tooltip("If false, parent Z scale stays constant so child depth offsets do not grow with distance.")]
    public bool scaleDepthAxisWithDistance = false;

    // internals
    private Vector3 baseScale;
    private Vector3 scaleVelocity;
    private Vector3 positionVelocity;
    private float smoothedDistance;
    private float distanceVelocity;
    private float originalDistance = 0f;
    private bool hasValidDistance = false;
    private GameObject calibrationCenterMarker;
    private Material calibrationCenterMarkerMaterial;

    void Start()
    {
        EnsureDistanceOrder();
        distance = ClampDistance(distance);
        defaultDistance = ClampDistance(defaultDistance);

        baseScale = transform.localScale;
        if (baseDistance <= 0f)
            baseDistance = 8f;

        originalDistance = Mathf.Max(0.01f, distance);
        smoothedDistance = distance;
        hasValidDistance = distance > 0f;

        if (xrCamera == null)
        {
            var found = GameObject.Find("CenterEyeAnchor");
            if (found != null) xrCamera = found.transform;
        }

        ResolveLayeredQuads();
        CreateCalibrationCenterMarker();
    }

    void LateUpdate()
    {
        if (useDepthSource && depthSource != null && xrCamera != null)
        {
            ApplyDepthSourceIfAvailable();
        }
        else
        {
            if (!useDepthSource && distance <= 0f)
                distance = defaultDistance;
        }

        if (xrCamera == null) return;

        float targetDistance = ClampDistance(distance);
        float poseDistance = targetDistance;

        if (lockCornersToCamera)
        {
            float smoothTime = Mathf.Max(0f, distanceSmoothTime);
            if (smoothTime > 0f)
            {
                float previousDistance = smoothedDistance;
                smoothedDistance = Mathf.SmoothDamp(smoothedDistance, targetDistance, ref distanceVelocity, smoothTime);

                if (maxDistanceChangePerSecond > 0f)
                {
                    float maxStep = maxDistanceChangePerSecond * Time.deltaTime;
                    smoothedDistance = Mathf.MoveTowards(previousDistance, smoothedDistance, maxStep);
                }
            }
            else
            {
                smoothedDistance = targetDistance;
            }

            poseDistance = ClampDistance(smoothedDistance);
            ApplyCameraLockedPose(poseDistance);
        }
        else
        {
            smoothedDistance = targetDistance;
            ApplyLegacyPose(poseDistance);
        }

        Vector3 targetScale = baseScale;
        if (maintainApparentSize)
        {
            float factor = poseDistance / Mathf.Max(0.0001f, baseDistance);
            targetScale = new Vector3(
                baseScale.x * factor,
                baseScale.y * factor,
                baseScale.z * (scaleDepthAxisWithDistance ? factor : 1f)
            );
        }

        targetScale = new Vector3(targetScale.x * flip.x, targetScale.y * flip.y, targetScale.z * flip.z);

        if (!lockCornersToCamera && scaleSmoothTime > 0f)
            transform.localScale = Vector3.SmoothDamp(transform.localScale, targetScale, ref scaleVelocity, scaleSmoothTime);
        else
            transform.localScale = targetScale;

        ApplyQuadLayering();
        UpdateCalibrationCenterMarker(poseDistance);
    }

    /// <summary>
    /// Compute and set the billboard center distance so that the camera->ray intersection with the billboard plane
    /// lies at the measured depthMeters along the ray. Uses the relation:
    ///   t = D / cos(theta)  with t == depthMeters  =>  D = depthMeters * cos(theta)
    /// where cos(theta) = dot(camera.forward, rayDir).
    /// </summary>
    void ApplyDepthSourceIfAvailable()
    {
        if (depthSource == null || xrCamera == null)
            return;

        float depthMeters = depthSource.lastDepthMeters;
        Vector3 hitPoint = depthSource.lastHitPoint;

        if (depthMeters <= 0f || hitPoint == Vector3.zero)
        {
            if (!holdLastDistanceWhenDepthMissing || !hasValidDistance)
                distance = defaultDistance;
            return;
        }

        Vector3 rayVec = hitPoint - xrCamera.position;
        float rayLen = rayVec.magnitude;
        if (rayLen < 1e-6f)
        {
            if (!holdLastDistanceWhenDepthMissing || !hasValidDistance)
                distance = defaultDistance;
            return;
        }

        Vector3 rayDir = rayVec / rayLen;
        float cos = Vector3.Dot(xrCamera.forward.normalized, rayDir);

        float minCos = 1e-4f;
        if (Mathf.Abs(cos) < minCos)
            cos = Mathf.Sign(cos) * minCos;

        float calibratedDepth = depthMeters * depthInputScale + depthInputOffsetMeters;
        if (calibratedDepth <= 0f)
            return;

        float desired = ClampDistance(calibratedDepth * cos);

        if (hasValidDistance && Mathf.Abs(desired - distance) < Mathf.Max(0f, depthDeadband))
            return;

        distance = desired;
        hasValidDistance = true;
    }

    void ApplyCameraLockedPose(float poseDistance)
    {
        Vector3 forward = xrCamera.forward;
        Vector3 up = xrCamera.up;
        if (forward.sqrMagnitude < 1e-8f || up.sqrMagnitude < 1e-8f)
            return;

        transform.SetPositionAndRotation(
            xrCamera.position + forward.normalized * poseDistance + GetCameraSpaceOffset(poseDistance),
            Quaternion.LookRotation(forward.normalized, up.normalized)
        );
    }

    void ApplyLegacyPose(float poseDistance)
    {
        Vector3 targetPos = xrCamera.position + xrCamera.forward * poseDistance + GetCameraSpaceOffset(poseDistance);
        if (positionSmoothTime > 0f)
            transform.position = Vector3.SmoothDamp(transform.position, targetPos, ref positionVelocity, positionSmoothTime);
        else
            transform.position = targetPos;

        Vector3 dir = (transform.position - xrCamera.position).normalized;
        transform.rotation = Quaternion.LookRotation(dir, xrCamera.up);
    }

    Vector3 GetCameraSpaceOffset(float poseDistance)
    {
        if (xrCamera == null) return Vector3.zero;

        float factor = poseDistance / Mathf.Max(0.0001f, framingReferenceDistance);

        return (xrCamera.right.normalized * horizontalOffsetMeters +
                xrCamera.up.normalized * verticalOffsetMeters) * factor;
    }

    public Vector2 GetHandCorrectionMeters(Vector2 centeredBillboardCoordinates, float depthMeters)
    {
        Vector2 clampedCoordinates = new Vector2(
            Mathf.Clamp(centeredBillboardCoordinates.x, -1f, 1f),
            Mathf.Clamp(centeredBillboardCoordinates.y, -1f, 1f));

        Vector2 edgeCorrection = Vector2.Scale(clampedCoordinates, handEdgeCorrectionAtOneMeter);
        return (handCenterCorrectionAtOneMeter + edgeCorrection) * Mathf.Max(0f, depthMeters);
    }

    /// <summary>
    /// External API to adjust the billboard center distance based on measured depth.
    /// Kept for compatibility with other scripts. The result is clamped by minDistance/maxDistance.
    /// </summary>
    public void ApplyDepthShift(float depthMeters, float pixelDistance, float limit)
    {
        float desired;
        if (Mathf.Approximately(depthMeters, 0f))
        {
            desired = originalDistance;
        }
        else
        {
            desired = originalDistance + (depthMeters - pixelDistance);
        }

        distance = ClampDistance(desired);
        hasValidDistance = true;
    }

    public void SetDepthSource(DepthDistance dd)
    {
        depthSource = dd;
    }

    void ResolveLayeredQuads()
    {
        if (colorQuad == null && !string.IsNullOrEmpty(colorQuadName))
            colorQuad = FindChildRecursive(transform, colorQuadName);

        if (depthQuad == null && !string.IsNullOrEmpty(depthQuadName))
            depthQuad = FindChildRecursive(transform, depthQuadName);
    }

    void ApplyQuadLayering()
    {
        ResolveLayeredQuads();
        if (colorQuad == null || depthQuad == null)
            return;

        float parentScaleZ = Mathf.Max(1e-6f, Mathf.Abs(transform.lossyScale.z));
        float halfGapLocal = Mathf.Max(0f, quadLayerGapMeters) * 0.5f / parentScaleZ;

        Vector3 colorLocal = colorQuad.localPosition;
        Vector3 depthLocal = depthQuad.localPosition;

        colorLocal.z = -halfGapLocal;
        depthLocal.z = halfGapLocal;

        colorQuad.localPosition = colorLocal;
        depthQuad.localPosition = depthLocal;
    }

    void CreateCalibrationCenterMarker()
    {
        if (calibrationCenterMarker != null)
            return;

        calibrationCenterMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        calibrationCenterMarker.name = "Billboard_CalibrationCenter";

        Collider markerCollider = calibrationCenterMarker.GetComponent<Collider>();
        if (markerCollider != null)
            Destroy(markerCollider);

        Renderer markerRenderer = calibrationCenterMarker.GetComponent<Renderer>();
        Shader markerShader = Resources.Load<Shader>("AlwaysOnTopColor");
        if (markerShader == null) markerShader = Shader.Find("Hidden/AlwaysOnTopColor");
        if (markerShader == null) markerShader = Shader.Find("Unlit/Color");

        calibrationCenterMarkerMaterial = new Material(markerShader);
        calibrationCenterMarkerMaterial.color = Color.green;
        calibrationCenterMarkerMaterial.renderQueue = 5000;
        markerRenderer.material = calibrationCenterMarkerMaterial;
        calibrationCenterMarker.SetActive(showCalibrationCenterMarker);
    }

    void UpdateCalibrationCenterMarker(float poseDistance)
    {
        if (calibrationCenterMarker == null)
            CreateCalibrationCenterMarker();

        if (calibrationCenterMarker == null)
            return;

        if (calibrationCenterMarker.activeSelf != showCalibrationCenterMarker)
            calibrationCenterMarker.SetActive(showCalibrationCenterMarker);

        if (!showCalibrationCenterMarker)
            return;

        float frontOffset = Mathf.Max(
            calibrationMarkerFrontOffset,
            quadLayerGapMeters * 0.5f + 0.001f);

        calibrationCenterMarker.transform.position =
            transform.position - transform.forward.normalized * frontOffset;
        calibrationCenterMarker.transform.rotation = transform.rotation;

        float sizeFactor = poseDistance / Mathf.Max(0.01f, calibrationMarkerReferenceDistance);
        float markerSize = Mathf.Max(0.0005f, calibrationCenterMarkerSize * sizeFactor);
        calibrationCenterMarker.transform.localScale = Vector3.one * markerSize;
    }

    Transform FindChildRecursive(Transform root, string childName)
    {
        if (root == null) return null;

        for (int i = 0; i < root.childCount; ++i)
        {
            Transform child = root.GetChild(i);
            if (child.name == childName)
                return child;

            Transform nested = FindChildRecursive(child, childName);
            if (nested != null)
                return nested;
        }

        return null;
    }

    float ClampDistance(float value)
    {
        EnsureDistanceOrder();
        return Mathf.Clamp(value, minDistance, maxDistance);
    }

    void EnsureDistanceOrder()
    {
        minDistance = Mathf.Max(0.01f, minDistance);
        maxDistance = Mathf.Max(minDistance, maxDistance);
    }

    void OnValidate()
    {
        EnsureDistanceOrder();
        distance = ClampDistance(distance);
        defaultDistance = ClampDistance(defaultDistance);
        depthDeadband = Mathf.Max(0f, depthDeadband);
        depthInputScale = Mathf.Clamp(depthInputScale, 0.01f, 10f);
        depthInputOffsetMeters = Mathf.Clamp(depthInputOffsetMeters, -10f, 10f);
        quadLayerGapMeters = Mathf.Max(0f, quadLayerGapMeters);
        if (baseDistance <= 0f) baseDistance = 8f;
        positionSmoothTime = Mathf.Max(0f, positionSmoothTime);
        distanceSmoothTime = Mathf.Max(0f, distanceSmoothTime);
        maxDistanceChangePerSecond = Mathf.Max(0f, maxDistanceChangePerSecond);
        scaleSmoothTime = Mathf.Max(0f, scaleSmoothTime);
        framingReferenceDistance = Mathf.Max(0.01f, framingReferenceDistance);
        handCenterCorrectionAtOneMeter.x = Mathf.Clamp(handCenterCorrectionAtOneMeter.x, -0.5f, 0.5f);
        handCenterCorrectionAtOneMeter.y = Mathf.Clamp(handCenterCorrectionAtOneMeter.y, -0.5f, 0.5f);
        handEdgeCorrectionAtOneMeter.x = Mathf.Clamp(handEdgeCorrectionAtOneMeter.x, -0.5f, 0.5f);
        handEdgeCorrectionAtOneMeter.y = Mathf.Clamp(handEdgeCorrectionAtOneMeter.y, -0.5f, 0.5f);
        calibrationCenterMarkerSize = Mathf.Max(0.0005f, calibrationCenterMarkerSize);
        calibrationMarkerReferenceDistance = Mathf.Max(0.01f, calibrationMarkerReferenceDistance);
        calibrationMarkerFrontOffset = Mathf.Max(0f, calibrationMarkerFrontOffset);
    }

    void OnDestroy()
    {
        if (calibrationCenterMarker != null)
            Destroy(calibrationCenterMarker);

        if (calibrationCenterMarkerMaterial != null)
            Destroy(calibrationCenterMarkerMaterial);
    }
}
