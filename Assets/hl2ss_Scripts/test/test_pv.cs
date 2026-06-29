using System.Runtime.InteropServices;
using UnityEngine;

public class test_pv : MonoBehaviour
{
    public GameObject quad_pv;

    private string host;
    private hl2ss.shared.source source_pv;
    private int pv_frame_size;
    private Texture2D tex_pv;
    private TextureFormat texture_format;
    private int bpp;

    // global: aktuelles PV-Bild + Pose
    public static Texture2D latestTexture;
    public static Matrix4x4 latestPosePV;
    public static bool hasPosePV = false;

    void Start()
    {
        host = run_once.host_address;

        var configuration = new hl2ss.ulm.configuration_pv();
        configuration.width = 640;
        configuration.height = 360;
        configuration.framerate = 30;

        var decoded_format = hl2ss.pv_decoded_format.RGB;
        texture_format = TextureFormat.RGB24;
        bpp = 3;

        var configuration_subsystem = new hl2ss.ulm.configuration_pv_subsystem();
        hl2ss.svc.start_subsystem_pv(host, hl2ss.stream_port.PERSONAL_VIDEO, configuration_subsystem);

        hl2ss.svc.open_stream(host, hl2ss.stream_port.PERSONAL_VIDEO, 300,
            configuration, decoded_format, out source_pv);
    }

    void Update()
    {
        using var packet = source_pv.get_by_index(-1);
        if (packet.status != hl2ss.mt.status.OK) return;

        packet.unpack(out hl2ss.map_pv region);

        var metadata = Marshal.PtrToStructure<hl2ss.pv_metadata>(region.metadata);
        var pose     = Marshal.PtrToStructure<hl2ss.matrix_4x4>(packet.pose);

        if (!tex_pv)
        {
            pv_frame_size = metadata.width * metadata.height * bpp;
            tex_pv = new Texture2D(metadata.width, metadata.height, texture_format, false);
            quad_pv.GetComponent<Renderer>().material.mainTexture = tex_pv;
        }

        tex_pv.LoadRawTextureData(region.image, pv_frame_size);
        tex_pv.Apply();

        latestTexture = tex_pv;

        // Pose in Unity-Matrix speichern
        latestPosePV = ToUnityMatrix(pose);
        hasPosePV = true;

        // einmalig (oder gelegentlich) loggen
        //Debug.Log($"PV Pose:\n{MatrixToString(latestPosePV)}");
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

    void OnApplicationQuit()
    {
        if (source_pv == null) return;

        source_pv.Dispose();
        hl2ss.svc.stop_subsystem_pv(host, hl2ss.stream_port.PERSONAL_VIDEO);
    }
}
