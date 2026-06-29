using UnityEngine;
using System.Runtime.InteropServices;

public class test_si : MonoBehaviour
{
    public static bool hasHeadPose = false;
    public static Vector3 latestHeadPos;
    public static Quaternion latestHeadRot;

    private hl2ss.shared.source source_si;

    void Start()
    {
        var cfg = new hl2ss.ulm.configuration_si();
        hl2ss.svc.open_stream(run_once.host_address, hl2ss.stream_port.SPATIAL_INPUT, 300, cfg, true, out source_si);
        Debug.Log("[SI] Spatial Input stream opened");
    }

    void Update()
    {
        using var packet = source_si.get_by_index(-1);
        if (packet.status != hl2ss.mt.status.OK)
        {
            Debug.Log("[SI] No packet yet");
            return;
        }

        packet.unpack(out hl2ss.map_si region);
        var data = Marshal.PtrToStructure<hl2ss.si_frame>(region.tracking);

        latestHeadPos = new Vector3(
            data.head_pose.position.x,
            data.head_pose.position.y,
            data.head_pose.position.z
        );

        Vector3 forward = new Vector3(
            data.head_pose.forward.x,
            data.head_pose.forward.y,
            data.head_pose.forward.z
        );

        Vector3 up = new Vector3(
            data.head_pose.up.x,
            data.head_pose.up.y,
            data.head_pose.up.z
        );

        latestHeadRot = Quaternion.LookRotation(forward, up);
        hasHeadPose = true;

        Debug.Log($"[SI] HeadPose OK: pos={latestHeadPos}");
    }
}