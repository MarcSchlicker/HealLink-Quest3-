using UnityEngine;
using System.IO;

public class SaveQuadImages : MonoBehaviour
{
    [Header("Assign the two quads whose textures you want to save")]
    public Renderer quad1;
    public Renderer quad2;

    [Header("Texture property name (usually _MainTex or your depth property)")]
    public string textureProperty = "_MainTex";

    [Header("Output folder inside Assets")]
    public string folderName = "TestBilder";

    [Header("Auto Save")]
    public bool autoSave = true;
    public float saveIntervalSeconds = 20f;

    private float timer = 0f;

    void Update()
    {
        if (!autoSave) return;

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
        if (quad1 == null || quad2 == null)
        {
            Debug.LogError("Bitte beide Quads im Inspector zuweisen!");
            return;
        }

        string folderPath = Path.Combine(Application.dataPath, folderName);

        if (!Directory.Exists(folderPath))
            Directory.CreateDirectory(folderPath);

        SaveSingleQuad(quad1, Path.Combine(folderPath, "Quad1_" + TimeStamp() + ".png"));
        SaveSingleQuad(quad2, Path.Combine(folderPath, "Quad2_" + TimeStamp() + ".png"));

        Debug.Log("Bilder gespeichert unter: " + folderPath);
    }

    void SaveSingleQuad(Renderer quadRenderer, string filePath)
    {
        Texture tex = quadRenderer.material.GetTexture(textureProperty);

        if (tex == null)
        {
            Debug.LogError("Keine Texture gefunden auf: " + quadRenderer.name);
            return;
        }

        Texture2D readableTex = ConvertToReadable(tex);
        byte[] png = readableTex.EncodeToPNG();
        File.WriteAllBytes(filePath, png);

        Debug.Log("Gespeichert: " + filePath);
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
}
