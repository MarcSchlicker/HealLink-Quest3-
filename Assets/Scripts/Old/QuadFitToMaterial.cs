using UnityEngine;

[ExecuteAlways]
[DisallowMultipleComponent]
public class QuadFitToMaterial : MonoBehaviour
{
    [Header("Quelle")]
    [Tooltip("Renderer des Quads oder GameObject mit dem Material (z.B. MeshRenderer).")]
    public Renderer targetRenderer;

    [Header("Einstellungen")]
    [Tooltip("Pixel pro Welt‑Einheit. Beispiel: 100 => 100 Pixel = 1 Unity‑Unit.")]
    public float pixelsPerUnit = 100f;
    [Tooltip("Wenn true: Kinder behalten ihre Welt‑Skalierung bei (visuell unverändert).")]
    public bool preserveChildrenWorldScale = true;
    [Tooltip("Wenn true: passt sich automatisch beim Start an (Editor & Playmode).")]
    public bool autoApplyOnStart = true;
    [Tooltip("Nur das Seitenverhältnis anpassen (true) oder absolute Pixelgröße (false).")]
    public bool matchAspectOnly = false;

    // Optional: Offset in Welt‑Einheiten (z.B. um Quad leicht zu verschieben)
    public Vector3 additionalLocalScale = Vector3.one;

    void Start()
    {
        if (autoApplyOnStart)
            ApplySize();
    }

    /// <summary>
    /// Hauptfunktion: passt die lokale Skalierung des GameObjects so an,
    /// dass das sichtbare Quad der Pixelgröße des Materials entspricht.
    /// </summary>
    [ContextMenu("Apply Size")]
    public void ApplySize()
    {
        if (targetRenderer == null)
        {
            Debug.LogWarning("QuadFitToMaterial: targetRenderer ist nicht gesetzt.");
            return;
        }

        // Material / Texture finden
        Texture tex = targetRenderer.sharedMaterial != null ? targetRenderer.sharedMaterial.mainTexture : null;
        if (tex == null)
        {
            Debug.LogWarning("QuadFitToMaterial: Kein Texture im targetRenderer.material.mainTexture gefunden.");
            return;
        }

        // Pixelmaße
        int texW = tex.width;
        int texH = tex.height;
        if (texW <= 0 || texH <= 0)
        {
            Debug.LogWarning("QuadFitToMaterial: Ungültige Texture‑Größe.");
            return;
        }

        // Berechne gewünschte Weltgröße (Unity Units)
        float desiredWidth = texW / Mathf.Max(1e-6f, pixelsPerUnit);
        float desiredHeight = texH / Mathf.Max(1e-6f, pixelsPerUnit);

        // Wenn nur Seitenverhältnis, skaliere relativ zur aktuellen X oder Y
        Vector3 newLocalScale = transform.localScale;
        if (matchAspectOnly)
        {
            // Erhalte aktuelle Welt‑Breite anhand der aktuellen lokalen Skalierung (Quad default 1x1)
            // Wir berechnen einen Skalierungsfaktor, der das Seitenverhältnis anpasst, ohne absolute Größe zu erzwingen.
            float currentAspect = transform.localScale.x != 0f ? (transform.localScale.x / transform.localScale.y) : 1f;
            float targetAspect = desiredWidth / desiredHeight;
            if (Mathf.Approximately(currentAspect, 0f)) currentAspect = 1f;
            float aspectFactor = targetAspect / currentAspect;
            // Wir skalieren X um aspectFactor, Y bleibt, Z bleibt
            newLocalScale = new Vector3(transform.localScale.x * aspectFactor, transform.localScale.y, transform.localScale.z);
        }
        else
        {
            // Quad default ist 1x1 in lokalen Einheiten; setze lokale Skalierung direkt
            // Beachte: falls das Quad Mesh nicht 1x1 ist, passe ggf. mit meshBounds ein.
            newLocalScale = new Vector3(desiredWidth, desiredHeight, transform.localScale.z);
        }

        // optionaler zusätzlicher Faktor
        newLocalScale = Vector3.Scale(newLocalScale, additionalLocalScale);

        if (!preserveChildrenWorldScale)
        {
            transform.localScale = newLocalScale;
            return;
        }

        // Preserve children world scale:
        // 1) speichere Welt‑Skalierung aller direkten Kinder (lossyScale)
        Transform[] children = new Transform[transform.childCount];
        Vector3[] childrenWorldScales = new Vector3[transform.childCount];
        for (int i = 0; i < transform.childCount; ++i)
        {
            children[i] = transform.GetChild(i);
            childrenWorldScales[i] = children[i].lossyScale;
        }

        // 2) setze neue lokale Skalierung am Parent (Quad)
        transform.localScale = newLocalScale;

        // 3) berechne für jedes Kind die lokaleScale, die seine Welt‑Skalierung wiederherstellt
        //    localScale_child = desiredWorldScale / parent.lossyScale (komponentenweise)
        Vector3 parentLossy = transform.lossyScale;
        for (int i = 0; i < children.Length; ++i)
        {
            if (children[i] == null) continue;
            Vector3 desiredWorld = childrenWorldScales[i];

            // Vermeide Division durch Null
            float lx = parentLossy.x != 0f ? desiredWorld.x / parentLossy.x : desiredWorld.x;
            float ly = parentLossy.y != 0f ? desiredWorld.y / parentLossy.y : desiredWorld.y;
            float lz = parentLossy.z != 0f ? desiredWorld.z / parentLossy.z : desiredWorld.z;

            children[i].localScale = new Vector3(lx, ly, lz);
        }
    }

#if UNITY_EDITOR
    // Editor helper: bei Änderung im Inspector automatisch anwenden (optional)
    void OnValidate()
    {
        // Nur im Editor anwenden, nicht während Playmode automatisch, außer autoApplyOnStart ist true
        if (!Application.isPlaying)
        {
            // kleine Verzögerung vermeiden: nur anwenden wenn targetRenderer gesetzt
            if (targetRenderer != null)
            {
                ApplySize();
            }
        }
    }
#endif
}
