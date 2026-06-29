using System;
using System.Runtime.InteropServices;
using UnityEngine;

public class test_rm_depth_ahat : MonoBehaviour
{
    public GameObject quad_z;
    public Shader shader_z;
    public Texture2D colormap_z;
    public GameObject quad_ab;
    public Shader shader_ab;

    private Texture2D tex_z;
    private Texture2D tex_ab;
    private RenderTexture texr_z;
    private RenderTexture texr_ab;
    private Material mat_z;
    private Material mat_ab;

    private hl2ss.shared.source source_rm_depth_ahat;

    public static event Action<ushort[]> OnAhatDepthFrame;
    public static event Action<ushort[]> OnAhatDepthFrameSmoothed;
    public static bool HasLatestDepthFrame => latestDepthFrame != null && latestDepthWidth > 0 && latestDepthHeight > 0;

    [Header("Smoothing")]
    [Tooltip("Number of recent frames used for the rolling average. 1 disables smoothing.")]
    public int smoothingWindow = 5;

    [Tooltip("Depth values at or below this value are treated as invalid and ignored by the average.")]
    public ushort invalidDepthValue = 0;

    [Tooltip("If true, replace the displayed depth texture with the smoothed depth frame.")]
    public bool useSmoothedForDisplay = true;

    private ushort[][] depthBuffer = null;
    private int depthBufferIndex = 0;
    private int depthBufferCount = 0;
    private long[] depthSum = null;
    private int[] depthValidCount = null;
    private ushort[] averagedDepth = null;
    private ushort[] lastValidAveragedDepth = null;

    public static Matrix4x4 latestPoseAhat;
    public static bool hasPoseAhat = false;
    private static ushort[] latestDepthFrame;
    private static int latestDepthWidth;
    private static int latestDepthHeight;

    void Start()
    {
        var host = run_once.host_address;
        var port = hl2ss.stream_port.RM_DEPTH_AHAT;

        var configuration = new hl2ss.ulm.configuration_rm_depth_ahat();

        using var calibration_handle = hl2ss.svc.download_calibration(host, port, configuration);
        var calibration = Marshal.PtrToStructure<hl2ss.calibration_rm_depth_ahat>(calibration_handle.data);

        hl2ss.svc.open_stream(host, port, 450, configuration, true, out source_rm_depth_ahat);

        tex_z = new Texture2D(hl2ss.parameters_rm_depth_ahat.WIDTH, hl2ss.parameters_rm_depth_ahat.HEIGHT, TextureFormat.R16, false);
        tex_ab = new Texture2D(hl2ss.parameters_rm_depth_ahat.WIDTH, hl2ss.parameters_rm_depth_ahat.HEIGHT, TextureFormat.R16, false);

        texr_z = new RenderTexture(hl2ss.parameters_rm_depth_ahat.WIDTH, hl2ss.parameters_rm_depth_ahat.HEIGHT, 0, RenderTextureFormat.BGRA32);
        texr_ab = new RenderTexture(hl2ss.parameters_rm_depth_ahat.WIDTH, hl2ss.parameters_rm_depth_ahat.HEIGHT, 0, RenderTextureFormat.BGRA32);

        quad_z.GetComponent<Renderer>().material.mainTexture = texr_z;
        quad_ab.GetComponent<Renderer>().material.mainTexture = texr_ab;

        mat_z = new Material(shader_z);
        mat_ab = new Material(shader_ab);

        mat_z.SetTexture("_ColorMapTex", colormap_z);
        mat_z.SetFloat("_Lf", 0.0f / 65535.0f);
        mat_z.SetFloat("_Rf", 1055.0f / 65535.0f);
    }

    void Update()
    {
        var packet = source_rm_depth_ahat.get_by_index(-1);
        if (packet.status != hl2ss.mt.status.OK) return;

        packet.unpack(out hl2ss.map_rm_depth_ahat region);

        var poseRaw = Marshal.PtrToStructure<hl2ss.matrix_4x4>(packet.pose);
        latestPoseAhat = ToUnityMatrix(poseRaw);
        hasPoseAhat = true;

        tex_z.LoadRawTextureData(region.depth, (int)hl2ss.parameters_rm_depth_ahat.PIXELS * sizeof(ushort));
        tex_z.Apply();

        tex_ab.LoadRawTextureData(region.ab, (int)hl2ss.parameters_rm_depth_ahat.PIXELS * sizeof(ushort));
        tex_ab.Apply();

        Graphics.Blit(tex_z, texr_z, mat_z);
        Graphics.Blit(tex_ab, texr_ab, mat_ab);

        int pixelCount = (int)hl2ss.parameters_rm_depth_ahat.PIXELS;
        int byteCount = pixelCount * sizeof(ushort);

        byte[] raw = new byte[byteCount];
        Marshal.Copy(region.depth, raw, 0, byteCount);

        ushort[] depth = new ushort[pixelCount];
        Buffer.BlockCopy(raw, 0, depth, 0, byteCount);

        ushort[] averaged = UpdateSmoothingAndGetAverage(depth, pixelCount);
        PublishLatestDepthFrame(averaged != null ? averaged : depth);

        OnAhatDepthFrame?.Invoke(depth);

        if (averaged != null)
        {
            OnAhatDepthFrameSmoothed?.Invoke(averaged);

            if (useSmoothedForDisplay)
            {
                byte[] avgRaw = new byte[byteCount];
                Buffer.BlockCopy(averaged, 0, avgRaw, 0, byteCount);
                tex_z.LoadRawTextureData(avgRaw);
                tex_z.Apply();
                Graphics.Blit(tex_z, texr_z, mat_z);
            }
        }
    }

    private ushort[] UpdateSmoothingAndGetAverage(ushort[] frame, int pixelCount)
    {
        if (smoothingWindow <= 1)
        {
            return frame;
        }

        if (depthBuffer == null ||
            depthBuffer.Length != smoothingWindow ||
            depthBuffer[0] == null ||
            depthBuffer[0].Length != pixelCount ||
            depthSum == null ||
            depthSum.Length != pixelCount ||
            depthValidCount == null ||
            depthValidCount.Length != pixelCount ||
            averagedDepth == null ||
            averagedDepth.Length != pixelCount ||
            lastValidAveragedDepth == null ||
            lastValidAveragedDepth.Length != pixelCount)
        {
            depthBuffer = new ushort[smoothingWindow][];
            for (int i = 0; i < smoothingWindow; ++i) depthBuffer[i] = new ushort[pixelCount];
            depthSum = new long[pixelCount];
            depthValidCount = new int[pixelCount];
            averagedDepth = new ushort[pixelCount];
            lastValidAveragedDepth = new ushort[pixelCount];
            depthBufferIndex = 0;
            depthBufferCount = 0;
        }

        if (depthBufferCount < smoothingWindow)
        {
            AddFrameToBuffer(frame, depthBuffer[depthBufferIndex], pixelCount);
            depthBufferCount++;
        }
        else
        {
            ReplaceOldestFrame(frame, depthBuffer[depthBufferIndex], pixelCount);
        }

        depthBufferIndex = (depthBufferIndex + 1) % smoothingWindow;
        WriteAveragedDepth(pixelCount);
        return averagedDepth;
    }

    private void AddFrameToBuffer(ushort[] frame, ushort[] bufferSlot, int pixelCount)
    {
        for (int i = 0; i < pixelCount; ++i)
        {
            ushort value = frame[i];
            bufferSlot[i] = value;

            if (IsValidDepth(value))
            {
                depthSum[i] += value;
                depthValidCount[i]++;
                lastValidAveragedDepth[i] = value;
            }
        }
    }

    private void ReplaceOldestFrame(ushort[] frame, ushort[] oldestFrame, int pixelCount)
    {
        for (int i = 0; i < pixelCount; ++i)
        {
            ushort oldValue = oldestFrame[i];
            if (IsValidDepth(oldValue))
            {
                depthSum[i] -= oldValue;
                depthValidCount[i]--;
            }

            ushort newValue = frame[i];
            oldestFrame[i] = newValue;

            if (IsValidDepth(newValue))
            {
                depthSum[i] += newValue;
                depthValidCount[i]++;
                lastValidAveragedDepth[i] = newValue;
            }
        }
    }

    private void WriteAveragedDepth(int pixelCount)
    {
        for (int i = 0; i < pixelCount; ++i)
        {
            int validCount = depthValidCount[i];
            if (validCount <= 0)
            {
                ushort fallback = lastValidAveragedDepth[i];
                averagedDepth[i] = IsValidDepth(fallback) ? fallback : (ushort)0;
                continue;
            }

            long value = (depthSum[i] + validCount / 2L) / validCount;
            if (value < 0) value = 0;
            if (value > ushort.MaxValue) value = ushort.MaxValue;
            averagedDepth[i] = (ushort)value;
            if (IsValidDepth(averagedDepth[i]))
            {
                lastValidAveragedDepth[i] = averagedDepth[i];
            }
        }
    }

    private bool IsValidDepth(ushort value)
    {
        return value > invalidDepthValue;
    }

    private void PublishLatestDepthFrame(ushort[] frame)
    {
        latestDepthFrame = frame;
        latestDepthWidth = hl2ss.parameters_rm_depth_ahat.WIDTH;
        latestDepthHeight = hl2ss.parameters_rm_depth_ahat.HEIGHT;
    }

    public static bool TrySampleLatestDepthMeters(Vector2 uv, out float depthMeters, out ushort depthMillimeters)
    {
        depthMeters = 0f;
        depthMillimeters = 0;

        if (!HasLatestDepthFrame)
        {
            return false;
        }

        int x = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(uv.x) * (latestDepthWidth - 1)), 0, latestDepthWidth - 1);
        int y = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(uv.y) * (latestDepthHeight - 1)), 0, latestDepthHeight - 1);
        int index = y * latestDepthWidth + x;

        if (index < 0 || index >= latestDepthFrame.Length)
        {
            return false;
        }

        depthMillimeters = latestDepthFrame[index];
        if (depthMillimeters == 0)
        {
            return false;
        }

        depthMeters = depthMillimeters * 0.001f;
        return true;
    }

    void OnValidate()
    {
        smoothingWindow = Mathf.Clamp(smoothingWindow, 1, 30);
    }

    static Matrix4x4 ToUnityMatrix(hl2ss.matrix_4x4 m)
    {
        Matrix4x4 r = new Matrix4x4();
        r.m00 = m.m_00; r.m01 = m.m_01; r.m02 = m.m_02; r.m03 = m.m_03;
        r.m10 = m.m_10; r.m11 = m.m_11; r.m12 = m.m_12; r.m13 = m.m_13;
        r.m20 = m.m_20; r.m21 = m.m_21; r.m22 = m.m_22; r.m23 = m.m_23;
        r.m30 = m.m_30; r.m31 = m.m_31; r.m32 = m.m_32; r.m33 = m.m_33;
        return r;
    }

    static string MatrixToString(Matrix4x4 m)
    {
        return
            $"{m.m00:F4} {m.m01:F4} {m.m02:F4} {m.m03:F4}\n" +
            $"{m.m10:F4} {m.m11:F4} {m.m12:F4} {m.m13:F4}\n" +
            $"{m.m20:F4} {m.m21:F4} {m.m22:F4} {m.m23:F4}\n" +
            $"{m.m30:F4} {m.m31:F4} {m.m32:F4} {m.m33:F4}";
    }
}
