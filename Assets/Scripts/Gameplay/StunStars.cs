using UnityEngine;

// Звёздочки над оглушённым предметом (ram-kick.md, фаза «в отключке»): 3 маленьких 2D-спрайта на пустом объекте,
// который медленно и равномерно вращается вокруг вертикальной оси. Чистое вращение кодом, без покадровой анимации.
public class StunStars : MonoBehaviour
{
    public float spinSpeed = 120f;   // °/с
    Transform follow;
    Renderer followRend;
    Transform spinner;
    Transform[] stars;
    float angle;

    static Texture2D starTex;
    static Material starMat;

    public static StunStars Create(Prop prop)
    {
        var go = new GameObject("StunStars");
        var s = go.AddComponent<StunStars>();
        s.follow = prop.transform;
        s.followRend = prop.GetComponentInChildren<Renderer>();
        float size = Mathf.Max(prop.height, prop.footRadius * 2f);
        float radius = Mathf.Max(0.15f, size * 0.3f), starSize = 0.12f + size * 0.12f;
        s.spinner = new GameObject("Spinner").transform;
        s.spinner.SetParent(go.transform, false);
        s.stars = new Transform[3];
        for (int i = 0; i < 3; i++)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(q.GetComponent<Collider>());
            q.name = "Star";
            q.transform.SetParent(s.spinner, false);
            float a = i * 120f * Mathf.Deg2Rad;
            q.transform.localPosition = new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(i * 2.1f) * 0.03f, Mathf.Sin(a) * radius);
            q.transform.localScale = Vector3.one * starSize;
            q.GetComponent<Renderer>().sharedMaterial = StarMaterial;
            s.stars[i] = q.transform;
        }
        s.Place();
        return s;
    }

    static Material StarMaterial
    {
        get
        {
            if (starMat != null) return starMat;
            starMat = new Material(Shader.Find("Sprites/Default")) { mainTexture = StarTexture, color = new Color(1f, 0.9f, 0.25f) };
            return starMat;
        }
    }

    // Процедурная пятиконечная звезда (ассетов нет).
    static Texture2D StarTexture
    {
        get
        {
            if (starTex != null) return starTex;
            const int n = 64;
            starTex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            var poly = new Vector2[10];
            for (int i = 0; i < 10; i++)
            {
                float a = Mathf.PI / 2f + i * Mathf.PI / 5f, r = i % 2 == 0 ? 0.48f : 0.2f;
                poly[i] = new Vector2(0.5f + Mathf.Cos(a) * r, 0.5f + Mathf.Sin(a) * r);
            }
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    starTex.SetPixel(x, y, Inside(poly, new Vector2((x + 0.5f) / n, (y + 0.5f) / n)) ? Color.white : new Color(1, 1, 1, 0));
            starTex.Apply();
            return starTex;
        }
    }

    static bool Inside(Vector2[] p, Vector2 pt)
    {
        bool c = false;
        for (int i = 0, j = p.Length - 1; i < p.Length; j = i++)
            if ((p[i].y > pt.y) != (p[j].y > pt.y) && pt.x < (p[j].x - p[i].x) * (pt.y - p[i].y) / (p[j].y - p[i].y) + p[i].x) c = !c;
        return c;
    }

    void Place()
    {
        if (follow == null) return;
        float top = followRend != null ? followRend.bounds.max.y : follow.position.y;
        transform.position = new Vector3(follow.position.x, top + 0.2f, follow.position.z);
    }

    void LateUpdate()
    {
        Place();
        angle += spinSpeed * Time.deltaTime;
        spinner.localRotation = Quaternion.Euler(0f, angle, 0f);
        var cam = Camera.main != null ? Camera.main : FindFirstObjectByType<Camera>();
        if (cam == null) return;
        foreach (var s in stars) s.rotation = cam.transform.rotation;   // спрайты всегда лицом к камере
    }
}
