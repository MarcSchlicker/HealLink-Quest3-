using System;
using System.Runtime.InteropServices;
using UnityEngine;

public class test_rm_depth_longthrow : MonoBehaviour
{
    public GameObject quad_z;
    public Shader shader_z;
    public Texture2D colormap_z;

    private Texture2D tex_z;
    private RenderTexture texr_z;
    private Material mat_z;

    private hl2ss.shared.source source_rm_depth_longthrow;

    // Live-Event
    public static event Action<ushort[]> OnLongThrowDepthFrame;

    // NEW: Pose export
    public static Matrix4x4 latestPoseLong;
    public static bool hasPoseLong = false;

    void Start()
    {
        var host = run_once.host_address;
        var port = hl2ss.stream_port.RM_DEPTH_LONGTHROW;

        var configuration = new hl2ss.ulm.configuration_rm_depth_longthrow();

        using var calibration_handle = hl2ss.svc.download_calibration(host, port, configuration);
        var calibration = Marshal.PtrToStructure<hl2ss.calibration_rm_depth_longthrow>(calibration_handle.data);

        hl2ss.svc.open_stream(host, port, 450, configuration, true, out source_rm_depth_longthrow);

        tex_z  = new Texture2D(hl2ss.parameters_rm_depth_longthrow.WIDTH, hl2ss.parameters_rm_depth_longthrow.HEIGHT, TextureFormat.R16, false);
        texr_z = new RenderTexture(hl2ss.parameters_rm_depth_longthrow.WIDTH, hl2ss.parameters_rm_depth_longthrow.HEIGHT, 0, RenderTextureFormat.BGRA32);

        quad_z.GetComponent<Renderer>().material.mainTexture = texr_z;

        mat_z = new Material(shader_z);
        mat_z.SetTexture("_ColorMapTex", colormap_z);
        mat_z.SetFloat("_Lf", 0.0f / 65535.0f);
        mat_z.SetFloat("_Rf", 8000.0f / 65535.0f);
    }

    void Update()
    {
        var packet = source_rm_depth_longthrow.get_by_index(-1);
        if (packet.status != hl2ss.mt.status.OK) return;

        packet.unpack(out hl2ss.map_rm_depth_longthrow region);

        // --- NEW: Pose extraction ---
        var poseRaw = Marshal.PtrToStructure<hl2ss.matrix_4x4>(packet.pose);
        latestPoseLong = ToUnityMatrix(poseRaw);
        hasPoseLong = true;

        //Debug.Log("LongThrow Pose:\n" + MatrixToString(latestPoseLong));

        // --- existing depth logic ---
        tex_z.LoadRawTextureData(region.depth, (int)hl2ss.parameters_rm_depth_longthrow.PIXELS * sizeof(ushort));
        tex_z.Apply();
        Graphics.Blit(tex_z, texr_z, mat_z);

        int pixelCount = (int)hl2ss.parameters_rm_depth_longthrow.PIXELS;
        int byteCount  = pixelCount * sizeof(ushort);

        byte[] raw = new byte[byteCount];
        Marshal.Copy(region.depth, raw, 0, byteCount);

        ushort[] depth = new ushort[pixelCount];
        Buffer.BlockCopy(raw, 0, depth, 0, byteCount);

        OnLongThrowDepthFrame?.Invoke(depth);
    }

    // --- helper functions ---
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
