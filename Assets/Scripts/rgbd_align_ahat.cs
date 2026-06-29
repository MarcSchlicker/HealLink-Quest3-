using System;
using System.Collections;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Simplified AHAT RGB-D pipeline:
/// - Reads AHAT raw data and unpacks it into canonical 512x512 U16 depth.
/// - Scales the canonical depth view to PV resolution for display.
/// - Keeps the aligner fields and buffers available for later alignment work.
/// - Publishes the latest aligned depth frame so drawing can skip invalid zero pixels.
/// </summary>
[DisallowMultipleComponent]
public class rgbd_align_ahat : MonoBehaviour
{
    [Header("Output quads")]
    public GameObject rgb_image;
    public GameObject d_image;
    public GameObject depth_raw_quad;
    public GameObject debugCanonicalQuad;

    [Header("Colormap")]
    public Texture colormap_texture;
    public Shader colormap_shader;
    public float depthMin = 0.0f;
    public float depthMax = 1.5f;

    [Header("Behavior")]
    public int align_algorithm = 0;
    public bool enableDebugRange = true;
    public bool logAhatRawInfo = false;
    public bool autoReconnectOnInvalidHandle = true;
    public int copyRetries = 2;
    public int copyRetryDelayMs = 30;
    [Tooltip("Skip repeated reads of the same AHAT packet. This prevents duplicate depth processing when Unity renders faster than the sensor updates.")]
    public bool processOnlyNewAhatFrames = true;

    [Header("Depth smoothing")]
    [Tooltip("Number of recent aligned depth frames used for the rolling average. 1 disables smoothing.")]
    public int smoothingWindow = 5;
    [Tooltip("Depth values at or below this value are treated as invalid and ignored by smoothing and drawing.")]
    public float invalidDepthMeters = 0f;
    [Tooltip("If true, display and publish the smoothed aligned depth frame.")]
    public bool useSmoothedDepthForDisplay = true;

    [Header("Long Throw fallback")]
    [Tooltip("Align Long Throw depth directly to the existing PV frame and use it only where the current AHAT average is empty.")]
    public bool useLongThrowForMissingDepth = false;
    [Tooltip("HL2SS Long Throw alignment algorithm. 1 generally fills projected surfaces more densely.")]
    public int longThrowAlignAlgorithm = 1;
    [Tooltip("Largest accepted Long Throw fallback depth in meters.")]
    public float longThrowMaximumDepthMeters = 8f;
    [Tooltip("Minimum time between Long Throw alignments. The sensor normally updates at about 5 FPS.")]
    public float longThrowUpdateIntervalSeconds = 0.033f;

    [Header("Spatial depth completion")]
    [Tooltip("Number of robust neighbor passes used to fill holes that have no AHAT, Long Throw, or historical measurement.")]
    public int neighborHoleFillIterations = 0;
    [Tooltip("Minimum valid neighbors required before an empty pixel is filled.")]
    public int minimumHoleFillNeighbors = 3;
    [Tooltip("Neighbor depths farther apart than this are not blended across an edge.")]
    public float neighborDepthAgreementMeters = 0.12f;
    [Tooltip("Apply a small edge-preserving smoothing pass after all holes have been completed.")]
    public bool useEdgePreservingSpatialSmoothing = true;
    [Tooltip("Maximum depth difference that receives substantial weight in the edge-preserving filter.")]
    public float spatialSmoothingDepthSigmaMeters = 0.06f;

    public static bool HasLatestAlignedDepthFrame => latestAlignedDepthFrame != null && latestAlignedDepthWidth > 0 && latestAlignedDepthHeight > 0;

    private const int PV_W = 640;
    private const int PV_H = 360;
    private const int AHAT_W = 512;
    private const int AHAT_H = 512;
    private const int AHAT_PIXELS = AHAT_W * AHAT_H;
    private const int AHAT_BYTE_COUNT = AHAT_PIXELS * sizeof(ushort);

    private string host;
    private hl2ss.ulm.configuration_pv configuration_pv;

    private hl2ss.calibration_rm_depth_ahat ahat_calibration;
    private Matrix4x4 ahat_extrinsics_inv;
    private hl2ss.calibration_rm_depth_longthrow longThrowCalibration;
    private Matrix4x4 longThrowExtrinsicsInv;
    private Matrix4x4 pv_extrinsics;

    private hl2ss.shared.source source_pv;
    private hl2ss.shared.source source_ahat;
    private hl2ss.shared.source sourceLongThrow;

    private hl2da.coprocessor.rgbd_align rgbd_aligner;
    private hl2da.coprocessor.rgbd_align longThrowAligner;
    private bool longThrowInitialized;
    private bool hasLongThrowAlignedFrame;
    private float nextLongThrowAlignmentTime;

    private Texture2D tex_rgb;
    private Texture2D tex_d;
    private RenderTexture tex_d_r;
    private Material colormap_mat;
    private Material depth_raw_mat;

    private byte[] ahatRawBytes;
    private ushort[] ahatRawUshorts;
    private ushort[] ahatCanonical;
    private float[] rawAlignedDepthFrame;
    private int[] resizeX0;
    private int[] resizeX1;
    private float[] resizeXWeight;
    private int[] resizeY0;
    private int[] resizeY1;
    private float[] resizeYWeight;

    private byte[] alignedRawBytes;
    private float[] alignedFloats;
    private float[] flippedFloats;
    private byte[] alignedFloatBytes;
    private float[] longThrowAlignedRaw;
    private float[] longThrowAlignedFlipped;

    private byte[] tmpRawRgb;
    private byte[] tmpFlippedRgb;

    private Texture2D debugCanonicalTex;
    private Color32[] debugCanonicalPixels;

    private float[][] depthSmoothingBuffer;
    private int depthSmoothingBufferIndex;
    private int depthSmoothingBufferCount;
    private double[] depthSmoothingSum;
    private int[] depthSmoothingValidCount;
    private float[] smoothedDepthFrame;
    private float[] lastValidSmoothedDepthFrame;
    private int[] depthHoleFillDistance;
    private float[] neighborHoleFillFrame;
    private float[] spatiallyFilteredDepthFrame;
    private float lastPublishedValidDepthMeters;

    private bool initialized = false;
    private bool reconnecting = false;
    private bool hasProcessedAhatTimestamp;
    private ulong lastProcessedAhatTimestamp;
    private bool hasLoggedAhatCopy;
    private bool hasLoggedAhatLayout;
    private bool hasLoggedCanonicalRange;

    private static float[] latestAlignedDepthFrame;
    private static float[] latestRawAlignedDepthFrame;
    private static int latestAlignedDepthWidth;
    private static int latestAlignedDepthHeight;

    void Start()
    {
        try
        {
            InitializeAll();
            initialized = true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"rgbd_align_ahat: Initialization failed: {ex.GetType().Name}: {ex.Message}");
            initialized = false;
        }
    }

    private void InitializeAll()
    {
        host = run_once.host_address;

        var configuration_subsystem = new hl2ss.ulm.configuration_pv_subsystem();
        configuration_pv = new hl2ss.ulm.configuration_pv();
        var configuration_ahat = new hl2ss.ulm.configuration_rm_depth_ahat();

        configuration_pv.width = PV_W;
        configuration_pv.height = PV_H;
        configuration_pv.framerate = 30;

        var decoded_format = hl2ss.pv_decoded_format.RGBA;

        using (var calibration_handle = hl2ss.svc.download_calibration(host, hl2ss.stream_port.RM_DEPTH_AHAT, configuration_ahat))
        {
            if (calibration_handle.data == IntPtr.Zero)
            {
                throw new ExternalException("download_calibration returned an invalid handle");
            }

            ahat_calibration = Marshal.PtrToStructure<hl2ss.calibration_rm_depth_ahat>(calibration_handle.data);
        }

        using (hl2ss.pointer p = hl2ss.pointer.get(ahat_calibration.extrinsics))
        {
            ahat_extrinsics_inv = Marshal.PtrToStructure<Matrix4x4>(p.value).inverse;
        }

        pv_extrinsics = Matrix4x4.identity;
        pv_extrinsics[1, 1] = -1;
        pv_extrinsics[2, 2] = -1;

        hl2ss.svc.start_subsystem_pv(host, hl2ss.stream_port.PERSONAL_VIDEO, configuration_subsystem);
        hl2ss.svc.open_stream(host, hl2ss.stream_port.PERSONAL_VIDEO, 300, configuration_pv, decoded_format, out source_pv);
        hl2ss.svc.open_stream(host, hl2ss.stream_port.RM_DEPTH_AHAT, 450, configuration_ahat, true, out source_ahat);

        if (source_pv == null || source_ahat == null)
        {
            throw new ExternalException("Failed to open one or more hl2ss streams.");
        }

        try
        {
            hl2da.coprocessor.RM_DepthInitializeRays(hl2da.SENSOR_ID.RM_DEPTH_AHAT, ahat_calibration.uv2xy);
            rgbd_aligner = hl2da.coprocessor.rgbd_align.create(hl2da.SENSOR_ID.RM_DEPTH_AHAT);
        }
        catch
        {
            rgbd_aligner = null;
            if (logAhatRawInfo)
            {
                Debug.LogWarning("rgbd_align_ahat: Could not create the RGB-D aligner. Display fallback remains available.");
            }
        }

        if (useLongThrowForMissingDepth)
        {
            TryInitializeLongThrow();
        }

        tex_rgb = new Texture2D(PV_W, PV_H, TextureFormat.RGBA32, false, true);
        tex_rgb.wrapMode = TextureWrapMode.Clamp;
        tex_rgb.filterMode = FilterMode.Bilinear;

        tex_d = new Texture2D(PV_W, PV_H, TextureFormat.RFloat, false, true);
        tex_d.wrapMode = TextureWrapMode.Clamp;
        tex_d.filterMode = FilterMode.Bilinear;

        tex_d_r = new RenderTexture(PV_W, PV_H, 0, RenderTextureFormat.BGRA32);
        tex_d_r.wrapMode = TextureWrapMode.Clamp;
        tex_d_r.filterMode = FilterMode.Bilinear;

        if (colormap_shader != null)
        {
            colormap_mat = new Material(colormap_shader);
            colormap_mat.SetTexture("_ColorMapTex", colormap_texture);
            colormap_mat.SetFloat("_Lf", depthMin);
            colormap_mat.SetFloat("_Rf", depthMax);
            colormap_mat.SetTexture("_MainTex", tex_d);
        }

        depth_raw_mat = new Material(Shader.Find("Unlit/Texture"));
        depth_raw_mat.mainTexture = tex_d;

        if (rgb_image != null) rgb_image.GetComponent<Renderer>().material.mainTexture = tex_rgb;
        if (d_image != null && colormap_mat != null) d_image.GetComponent<Renderer>().material.mainTexture = tex_d_r;
        if (depth_raw_quad != null) depth_raw_quad.GetComponent<Renderer>().material = depth_raw_mat;

        ahatRawBytes = new byte[AHAT_BYTE_COUNT];
        ahatRawUshorts = new ushort[AHAT_PIXELS];
        ahatCanonical = new ushort[AHAT_PIXELS];

        int count = PV_W * PV_H;
        rawAlignedDepthFrame = new float[count];
        BuildResizeMap(AHAT_W, AHAT_H, PV_W, PV_H);
        alignedRawBytes = new byte[count * sizeof(float)];
        alignedFloats = new float[count];
        flippedFloats = new float[count];
        alignedFloatBytes = new byte[count * sizeof(float)];
        longThrowAlignedRaw = new float[count];
        longThrowAlignedFlipped = new float[count];

        tmpRawRgb = new byte[count * 4];
        tmpFlippedRgb = new byte[count * 4];

        debugCanonicalTex = new Texture2D(AHAT_W, AHAT_H, TextureFormat.RGBA32, false);
        debugCanonicalPixels = new Color32[AHAT_PIXELS];
        if (debugCanonicalQuad != null) debugCanonicalQuad.GetComponent<Renderer>().material.mainTexture = debugCanonicalTex;

        Debug.Log("rgbd_align_ahat: Initialization complete.");
    }

    void Update()
    {
        if (!initialized)
        {
            if (autoReconnectOnInvalidHandle && !reconnecting)
            {
                StartCoroutine(ReconnectCoroutine());
            }
            return;
        }

        try
        {
            using var packet_ahat = source_ahat.get_by_index(-1);
            if (packet_ahat.status != hl2ss.mt.status.OK) return;
            if (processOnlyNewAhatFrames &&
                hasProcessedAhatTimestamp &&
                packet_ahat.timestamp == lastProcessedAhatTimestamp)
            {
                return;
            }

            using var packet_pv = source_pv.get_by_timestamp(packet_ahat.timestamp, hl2ss.mt.time_preference.PREFER_NEAREST, true);
            if (packet_pv.status != hl2ss.mt.status.OK) return;

            var pose_ahat = Marshal.PtrToStructure<Matrix4x4>(packet_ahat.pose);
            if (pose_ahat.m33 == 0.0f) return;

            var pose_pv = Marshal.PtrToStructure<Matrix4x4>(packet_pv.pose);
            if (pose_pv.m33 == 0.0f) return;

            var depth2camera = pv_extrinsics * pose_pv.inverse * pose_ahat * ahat_extrinsics_inv;
            using hl2da.pointer p_depth2camera = hl2da.pointer.get(depth2camera);

            packet_ahat.unpack(out hl2ss.map_rm_depth_ahat region_ahat);
            packet_pv.unpack(out hl2ss.map_pv region_pv);

            if (region_ahat.depth == IntPtr.Zero)
            {
                if (logAhatRawInfo) Debug.LogWarning("rgbd_align_ahat: region_ahat.depth is IntPtr.Zero; skipping frame.");
                return;
            }

            int copied = CopyAhatDepthBytes(region_ahat.depth);
            if (copied == 0)
            {
                if (logAhatRawInfo) Debug.LogWarning("rgbd_align_ahat: Failed to copy AHAT raw bytes; skipping frame.");
                return;
            }

            if (logAhatRawInfo && !hasLoggedAhatCopy)
            {
                Debug.Log($"rgbd_align_ahat: Copied {copied} bytes from AHAT raw depth.");
                hasLoggedAhatCopy = true;
            }

            int totalUshorts = Mathf.Min(copied / sizeof(ushort), ahatRawUshorts.Length);
            Buffer.BlockCopy(ahatRawBytes, 0, ahatRawUshorts, 0, totalUshorts * sizeof(ushort));

            FillCanonicalDepth(ahatRawUshorts, totalUshorts);
            UpdateDebugCanonicalTextureFallback(ahatCanonical);

            ResizeCanonicalToPV(ahatCanonical, AHAT_W, AHAT_H, PV_W, PV_H, rawAlignedDepthFrame);
            int pvCount = PV_W * PV_H;
            float[] smoothedAlignedDepth = UpdateDepthSmoothingAndGetAverage(rawAlignedDepthFrame, pvCount);
            bool hasLongThrowFallback = TryUpdateLongThrowAlignedDepth(pose_pv, region_pv.metadata);
            if (hasLongThrowFallback)
            {
                MergeLongThrowIntoMissingDepth(smoothedAlignedDepth, longThrowAlignedFlipped, pvCount);
            }

            RestoreMissingDepthFromHistory(smoothedAlignedDepth, pvCount);
            if (neighborHoleFillIterations > 0)
            {
                FillMissingDepthFromNeighbors(smoothedAlignedDepth, PV_W, PV_H);
                FillMissingDepth(smoothedAlignedDepth, PV_W, PV_H);
            }
            ApplyEdgePreservingSpatialSmoothing(smoothedAlignedDepth, PV_W, PV_H);
            float[] depthForDisplay = useSmoothedDepthForDisplay && smoothedAlignedDepth != null ? smoothedAlignedDepth : rawAlignedDepthFrame;

            PublishLatestAlignedDepthFrame(depthForDisplay, rawAlignedDepthFrame, PV_W, PV_H);

            int pvByteCount = pvCount * sizeof(float);
            Buffer.BlockCopy(depthForDisplay, 0, alignedFloatBytes, 0, pvByteCount);
            tex_d.LoadRawTextureData(alignedFloatBytes);
            tex_d.Apply(false);

            if (colormap_mat != null)
            {
                colormap_mat.SetTexture("_MainTex", tex_d);
                colormap_mat.SetFloat("_Lf", depthMin);
                colormap_mat.SetFloat("_Rf", depthMax);
            }

            if (colormap_mat != null && d_image != null)
            {
                Graphics.Blit(tex_d, tex_d_r, colormap_mat);
            }

            int rgbByteCount = PV_W * PV_H * 4;
            Marshal.Copy(region_pv.image, tmpRawRgb, 0, rgbByteCount);
            FlipByteArrayVertical(tmpRawRgb, tmpFlippedRgb, PV_W, PV_H, 4);
            tex_rgb.LoadRawTextureData(tmpFlippedRgb);
            tex_rgb.Apply(false);

            lastProcessedAhatTimestamp = packet_ahat.timestamp;
            hasProcessedAhatTimestamp = true;
        }
        catch (ExternalException ex)
        {
            Debug.LogWarning($"rgbd_align_ahat: ExternalException in Update: {ex.Message}");
            if (autoReconnectOnInvalidHandle && !reconnecting) StartCoroutine(ReconnectCoroutine());
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"rgbd_align_ahat: Exception in Update: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private int CopyAhatDepthBytes(IntPtr depthPointer)
    {
        for (int attempt = 0; attempt < copyRetries; ++attempt)
        {
            try
            {
                Marshal.Copy(depthPointer, ahatRawBytes, 0, AHAT_BYTE_COUNT);
                return AHAT_BYTE_COUNT;
            }
            catch (Exception ex)
            {
                if (logAhatRawInfo)
                {
                    Debug.LogWarning($"rgbd_align_ahat: Marshal.Copy failed attempt {attempt + 1}: {ex.GetType().Name}: {ex.Message}");
                }

                if (attempt < copyRetries - 1)
                {
                    StartCoroutine(ShortDelay(copyRetryDelayMs));
                }
            }
        }

        return 0;
    }

    private void TryInitializeLongThrow()
    {
        longThrowInitialized = false;
        hasLongThrowAlignedFrame = false;
        nextLongThrowAlignmentTime = 0f;
        try
        {
            var configurationLongThrow = new hl2ss.ulm.configuration_rm_depth_longthrow();
            using (var calibrationHandle = hl2ss.svc.download_calibration(
                       host,
                       hl2ss.stream_port.RM_DEPTH_LONGTHROW,
                       configurationLongThrow))
            {
                if (calibrationHandle.data == IntPtr.Zero)
                {
                    throw new ExternalException("Long Throw calibration returned an invalid handle");
                }

                longThrowCalibration =
                    Marshal.PtrToStructure<hl2ss.calibration_rm_depth_longthrow>(calibrationHandle.data);
            }

            using (hl2ss.pointer calibrationPointer = hl2ss.pointer.get(longThrowCalibration.extrinsics))
            {
                longThrowExtrinsicsInv =
                    Marshal.PtrToStructure<Matrix4x4>(calibrationPointer.value).inverse;
            }

            hl2ss.svc.open_stream(
                host,
                hl2ss.stream_port.RM_DEPTH_LONGTHROW,
                50,
                configurationLongThrow,
                true,
                out sourceLongThrow);

            if (sourceLongThrow == null)
            {
                throw new ExternalException("Long Throw stream returned a null source");
            }

            hl2da.coprocessor.RM_DepthInitializeRays(
                hl2da.SENSOR_ID.RM_DEPTH_LONGTHROW,
                longThrowCalibration.uv2xy);
            longThrowAligner = hl2da.coprocessor.rgbd_align.create(
                hl2da.SENSOR_ID.RM_DEPTH_LONGTHROW);

            if (longThrowAligner == null)
            {
                throw new ExternalException("Could not create Long Throw RGB-D aligner");
            }

            longThrowInitialized = true;
            Debug.Log("rgbd_align_ahat: Long Throw depth fallback initialized without an additional PV display.");
        }
        catch (Exception ex)
        {
            try { sourceLongThrow?.Dispose(); } catch { }
            try { longThrowAligner?.Dispose(); } catch { }
            sourceLongThrow = null;
            longThrowAligner = null;
            Debug.LogWarning("rgbd_align_ahat: Long Throw fallback unavailable: " + ex.Message);
        }
    }

    private bool TryUpdateLongThrowAlignedDepth(Matrix4x4 posePv, IntPtr pvMetadataPointer)
    {
        if (!useLongThrowForMissingDepth ||
            !longThrowInitialized ||
            sourceLongThrow == null ||
            longThrowAligner == null ||
            pvMetadataPointer == IntPtr.Zero ||
            longThrowAlignedRaw == null ||
            longThrowAlignedFlipped == null)
        {
            return false;
        }

        if (Time.unscaledTime < nextLongThrowAlignmentTime)
        {
            return hasLongThrowAlignedFrame;
        }

        nextLongThrowAlignmentTime =
            Time.unscaledTime + Mathf.Max(0.01f, longThrowUpdateIntervalSeconds);

        try
        {
            using var packetLongThrow = sourceLongThrow.get_by_index(-1);
            if (packetLongThrow.status != hl2ss.mt.status.OK)
            {
                return false;
            }

            Matrix4x4 poseLongThrow = Marshal.PtrToStructure<Matrix4x4>(packetLongThrow.pose);
            if (poseLongThrow.m33 == 0f || posePv.m33 == 0f)
            {
                return false;
            }

            packetLongThrow.unpack(out hl2ss.map_rm_depth_longthrow regionLongThrow);
            if (regionLongThrow.depth == IntPtr.Zero)
            {
                return false;
            }

            hl2ss.pv_metadata pvMetadata =
                Marshal.PtrToStructure<hl2ss.pv_metadata>(pvMetadataPointer);
            float[] pvIntrinsics =
            {
                pvMetadata.f.x,
                pvMetadata.f.y,
                pvMetadata.c.x,
                pvMetadata.c.y
            };

            Matrix4x4 depthToCamera =
                pv_extrinsics *
                posePv.inverse *
                poseLongThrow *
                longThrowExtrinsicsInv;

            using hl2da.pointer depthToCameraPointer = hl2da.pointer.get(depthToCamera);
            float[,,] alignedLongThrow = longThrowAligner.align(
                longThrowAlignAlgorithm,
                regionLongThrow.depth,
                depthToCameraPointer.value,
                pvIntrinsics,
                PV_W,
                PV_H);

            using hl2da.pointer alignedLongThrowPointer = hl2da.pointer.get(alignedLongThrow);
            Marshal.Copy(alignedLongThrowPointer.value, longThrowAlignedRaw, 0, PV_W * PV_H);
            FlipFloatArrayVertical(longThrowAlignedRaw, longThrowAlignedFlipped, PV_W, PV_H);
            hasLongThrowAlignedFrame = true;
            return true;
        }
        catch (Exception ex)
        {
            if (logAhatRawInfo)
            {
                Debug.LogWarning("rgbd_align_ahat: Long Throw alignment failed for this frame: " + ex.Message);
            }

            return false;
        }
    }

    private void FillCanonicalDepth(ushort[] rawUshorts, int totalUshorts)
    {
        Array.Fill(ahatCanonical, ushort.MaxValue);

        if (totalUshorts == AHAT_PIXELS * 2)
        {
            for (int i = 0, di = 0; i + 1 < totalUshorts && di < AHAT_PIXELS; i += 2, ++di)
            {
                ahatCanonical[di] = rawUshorts[i];
            }

            LogAhatLayoutOnce("rgbd_align_ahat: Detected interleaved Depth+AB and extracted the depth channel.");
            return;
        }

        if (totalUshorts == AHAT_PIXELS)
        {
            Buffer.BlockCopy(rawUshorts, 0, ahatCanonical, 0, AHAT_PIXELS * sizeof(ushort));
            LogAhatLayoutOnce("rgbd_align_ahat: Single-channel 16-bit depth detected.");
            return;
        }

        int srcPixels = totalUshorts;
        int srcW = 0;
        int srcH = 0;
        int[] candidates = new int[] { AHAT_W, 320, 288, 256, 128 };
        foreach (int candidate in candidates)
        {
            if (srcPixels % candidate == 0)
            {
                srcW = candidate;
                srcH = srcPixels / candidate;
                break;
            }
        }

        if (srcW == 0)
        {
            srcW = Math.Min(srcPixels, AHAT_W);
            srcH = Math.Max(1, srcPixels / srcW);
        }

        int offsetX = (AHAT_W - srcW) / 2;
        int offsetY = (AHAT_H - srcH) / 2;
        for (int y = 0; y < srcH; ++y)
        {
            int srcRow = y * srcW;
            int dstRow = (y + offsetY) * AHAT_W + offsetX;
            if (dstRow < 0 || dstRow + srcW > ahatCanonical.Length) continue;
            Array.Copy(rawUshorts, srcRow, ahatCanonical, dstRow, Math.Min(srcW, srcPixels - srcRow));
        }

        LogAhatLayoutOnce(
            $"rgbd_align_ahat: Heuristic copy used srcW={srcW}, srcH={srcH}, offsetX={offsetX}, offsetY={offsetY}.");
    }

    private void LogAhatLayoutOnce(string message)
    {
        if (!logAhatRawInfo || hasLoggedAhatLayout)
        {
            return;
        }

        Debug.Log(message);
        hasLoggedAhatLayout = true;
    }

    private void BuildResizeMap(int srcW, int srcH, int dstW, int dstH)
    {
        resizeX0 = new int[dstW];
        resizeX1 = new int[dstW];
        resizeXWeight = new float[dstW];
        resizeY0 = new int[dstH];
        resizeY1 = new int[dstH];
        resizeYWeight = new float[dstH];

        float sx = (float)srcW / dstW;
        float sy = (float)srcH / dstH;

        for (int x = 0; x < dstW; x++)
        {
            float sourceX = (x + 0.5f) * sx - 0.5f;
            int x0 = Mathf.Clamp((int)Mathf.Floor(sourceX), 0, srcW - 1);
            resizeX0[x] = x0;
            resizeX1[x] = Mathf.Min(x0 + 1, srcW - 1);
            resizeXWeight[x] = sourceX - x0;
        }

        for (int y = 0; y < dstH; y++)
        {
            float sourceY = (y + 0.5f) * sy - 0.5f;
            int y0 = Mathf.Clamp((int)Mathf.Floor(sourceY), 0, srcH - 1);
            resizeY0[y] = y0;
            resizeY1[y] = Mathf.Min(y0 + 1, srcH - 1);
            resizeYWeight[y] = sourceY - y0;
        }
    }

    private void ResizeCanonicalToPV(
        ushort[] canonical,
        int srcW,
        int srcH,
        int dstW,
        int dstH,
        float[] output)
    {
        if (canonical == null ||
            output == null ||
            output.Length != dstW * dstH ||
            resizeX0 == null ||
            resizeX0.Length != dstW ||
            resizeY0 == null ||
            resizeY0.Length != dstH)
        {
            return;
        }

        for (int y = 0; y < dstH; ++y)
        {
            int y0 = resizeY0[y];
            int y1 = resizeY1[y];
            float wy = resizeYWeight[y];
            int row0 = y0 * srcW;
            int row1 = y1 * srcW;
            int outputRow = y * dstW;

            for (int x = 0; x < dstW; ++x)
            {
                int x0 = resizeX0[x];
                int x1 = resizeX1[x];
                float wx = resizeXWeight[x];

                ushort v00 = canonical[row0 + x0];
                ushort v10 = canonical[row0 + x1];
                ushort v01 = canonical[row1 + x0];
                ushort v11 = canonical[row1 + x1];

                float s00 = IsValidDepthMillimeters(v00) ? v00 : 0f;
                float s10 = IsValidDepthMillimeters(v10) ? v10 : 0f;
                float s01 = IsValidDepthMillimeters(v01) ? v01 : 0f;
                float s11 = IsValidDepthMillimeters(v11) ? v11 : 0f;

                float w00 = s00 > 0f ? (1f - wx) * (1f - wy) : 0f;
                float w10 = s10 > 0f ? wx * (1f - wy) : 0f;
                float w01 = s01 > 0f ? (1f - wx) * wy : 0f;
                float w11 = s11 > 0f ? wx * wy : 0f;

                float weightSum = w00 + w10 + w01 + w11;
                float valueMillimeters = weightSum > 0f
                    ? (s00 * w00 + s10 * w10 + s01 * w01 + s11 * w11) / weightSum
                    : 0f;

                output[outputRow + x] = valueMillimeters > 0f ? valueMillimeters * 0.001f : 0f;
            }
        }
    }

    private float[] UpdateDepthSmoothingAndGetAverage(float[] frame, int pixelCount)
    {
        EnsureDepthCompletionBuffers(pixelCount);

        if (smoothingWindow <= 1)
        {
            RememberValidDepth(frame, pixelCount);
            return frame;
        }

        if (depthSmoothingBuffer == null ||
            depthSmoothingBuffer.Length != smoothingWindow ||
            depthSmoothingBuffer[0] == null ||
            depthSmoothingBuffer[0].Length != pixelCount ||
            depthSmoothingSum == null ||
            depthSmoothingSum.Length != pixelCount ||
            depthSmoothingValidCount == null ||
            depthSmoothingValidCount.Length != pixelCount ||
            smoothedDepthFrame == null ||
            smoothedDepthFrame.Length != pixelCount)
        {
            depthSmoothingBuffer = new float[smoothingWindow][];
            for (int i = 0; i < smoothingWindow; ++i) depthSmoothingBuffer[i] = new float[pixelCount];
            depthSmoothingSum = new double[pixelCount];
            depthSmoothingValidCount = new int[pixelCount];
            smoothedDepthFrame = new float[pixelCount];
            depthSmoothingBufferIndex = 0;
            depthSmoothingBufferCount = 0;
        }

        if (depthSmoothingBufferCount < smoothingWindow)
        {
            AddDepthFrameToSmoothingBuffer(frame, depthSmoothingBuffer[depthSmoothingBufferIndex], pixelCount);
            depthSmoothingBufferCount++;
        }
        else
        {
            ReplaceOldestDepthFrame(frame, depthSmoothingBuffer[depthSmoothingBufferIndex], pixelCount);
        }

        depthSmoothingBufferIndex = (depthSmoothingBufferIndex + 1) % smoothingWindow;
        WriteSmoothedDepth(pixelCount);
        return smoothedDepthFrame;
    }

    private void EnsureDepthCompletionBuffers(int pixelCount)
    {
        if (lastValidSmoothedDepthFrame == null ||
            lastValidSmoothedDepthFrame.Length != pixelCount)
        {
            lastValidSmoothedDepthFrame = new float[pixelCount];
        }

        if (depthHoleFillDistance == null ||
            depthHoleFillDistance.Length != pixelCount)
        {
            depthHoleFillDistance = new int[pixelCount];
        }

        if (neighborHoleFillFrame == null ||
            neighborHoleFillFrame.Length != pixelCount)
        {
            neighborHoleFillFrame = new float[pixelCount];
        }

        if (spatiallyFilteredDepthFrame == null ||
            spatiallyFilteredDepthFrame.Length != pixelCount)
        {
            spatiallyFilteredDepthFrame = new float[pixelCount];
        }
    }

    private void RememberValidDepth(float[] depth, int pixelCount)
    {
        if (depth == null || lastValidSmoothedDepthFrame == null)
        {
            return;
        }

        int safePixelCount = Mathf.Min(pixelCount, depth.Length);
        for (int i = 0; i < safePixelCount; i++)
        {
            float value = depth[i];
            if (IsValidDepthMeters(value, invalidDepthMeters))
            {
                lastValidSmoothedDepthFrame[i] = value;
            }
        }
    }

    private void MergeLongThrowIntoMissingDepth(float[] ahatDepth, float[] longThrowDepth, int pixelCount)
    {
        if (ahatDepth == null || longThrowDepth == null)
        {
            return;
        }

        int safePixelCount = Mathf.Min(pixelCount, Mathf.Min(ahatDepth.Length, longThrowDepth.Length));
        float maximumDepth = Mathf.Max(0.01f, longThrowMaximumDepthMeters);
        for (int i = 0; i < safePixelCount; i++)
        {
            if (IsValidDepthMeters(ahatDepth[i], invalidDepthMeters))
            {
                continue;
            }

            float fallbackDepth = longThrowDepth[i];
            if (!IsValidDepthMeters(fallbackDepth, invalidDepthMeters) ||
                fallbackDepth > maximumDepth)
            {
                continue;
            }

            ahatDepth[i] = fallbackDepth;
            lastValidSmoothedDepthFrame[i] = fallbackDepth;
        }
    }

    private void AddDepthFrameToSmoothingBuffer(float[] frame, float[] bufferSlot, int pixelCount)
    {
        for (int i = 0; i < pixelCount; ++i)
        {
            float value = frame[i];
            bufferSlot[i] = value;

            if (IsValidDepthMeters(value, invalidDepthMeters))
            {
                depthSmoothingSum[i] += value;
                depthSmoothingValidCount[i]++;
                lastValidSmoothedDepthFrame[i] = value;
            }
        }
    }

    private void ReplaceOldestDepthFrame(float[] frame, float[] oldestFrame, int pixelCount)
    {
        for (int i = 0; i < pixelCount; ++i)
        {
            float oldValue = oldestFrame[i];
            if (IsValidDepthMeters(oldValue, invalidDepthMeters))
            {
                depthSmoothingSum[i] -= oldValue;
                depthSmoothingValidCount[i]--;
            }

            float newValue = frame[i];
            oldestFrame[i] = newValue;

            if (IsValidDepthMeters(newValue, invalidDepthMeters))
            {
                depthSmoothingSum[i] += newValue;
                depthSmoothingValidCount[i]++;
                lastValidSmoothedDepthFrame[i] = newValue;
            }
        }
    }

    private void WriteSmoothedDepth(int pixelCount)
    {
        for (int i = 0; i < pixelCount; ++i)
        {
            int validCount = depthSmoothingValidCount[i];
            if (validCount > 0)
            {
                float value = (float)(depthSmoothingSum[i] / validCount);
                smoothedDepthFrame[i] = value;
                lastValidSmoothedDepthFrame[i] = value;
                continue;
            }

            smoothedDepthFrame[i] = 0f;
        }
    }

    private void RestoreMissingDepthFromHistory(float[] depth, int pixelCount)
    {
        if (depth == null || lastValidSmoothedDepthFrame == null)
        {
            return;
        }

        int safePixelCount = Mathf.Min(
            pixelCount,
            Mathf.Min(depth.Length, lastValidSmoothedDepthFrame.Length));
        for (int i = 0; i < safePixelCount; i++)
        {
            if (IsValidDepthMeters(depth[i], invalidDepthMeters))
            {
                continue;
            }

            float historicalDepth = lastValidSmoothedDepthFrame[i];
            if (IsValidDepthMeters(historicalDepth, invalidDepthMeters))
            {
                depth[i] = historicalDepth;
            }
        }
    }

    private void FillMissingDepthFromNeighbors(float[] depthFrame, int width, int height)
    {
        int pixelCount = width * height;
        if (depthFrame == null ||
            depthFrame.Length != pixelCount ||
            neighborHoleFillFrame == null ||
            neighborHoleFillFrame.Length != pixelCount)
        {
            return;
        }

        int iterations = Mathf.Clamp(neighborHoleFillIterations, 0, 8);
        int requiredNeighbors = Mathf.Clamp(minimumHoleFillNeighbors, 1, 8);
        float agreement = Mathf.Max(0.001f, neighborDepthAgreementMeters);
        float[] neighbors = new float[8];

        for (int iteration = 0; iteration < iterations; iteration++)
        {
            Array.Copy(depthFrame, neighborHoleFillFrame, pixelCount);
            int filledCount = 0;

            for (int y = 0; y < height; y++)
            {
                int rowStart = y * width;
                for (int x = 0; x < width; x++)
                {
                    int index = rowStart + x;
                    if (IsValidDepthMeters(depthFrame[index], invalidDepthMeters))
                    {
                        continue;
                    }

                    int neighborCount = 0;
                    for (int offsetY = -1; offsetY <= 1; offsetY++)
                    {
                        int neighborY = y + offsetY;
                        if (neighborY < 0 || neighborY >= height)
                        {
                            continue;
                        }

                        for (int offsetX = -1; offsetX <= 1; offsetX++)
                        {
                            if (offsetX == 0 && offsetY == 0)
                            {
                                continue;
                            }

                            int neighborX = x + offsetX;
                            if (neighborX < 0 || neighborX >= width)
                            {
                                continue;
                            }

                            float value = depthFrame[neighborY * width + neighborX];
                            if (IsValidDepthMeters(value, invalidDepthMeters))
                            {
                                neighbors[neighborCount++] = value;
                            }
                        }
                    }

                    if (neighborCount < requiredNeighbors)
                    {
                        continue;
                    }

                    Array.Sort(neighbors, 0, neighborCount);
                    float median = neighbors[neighborCount / 2];
                    float sum = 0f;
                    int agreeingCount = 0;
                    for (int i = 0; i < neighborCount; i++)
                    {
                        if (Mathf.Abs(neighbors[i] - median) <= agreement)
                        {
                            sum += neighbors[i];
                            agreeingCount++;
                        }
                    }

                    if (agreeingCount < requiredNeighbors)
                    {
                        continue;
                    }

                    neighborHoleFillFrame[index] = sum / agreeingCount;
                    filledCount++;
                }
            }

            Array.Copy(neighborHoleFillFrame, depthFrame, pixelCount);
            if (filledCount == 0)
            {
                break;
            }
        }
    }

    private void ApplyEdgePreservingSpatialSmoothing(float[] depthFrame, int width, int height)
    {
        int pixelCount = width * height;
        if (!useEdgePreservingSpatialSmoothing ||
            depthFrame == null ||
            depthFrame.Length != pixelCount ||
            spatiallyFilteredDepthFrame == null ||
            spatiallyFilteredDepthFrame.Length != pixelCount)
        {
            return;
        }

        float sigma = Mathf.Max(0.001f, spatialSmoothingDepthSigmaMeters);
        float maximumDifference = sigma * 3f;
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * width;
            for (int x = 0; x < width; x++)
            {
                int index = rowStart + x;
                float centerDepth = depthFrame[index];
                if (!IsValidDepthMeters(centerDepth, invalidDepthMeters))
                {
                    spatiallyFilteredDepthFrame[index] = centerDepth;
                    continue;
                }

                float weightedSum = centerDepth;
                float weightSum = 1f;
                for (int offsetY = -1; offsetY <= 1; offsetY++)
                {
                    int neighborY = y + offsetY;
                    if (neighborY < 0 || neighborY >= height)
                    {
                        continue;
                    }

                    for (int offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        if (offsetX == 0 && offsetY == 0)
                        {
                            continue;
                        }

                        int neighborX = x + offsetX;
                        if (neighborX < 0 || neighborX >= width)
                        {
                            continue;
                        }

                        float neighborDepth = depthFrame[neighborY * width + neighborX];
                        if (!IsValidDepthMeters(neighborDepth, invalidDepthMeters))
                        {
                            continue;
                        }

                        float difference = Mathf.Abs(neighborDepth - centerDepth);
                        if (difference > maximumDifference)
                        {
                            continue;
                        }

                        float normalizedDifference = difference / sigma;
                        float rangeWeight = 1f / (1f + normalizedDifference * normalizedDifference * 4f);
                        float spatialWeight = offsetX == 0 || offsetY == 0 ? 0.75f : 0.5f;
                        float weight = rangeWeight * spatialWeight;
                        weightedSum += neighborDepth * weight;
                        weightSum += weight;
                    }
                }

                spatiallyFilteredDepthFrame[index] = weightedSum / weightSum;
            }
        }

        Array.Copy(spatiallyFilteredDepthFrame, depthFrame, pixelCount);
    }

    private void FillMissingDepth(float[] depthFrame, int width, int height)
    {
        int pixelCount = width * height;
        if (depthFrame == null ||
            depthFrame.Length != pixelCount ||
            depthHoleFillDistance == null ||
            depthHoleFillDistance.Length != pixelCount)
        {
            return;
        }

        const int unreachable = int.MaxValue / 4;
        bool hasValidDepth = false;
        for (int i = 0; i < pixelCount; i++)
        {
            float depth = depthFrame[i];
            if (IsValidDepthMeters(depth, invalidDepthMeters))
            {
                depthHoleFillDistance[i] = 0;
                lastValidSmoothedDepthFrame[i] = depth;
                lastPublishedValidDepthMeters = depth;
                hasValidDepth = true;
            }
            else
            {
                depthHoleFillDistance[i] = unreachable;
            }
        }

        if (!hasValidDepth)
        {
            if (!IsValidDepthMeters(lastPublishedValidDepthMeters, invalidDepthMeters))
            {
                return;
            }

            Array.Fill(depthFrame, lastPublishedValidDepthMeters);
            return;
        }

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * width;
            for (int x = 0; x < width; x++)
            {
                int index = rowStart + x;
                TryPropagateDepth(depthFrame, index, x > 0 ? index - 1 : -1);
                TryPropagateDepth(depthFrame, index, y > 0 ? index - width : -1);
            }
        }

        for (int y = height - 1; y >= 0; y--)
        {
            int rowStart = y * width;
            for (int x = width - 1; x >= 0; x--)
            {
                int index = rowStart + x;
                TryPropagateDepth(depthFrame, index, x + 1 < width ? index + 1 : -1);
                TryPropagateDepth(depthFrame, index, y + 1 < height ? index + width : -1);
            }
        }

        for (int i = 0; i < pixelCount; i++)
        {
            float depth = depthFrame[i];
            if (!IsValidDepthMeters(depth, invalidDepthMeters))
            {
                depth = lastPublishedValidDepthMeters;
                depthFrame[i] = depth;
            }
        }
    }

    private void TryPropagateDepth(float[] depthFrame, int targetIndex, int sourceIndex)
    {
        if (depthFrame == null ||
            sourceIndex < 0 ||
            targetIndex < 0 ||
            targetIndex >= depthHoleFillDistance.Length ||
            sourceIndex >= depthHoleFillDistance.Length ||
            targetIndex >= depthFrame.Length ||
            sourceIndex >= depthFrame.Length)
        {
            return;
        }

        int sourceDistance = depthHoleFillDistance[sourceIndex];
        if (sourceDistance >= int.MaxValue / 4 ||
            sourceDistance + 1 >= depthHoleFillDistance[targetIndex])
        {
            return;
        }

        float sourceDepth = depthFrame[sourceIndex];
        if (!IsValidDepthMeters(sourceDepth, invalidDepthMeters))
        {
            return;
        }

        depthHoleFillDistance[targetIndex] = sourceDistance + 1;
        depthFrame[targetIndex] = sourceDepth;
    }

    private static void PublishLatestAlignedDepthFrame(float[] depthFrame, float[] rawDepthFrame, int width, int height)
    {
        latestAlignedDepthFrame = depthFrame;
        latestRawAlignedDepthFrame = rawDepthFrame;
        latestAlignedDepthWidth = width;
        latestAlignedDepthHeight = height;
    }

    public static bool TrySampleLatestAlignedDepthMeters(Vector2 uv, out float depthMeters, out float rawDepthMeters)
    {
        depthMeters = 0f;
        rawDepthMeters = 0f;

        if (!HasLatestAlignedDepthFrame)
        {
            return false;
        }

        int x = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(uv.x) * (latestAlignedDepthWidth - 1)), 0, latestAlignedDepthWidth - 1);
        int y = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(uv.y) * (latestAlignedDepthHeight - 1)), 0, latestAlignedDepthHeight - 1);
        int index = y * latestAlignedDepthWidth + x;

        if (index < 0 || index >= latestAlignedDepthFrame.Length)
        {
            return false;
        }

        float sampledDepthMeters = latestAlignedDepthFrame[index];
        rawDepthMeters = latestRawAlignedDepthFrame != null && index < latestRawAlignedDepthFrame.Length
            ? latestRawAlignedDepthFrame[index]
            : sampledDepthMeters;

        if (!IsValidDepthMeters(rawDepthMeters, 0f) && IsValidDepthMeters(sampledDepthMeters, 0f))
        {
            rawDepthMeters = sampledDepthMeters;
        }

        depthMeters = sampledDepthMeters;
        return IsValidDepthMeters(depthMeters, 0f);
    }

    private void UpdateDebugCanonicalTextureFallback(ushort[] canonical)
    {
        if (debugCanonicalTex == null ||
            canonical == null ||
            debugCanonicalPixels == null ||
            debugCanonicalPixels.Length != canonical.Length)
        {
            return;
        }

        int validCount = 0;
        int min = int.MaxValue;
        int max = int.MinValue;
        for (int i = 0; i < canonical.Length; ++i)
        {
            int value = canonical[i];
            if (IsValidDepthMillimeters(value))
            {
                validCount++;
                if (value < min) min = value;
                if (value > max) max = value;
            }
        }

        if (validCount == 0)
        {
            min = 0;
            max = 1;
        }

        float range = Mathf.Max(1f, max - min);
        for (int i = 0; i < canonical.Length; ++i)
        {
            int value = canonical[i];
            byte luminance = IsValidDepthMillimeters(value)
                ? (byte)(Mathf.Clamp01((value - min) / range) * 255f)
                : (byte)0;
            debugCanonicalPixels[i] = new Color32(luminance, luminance, luminance, 255);
        }

        debugCanonicalTex.SetPixels32(debugCanonicalPixels);
        debugCanonicalTex.Apply(false);

        if (debugCanonicalQuad != null)
        {
            debugCanonicalQuad.GetComponent<Renderer>().material.mainTexture = debugCanonicalTex;
        }

        if (logAhatRawInfo && !hasLoggedCanonicalRange)
        {
            Debug.Log($"rgbd_align_ahat: Debug canonical visual validCount={validCount}, min={min}, max={max}.");
            hasLoggedCanonicalRange = true;
        }
    }

    private IEnumerator ShortDelay(int ms)
    {
        yield return new WaitForSeconds(ms / 1000f);
    }

    private IEnumerator ReconnectCoroutine()
    {
        reconnecting = true;
        Debug.Log("rgbd_align_ahat: ReconnectCoroutine starting...");

        try { source_ahat?.Dispose(); } catch { }
        try { source_pv?.Dispose(); } catch { }
        try { sourceLongThrow?.Dispose(); } catch { }
        try { rgbd_aligner?.Dispose(); } catch { }
        try { longThrowAligner?.Dispose(); } catch { }
        source_ahat = null;
        source_pv = null;
        sourceLongThrow = null;
        rgbd_aligner = null;
        longThrowAligner = null;
        longThrowInitialized = false;
        hasLongThrowAlignedFrame = false;
        hasProcessedAhatTimestamp = false;
        hasLoggedAhatCopy = false;
        hasLoggedAhatLayout = false;
        hasLoggedCanonicalRange = false;
        try { hl2ss.svc.stop_subsystem_pv(host, hl2ss.stream_port.PERSONAL_VIDEO); } catch { }

        yield return new WaitForSeconds(0.2f);

        try
        {
            var configuration_subsystem = new hl2ss.ulm.configuration_pv_subsystem();
            var configuration_ahat = new hl2ss.ulm.configuration_rm_depth_ahat();
            configuration_pv = new hl2ss.ulm.configuration_pv();
            configuration_pv.width = PV_W;
            configuration_pv.height = PV_H;
            configuration_pv.framerate = 30;

            hl2ss.svc.start_subsystem_pv(host, hl2ss.stream_port.PERSONAL_VIDEO, configuration_subsystem);
            hl2ss.svc.open_stream(host, hl2ss.stream_port.PERSONAL_VIDEO, 300, configuration_pv, hl2ss.pv_decoded_format.RGBA, out source_pv);
            hl2ss.svc.open_stream(host, hl2ss.stream_port.RM_DEPTH_AHAT, 450, configuration_ahat, true, out source_ahat);

            if (source_pv == null || source_ahat == null)
            {
                Debug.LogWarning("rgbd_align_ahat: Reconnect opened a null source.");
                reconnecting = false;
                yield break;
            }

            hl2da.coprocessor.RM_DepthInitializeRays(hl2da.SENSOR_ID.RM_DEPTH_AHAT, ahat_calibration.uv2xy);
            rgbd_aligner = hl2da.coprocessor.rgbd_align.create(hl2da.SENSOR_ID.RM_DEPTH_AHAT);
            if (useLongThrowForMissingDepth)
            {
                TryInitializeLongThrow();
            }

            Debug.Log("rgbd_align_ahat: Reconnect successful.");
            initialized = true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("rgbd_align_ahat: Reconnect failed: " + ex.Message);
            initialized = false;
        }
        finally
        {
            reconnecting = false;
        }
    }

    void OnApplicationQuit()
    {
        try { source_ahat?.Dispose(); } catch { }
        try { source_pv?.Dispose(); } catch { }
        try { sourceLongThrow?.Dispose(); } catch { }
        try { hl2ss.svc.stop_subsystem_pv(host, hl2ss.stream_port.PERSONAL_VIDEO); } catch { }
        try { rgbd_aligner?.Dispose(); } catch { }
        try { longThrowAligner?.Dispose(); } catch { }
    }

    void OnValidate()
    {
        smoothingWindow = Mathf.Clamp(smoothingWindow, 1, 30);
        invalidDepthMeters = Mathf.Max(0f, invalidDepthMeters);
        longThrowAlignAlgorithm = Mathf.Clamp(longThrowAlignAlgorithm, 0, 1);
        longThrowMaximumDepthMeters = Mathf.Max(0.01f, longThrowMaximumDepthMeters);
        longThrowUpdateIntervalSeconds = Mathf.Max(0.01f, longThrowUpdateIntervalSeconds);
        neighborHoleFillIterations = Mathf.Clamp(neighborHoleFillIterations, 0, 8);
        minimumHoleFillNeighbors = Mathf.Clamp(minimumHoleFillNeighbors, 1, 8);
        neighborDepthAgreementMeters = Mathf.Max(0.001f, neighborDepthAgreementMeters);
        spatialSmoothingDepthSigmaMeters = Mathf.Max(0.001f, spatialSmoothingDepthSigmaMeters);
        copyRetries = Mathf.Clamp(copyRetries, 1, 10);
        copyRetryDelayMs = Mathf.Clamp(copyRetryDelayMs, 0, 1000);
    }

    private static bool IsValidDepthMillimeters(int value)
    {
        return value > 0 && value != ushort.MaxValue;
    }

    private static bool IsValidDepthMeters(float value, float invalidThresholdMeters)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value) && value > invalidThresholdMeters;
    }

    private static void FlipFloatArrayVertical(float[] src, float[] dst, int width, int height)
    {
        if (src == null || dst == null) return;

        int rowLength = width;
        for (int y = 0; y < height; ++y)
        {
            int srcRow = y * rowLength;
            int dstRow = (height - 1 - y) * rowLength;
            Array.Copy(src, srcRow, dst, dstRow, rowLength);
        }
    }

    private static void FlipByteArrayVertical(byte[] src, byte[] dst, int width, int height, int pixelSizeBytes)
    {
        if (src == null || dst == null) return;

        int rowBytes = width * pixelSizeBytes;
        for (int y = 0; y < height; ++y)
        {
            int srcRow = y * rowBytes;
            int dstRow = (height - 1 - y) * rowBytes;
            Array.Copy(src, srcRow, dst, dstRow, rowBytes);
        }
    }
}
