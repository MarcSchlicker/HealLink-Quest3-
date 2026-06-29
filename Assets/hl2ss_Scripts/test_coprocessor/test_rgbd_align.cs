using System;
using System.Runtime.InteropServices;
using UnityEngine;

public class test_rgbd_align : MonoBehaviour
{
    [Header("Output quads")]
    public GameObject rgb_image;        // zeigt PV-RGB
    public GameObject d_image;          // zeigt Colormap-Depth
    public GameObject depth_raw_quad;   // NEU: zeigt echte Depth (tex_d)

    [Header("Colormap")]
    public int align_algorithm;
    public Texture colormap_texture;
    public Shader colormap_shader;

    private string host;
    hl2ss.ulm.configuration_pv configuration_pv;

    hl2ss.calibration_rm_depth_longthrow zlt_calibration;
    private Matrix4x4 zlt_extrinsics_inv;
    private Matrix4x4 pv_extrinsics;

    private hl2ss.shared.source source_pv;
    private hl2ss.shared.source source_zlt;

    private hl2da.coprocessor.rgbd_align rgbd_aligner;

    private Texture2D tex_rgb;
    private Texture2D tex_d;      // aligned Depth (float, Meter)
    private RenderTexture tex_d_r;
    private Material colormap_mat_lt;
    private Material depth_raw_mat; // NEU: Material für echte Depth

    // Reusable buffers to avoid per-frame allocations
    private float[] tmpRawDepth;
    private float[] tmpFlippedDepth;
    private byte[] tmpDepthBytes;
    private byte[] tmpRawRgb;
    private byte[] tmpFlippedRgb;

    void Start()
    {
        // Configure
        host = run_once.host_address;

        var configuration_subsystem = new hl2ss.ulm.configuration_pv_subsystem();
        configuration_pv = new hl2ss.ulm.configuration_pv();
        var configuration_zlt = new hl2ss.ulm.configuration_rm_depth_longthrow();

        configuration_pv.width = 640;
        configuration_pv.height = 360;
        configuration_pv.framerate = 30;

        var decoded_format = hl2ss.pv_decoded_format.RGBA;

        // Get calibration
        using var calibration_handle =
            hl2ss.svc.download_calibration(host, hl2ss.stream_port.RM_DEPTH_LONGTHROW, configuration_zlt);
        zlt_calibration =
            Marshal.PtrToStructure<hl2ss.calibration_rm_depth_longthrow>(calibration_handle.data);

        using hl2ss.pointer p = hl2ss.pointer.get(zlt_calibration.extrinsics);
        zlt_extrinsics_inv = Marshal.PtrToStructure<Matrix4x4>(p.value).inverse;

        pv_extrinsics = Matrix4x4.identity;
        pv_extrinsics[1, 1] = -1;
        pv_extrinsics[2, 2] = -1;

        // Start streams
        hl2ss.svc.start_subsystem_pv(host, hl2ss.stream_port.PERSONAL_VIDEO, configuration_subsystem);

        hl2ss.svc.open_stream(
            host,
            hl2ss.stream_port.PERSONAL_VIDEO,
            300,
            configuration_pv,
            decoded_format,
            out source_pv);

        hl2ss.svc.open_stream(
            host,
            hl2ss.stream_port.RM_DEPTH_LONGTHROW,
            50,
            configuration_zlt,
            true,
            out source_zlt);

        // Configure coprocessor
        hl2da.coprocessor.RM_DepthInitializeRays(
            hl2da.SENSOR_ID.RM_DEPTH_LONGTHROW,
            zlt_calibration.uv2xy);
        rgbd_aligner = hl2da.coprocessor.rgbd_align.create(
            hl2da.SENSOR_ID.RM_DEPTH_LONGTHROW);

        // Create textures
        int w = configuration_pv.width;
        int h = configuration_pv.height;
        tex_rgb = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex_d   = new Texture2D(w, h, TextureFormat.RFloat, false);
        tex_d_r = new RenderTexture(w, h, 0, RenderTextureFormat.BGRA32);

        // Colormap-Material
        colormap_mat_lt = new Material(colormap_shader);
        colormap_mat_lt.SetTexture("_ColorMapTex", colormap_texture);
        colormap_mat_lt.SetFloat("_Lf", 0.0f);
        colormap_mat_lt.SetFloat("_Rf", 7.5f);

        // NEU: Material für echte Depth (RFloat)
        depth_raw_mat = new Material(Shader.Find("Unlit/Texture"));
        depth_raw_mat.mainTexture = tex_d;

        // Assign to quads
        if (rgb_image != null)
            rgb_image.GetComponent<Renderer>().material.mainTexture = tex_rgb;

        if (d_image != null)
            d_image.GetComponent<Renderer>().material.mainTexture = tex_d_r;

        if (depth_raw_quad != null)
            depth_raw_quad.GetComponent<Renderer>().material = depth_raw_mat;

        // Allocate reusable buffers
        int pixelCount = w * h;
        tmpRawDepth = new float[pixelCount];
        tmpFlippedDepth = new float[pixelCount];
        tmpDepthBytes = new byte[pixelCount * sizeof(float)];
        tmpRawRgb = new byte[pixelCount * 4];
        tmpFlippedRgb = new byte[pixelCount * 4];
    }

    void Update()
    {
        // Get Depth and RGB data
        using var packet_zlt = source_zlt.get_by_index(-1);
        if (packet_zlt.status != hl2ss.mt.status.OK) { return; }

        using var packet_pv = source_pv.get_by_timestamp(
            packet_zlt.timestamp,
            hl2ss.mt.time_preference.PREFER_NEAREST,
            true);
        if (packet_pv.status != hl2ss.mt.status.OK) { return; }

        // Check poses are valid
        var pose_zlt = Marshal.PtrToStructure<Matrix4x4>(packet_zlt.pose);
        if (pose_zlt.m33 == 0.0f) { return; }

        var pose_pv = Marshal.PtrToStructure<Matrix4x4>(packet_pv.pose);
        if (pose_pv.m33 == 0.0f) { return; }

        // Compute Depth to RGB transform
        var depth2camera =
            pv_extrinsics *
            pose_pv.inverse *
            pose_zlt *
            zlt_extrinsics_inv;

        using hl2da.pointer p_depth2camera = hl2da.pointer.get(depth2camera);

        // Extract data pointers
        packet_zlt.unpack(out hl2ss.map_rm_depth_longthrow region_zlt);
        packet_pv.unpack(out hl2ss.map_pv region_pv);

        // Get RGB intrinsics
        var metadata_pv = Marshal.PtrToStructure<hl2ss.pv_metadata>(region_pv.metadata);
        var pv_k = new float[4]
        {
            metadata_pv.f.x,
            metadata_pv.f.y,
            metadata_pv.c.x,
            metadata_pv.c.y
        };

        // Align Depth (ergibt float-Depth in Metern, PV-Auflösung)
        var pv_z = rgbd_aligner.align(
            align_algorithm,
            region_zlt.depth,
            p_depth2camera.value,
            pv_k,
            configuration_pv.width,
            configuration_pv.height);

        using hl2da.pointer p_pv_z = hl2da.pointer.get(pv_z);

        int w = configuration_pv.width;
        int h = configuration_pv.height;
        int pixelCount = w * h;

        // --- Depth: copy raw floats into managed array, flip vertically, then load into tex_d ---
        try
        {
            // copy raw floats from native pointer into reusable buffer
            Marshal.Copy(p_pv_z.value, tmpRawDepth, 0, pixelCount);

            // flip vertical into tmpFlippedDepth
            FlipFloatArrayVertical(tmpRawDepth, tmpFlippedDepth, w, h);

            // convert float[] -> byte[] (little-endian) into tmpDepthBytes
            Buffer.BlockCopy(tmpFlippedDepth, 0, tmpDepthBytes, 0, tmpDepthBytes.Length);

            // load into texture
            tex_d.LoadRawTextureData(tmpDepthBytes);
            tex_d.Apply();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("test_rgbd_align: Depth flip/load failed: " + ex.Message);
            // fallback: try direct load (original behavior)
            try
            {
                tex_d.LoadRawTextureData(
                    p_pv_z.value,
                    configuration_pv.width * configuration_pv.height * sizeof(float));
                tex_d.Apply();
            }
            catch (Exception inner)
            {
                Debug.LogWarning("test_rgbd_align: Fallback direct depth load failed: " + inner.Message);
            }
        }

        // --- RGB: copy raw bytes, flip vertically, then load into tex_rgb ---
        try
        {
            int byteCount = w * h * 4;
            Marshal.Copy(region_pv.image, tmpRawRgb, 0, byteCount);

            FlipByteArrayVertical(tmpRawRgb, tmpFlippedRgb, w, h, 4);

            tex_rgb.LoadRawTextureData(tmpFlippedRgb);
            tex_rgb.Apply();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("test_rgbd_align: RGB flip/load failed: " + ex.Message);
            // fallback: direct load
            try
            {
                tex_rgb.LoadRawTextureData(
                    region_pv.image,
                    configuration_pv.width * configuration_pv.height * 4);
                tex_rgb.Apply();
            }
            catch (Exception inner)
            {
                Debug.LogWarning("test_rgbd_align: Fallback direct RGB load failed: " + inner.Message);
            }
        }

        // Colormap-Visualisierung (tex_d is already flipped)
        Graphics.Blit(tex_d, tex_d_r, colormap_mat_lt);
    }

    void OnApplicationQuit()
    {
        if (source_zlt != null) { source_zlt.Dispose(); }
        if (source_pv != null)
        {
            source_pv.Dispose();
            hl2ss.svc.stop_subsystem_pv(host, hl2ss.stream_port.PERSONAL_VIDEO);
        }
        if (rgbd_aligner != null) { rgbd_aligner.Dispose(); }
    }

    // --- Hilfsfunktionen ---

    // Flip float array vertically (row-major). src -> dst (both length width*height)
    private static void FlipFloatArrayVertical(float[] src, float[] dst, int width, int height)
    {
        if (src == null || dst == null) return;
        int rowLen = width;
        for (int y = 0; y < height; ++y)
        {
            int srcRow = y * rowLen;
            int dstRow = (height - 1 - y) * rowLen;
            Array.Copy(src, srcRow, dst, dstRow, rowLen);
        }
    }

    // Flip byte array vertically. src -> dst. pixelSizeBytes e.g. 4 for RGBA32
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
