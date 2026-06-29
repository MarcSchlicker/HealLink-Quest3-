using System.Net.Sockets;
using UnityEngine;

public class run_once : MonoBehaviour
{
    [Header("HL2SS Host Settings")]
    public string host;
    [Tooltip("HL2SS Personal Video port used only as a connection health check.")]
    public int testPort = 3810;
    public int connectionTimeoutMs = 500;
    [Range(1, 5)]
    public int connectionAttempts = 3;
    public int connectionRetryDelayMs = 150;

    public static string host_address;

    [Header("Scripts to disable if connection fails")]
    public MonoBehaviour[] hl2ssScripts;

    private void Awake()
    {
        host_address = TrimToNull(host);

        if (string.IsNullOrEmpty(host_address))
        {
            Debug.LogWarning("[HL2SS] No host configured. Disabling HL2SS scripts.");
            DisableHl2ssScripts();
            return;
        }

        if (!CheckConnectionWithRetries(host_address, testPort))
        {
            Debug.LogWarning("[HL2SS] Could not connect to " + host_address + ":" + testPort + ". Disabling HL2SS scripts.");
            DisableHl2ssScripts();
            return;
        }

        try
        {
            Debug.Log("[HL2SS] Connection succeeded. Initializing HL2SS.");
            hl2ss.svc.initialize();
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[HL2SS] Initialization failed. Disabling HL2SS scripts. " + e.Message);
            DisableHl2ssScripts();
        }
    }

    private bool CheckConnectionWithRetries(string host, int port)
    {
        int attempts = Mathf.Clamp(connectionAttempts, 1, 5);
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            if (CheckConnection(host, port))
            {
                Debug.Log("[HL2SS] PV health check succeeded on " + host + ":" + port + ".");
                return true;
            }

            if (attempt < attempts)
            {
                System.Threading.Thread.Sleep(Mathf.Clamp(connectionRetryDelayMs, 0, 1000));
            }
        }

        return false;
    }

    private bool CheckConnection(string host, int port)
    {
        if (string.IsNullOrEmpty(host) || port <= 0 || port > 65535)
        {
            return false;
        }

        try
        {
            using (TcpClient client = new TcpClient())
            {
                int timeout = Mathf.Max(100, connectionTimeoutMs);
                client.ReceiveTimeout = timeout;
                client.SendTimeout = timeout;

                System.IAsyncResult result = client.BeginConnect(host, port, null, null);
                bool success = result.AsyncWaitHandle.WaitOne(timeout);
                if (!success)
                {
                    return false;
                }

                client.EndConnect(result);
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    private void DisableHl2ssScripts()
    {
        if (hl2ssScripts == null)
        {
            return;
        }

        foreach (MonoBehaviour script in hl2ssScripts)
        {
            if (script != null)
            {
                script.enabled = false;
            }
        }
    }

    private static string TrimToNull(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private void OnValidate()
    {
        testPort = Mathf.Clamp(testPort, 1, 65535);
        connectionTimeoutMs = Mathf.Clamp(connectionTimeoutMs, 100, 5000);
        connectionAttempts = Mathf.Clamp(connectionAttempts, 1, 5);
        connectionRetryDelayMs = Mathf.Clamp(connectionRetryDelayMs, 0, 1000);
    }
}
