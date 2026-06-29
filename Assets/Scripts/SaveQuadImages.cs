using UnityEngine;
using System.IO;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class SaveQuadImages : MonoBehaviour
{
    [Header("Source quads")]
    public Renderer quad1;
    public Renderer quad2;

    [Header("Texture property")]
    public string textureProperty = "_MainTex";

    [Header("Output folder inside Assets")]
    public string folderName = "TestImages";

    [Header("Output files")]
    public bool overwriteExistingFiles = true;
    public string quad1FileName = "Quad1_20260609_181907.png";
    public string quad2FileName = "Quad2_20260609_181907.png";

    [Header("Auto Save")]
    public bool autoSave = true;
    public bool saveInPlayerBuilds = false;
    public float saveIntervalSeconds = 20f;

    private float timer = 0f;

    void Update()
    {
        if (!autoSave) return;
#if !UNITY_EDITOR
        if (!saveInPlayerBuilds) return;
#endif

        timer += Time.deltaTime;

        if (timer >= saveIntervalSeconds)
        {
            timer = 0f;
            SaveImages();
        }
    }

    [ContextMenu("Save Quad Images")]
    public void SaveImages()
    {
#if !UNITY_EDITOR
        if (!saveInPlayerBuilds)
        {
            Debug.Log("SaveQuadImages is disabled in player builds.");
            return;
        }
#endif

        if (quad1 == null || quad2 == null)
        {
            Debug.LogError("Assign both source quads in the Inspector.");
            return;
        }

        string folderPath = GetOutputFolderPath();

        if (!Directory.Exists(folderPath))
            Directory.CreateDirectory(folderPath);

        string quad1Path = overwriteExistingFiles
            ? Path.Combine(folderPath, quad1FileName)
            : Path.Combine(folderPath, "Quad1_" + TimeStamp() + ".png");
        string quad2Path = overwriteExistingFiles
            ? Path.Combine(folderPath, quad2FileName)
            : Path.Combine(folderPath, "Quad2_" + TimeStamp() + ".png");

        SaveSingleQuad(quad1, quad1Path);
        SaveSingleQuad(quad2, quad2Path);

        Debug.Log("Saved quad images to: " + folderPath);
#if UNITY_EDITOR
        AssetDatabase.Refresh();
#endif
    }

    void SaveSingleQuad(Renderer quadRenderer, string filePath)
    {
        Texture tex = quadRenderer.material.GetTexture(textureProperty);

        if (tex == null)
        {
            Debug.LogError("No texture found on: " + quadRenderer.name);
            return;
        }

        Texture2D readableTex = ConvertToReadable(tex);
        byte[] png = readableTex.EncodeToPNG();
        File.WriteAllBytes(filePath, png);

        Debug.Log("Saved: " + filePath);
    }

    Texture2D ConvertToReadable(Texture src)
    {
        RenderTexture rt = new RenderTexture(src.width, src.height, 0);
        Graphics.Blit(src, rt);

        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;

        Texture2D tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
        tex.Apply();

        RenderTexture.active = prev;
        rt.Release();

        return tex;
    }

    string TimeStamp()
    {
        return System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
    }

    string GetOutputFolderPath()
    {
#if UNITY_EDITOR
        return Path.Combine(Application.dataPath, folderName);
#else
        return Path.Combine(Application.persistentDataPath, folderName);
#endif
    }
}
