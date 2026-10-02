using System.IO;
using UnityEditor;
using UnityEngine;
using Random = System.Random;

// Тестовое наполнение карты: предметы-заглушки на стеллажах и на полу, игрок-прячущийся, менеджер раунда.
// Вызывается из SupermarketMapBuilder.Build(); меню PropHunt/... не нужно, пересборка карты делает всё.
public static class GameplayTestSetup
{
    const string PropMatDir = "Assets/Materials/Props";
    const int Seed = 12345;

    struct PropDef
    {
        public string name; public PrimitiveType shape; public Vector3 size; public PropTier tier; public int color;
        public PropDef(string n, PrimitiveType s, float x, float y, float z, PropTier t, int c)
        { name = n; shape = s; size = new Vector3(x, y, z); tier = t; color = c; }
    }

    // Размеры в метрах (итоговый размер меша; у цилиндра и капсулы высота = 2 * scale.y).
    static readonly PropDef[] Pool =
    {
        new PropDef("Can",    PrimitiveType.Capsule,  0.09f, 0.06f, 0.09f, PropTier.Tiny,   0),
        new PropDef("Snack",  PrimitiveType.Cube,     0.14f, 0.18f, 0.05f, PropTier.Tiny,   1),
        new PropDef("Box",    PrimitiveType.Cube,     0.30f, 0.20f, 0.22f, PropTier.Small,  2),
        new PropDef("Basket", PrimitiveType.Cylinder, 0.32f, 0.11f, 0.32f, PropTier.Small,  3),
        new PropDef("Bin",    PrimitiveType.Cylinder, 0.45f, 0.35f, 0.45f, PropTier.Medium, 4),
        new PropDef("Stool",  PrimitiveType.Cube,     0.40f, 0.50f, 0.40f, PropTier.Medium, 5),
        new PropDef("Table",  PrimitiveType.Cube,     1.00f, 0.90f, 0.70f, PropTier.Large,  1),
        new PropDef("Machine",PrimitiveType.Cube,     0.60f, 1.20f, 0.60f, PropTier.Large,  4),
    };

    static readonly Color[] Colors =
    {
        new Color(0.85f, 0.35f, 0.30f), new Color(0.95f, 0.75f, 0.25f), new Color(0.35f, 0.60f, 0.85f),
        new Color(0.40f, 0.75f, 0.45f), new Color(0.70f, 0.45f, 0.80f), new Color(0.90f, 0.55f, 0.30f),
    };

    public static void Create(Vector3 playerPos)
    {
        EnsureLayer("OwnBody");
        Directory.CreateDirectory(PropMatDir);
        var mats = new Material[Colors.Length];
        for (int i = 0; i < mats.Length; i++) mats[i] = Mat($"{PropMatDir}/Prop_{i}.mat", Colors[i]);

        var props = new GameObject("Props").transform;
        var points = new GameObject("SpawnPoints").transform;
        var rng = new Random(Seed);

        // Стеллажи: 3 ряда x 5 поверхностей (основание + 4 полки), по 12 точек, на каждой 2-3 предмета (tiny/small).
        float[] rowZ = { -1.8f, 0.4f, 2.6f };
        for (int r = 0; r < rowZ.Length; r++)
            for (int level = 0; level <= 4; level++)
            {
                float top = level == 0 ? 0.2f : 0.2f + level * 0.4f + 0.02f;
                var group = new GameObject($"Row{r + 1}_L{level}").transform;
                group.SetParent(points, false);
                var pts = new SpawnPoint[12];
                for (int i = 0; i < 12; i++)
                {
                    float x = -4.5f + i * (9f / 11f);
                    float z = rowZ[r] + (i % 2 == 0 ? -0.12f : 0.12f);
                    pts[i] = MakePoint(group, $"P{i}", new Vector3(x, top, z), PropTier.Small);
                }
                Fill(pts, 2 + rng.Next(2), props, mats, rng, PropTier.Tiny, PropTier.Small);
            }

        // Пол: 8 точек у боковых стен, 4 предмета среднего и крупного тира.
        var floor = new GameObject("Floor").transform;
        floor.SetParent(points, false);
        var fp = new SpawnPoint[8];
        for (int i = 0; i < 8; i++)
        {
            float x = i < 4 ? -8.6f : 8.6f;
            fp[i] = MakePoint(floor, $"P{i}", new Vector3(x, 0f, -3f + (i % 4) * 2f), PropTier.Large);
        }
        Fill(fp, 4, props, mats, rng, PropTier.Medium, PropTier.Large);

        // Материалы эффектов
        var outline = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Outline.mat");
        if (outline == null)
        {
            outline = new Material(Shader.Find("PropHunt/Outline"));
            AssetDatabase.CreateAsset(outline, "Assets/Materials/Outline.mat");
        }
        outline.SetColor("_Color", Color.white);
        outline.SetFloat("_Thickness", 0.04f);
        EditorUtility.SetDirty(outline);
        var puff = PuffMaterial();

        // Менеджер раунда
        var gm = new GameObject("GameManager");
        gm.AddComponent<RoundState>();
        gm.AddComponent<PossessionAuthority>();

        // Игрок
        var player = new GameObject("Player");
        player.transform.position = playerPos;
        var cc = player.AddComponent<CharacterController>();
        cc.height = 1.8f; cc.radius = 0.25f; cc.center = new Vector3(0, 0.9f, 0);
        cc.skinWidth = 0.01f;   // дефолт 0.08 даёт видимый зазор до поверхностей
        var stick = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        stick.name = "Stickman";
        Object.DestroyImmediate(stick.GetComponent<Collider>());
        stick.transform.SetParent(player.transform, false);
        stick.transform.localPosition = new Vector3(0, 0.9f, 0);
        stick.transform.localScale = new Vector3(0.5f, 0.9f, 0.5f);
        stick.GetComponent<Renderer>().sharedMaterial = Mat($"{PropMatDir}/Stickman.mat", new Color(0.2f, 0.2f, 0.22f));
        var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
        camGo.transform.SetParent(player.transform, false);
        var cam = camGo.AddComponent<Camera>();
        cam.nearClipPlane = 0.05f;
        camGo.AddComponent<AudioListener>();
        var hp = player.AddComponent<HiderPlayer>();
        hp.cam = cam; hp.stickman = stick.transform; hp.outlineMaterial = outline; hp.puffMaterial = puff;
    }

    static void Fill(SpawnPoint[] pts, int count, Transform parent, Material[] mats, Random rng, PropTier minTier, PropTier maxTier)
    {
        // случайное подмножество точек без повторов (частичная перетасовка)
        for (int i = pts.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (pts[i], pts[j]) = (pts[j], pts[i]);
        }
        for (int i = 0; i < count && i < pts.Length; i++)
        {
            var candidates = System.Array.FindAll(Pool, d => d.tier >= minTier && d.tier <= maxTier && d.tier <= pts[i].maxTier);
            Spawn(candidates[rng.Next(candidates.Length)], pts[i].transform.position, parent, mats, rng);
        }
    }

    static void Spawn(PropDef def, Vector3 surfacePos, Transform parent, Material[] mats, Random rng)
    {
        var go = GameObject.CreatePrimitive(def.shape);
        go.name = def.name;
        go.transform.SetParent(parent, false);
        float h = def.shape == PrimitiveType.Cube ? def.size.y : def.size.y * 2f;
        Vector3 scale = def.shape == PrimitiveType.Cube ? def.size : new Vector3(def.size.x, def.size.y, def.size.z);
        go.transform.localScale = scale;
        go.transform.position = surfacePos + Vector3.up * (h * 0.5f);
        go.transform.rotation = Quaternion.Euler(0, (float)rng.NextDouble() * 360f, 0);
        go.GetComponent<Renderer>().sharedMaterial = mats[def.color];
        FitCollider(go);
        Ground(go, surfacePos);
        var prop = go.AddComponent<Prop>();
        prop.tier = def.tier;
        // габариты по локальным границам меша (без раздувания AABB от поворота)
        var lb = go.GetComponent<MeshFilter>().sharedMesh.bounds;
        var ls = go.transform.localScale;
        prop.height = lb.size.y * ls.y;
        prop.footRadius = Mathf.Max(lb.extents.x * ls.x, lb.extents.z * ls.z);
    }

    // Коллайдер строго по мешу: у куба примитивный BoxCollider уже точный, у цилиндра/капсулы
    // примитивный CapsuleCollider скруглён и оставляет зазор у граней, поэтому ставим выпуклый MeshCollider по самому мешу.
    static void FitCollider(GameObject go)
    {
        var old = go.GetComponent<Collider>();
        if (old is BoxCollider || go.GetComponent<MeshFilter>().sharedMesh.name == "Capsule") return;   // капсула: меш тоже скруглённый, примитивный коллайдер совпадает
        Object.DestroyImmediate(old);
        var mc = go.AddComponent<MeshCollider>();
        mc.sharedMesh = go.GetComponent<MeshFilter>().sharedMesh;
        mc.convex = true;
    }

    // Опирает предмет на поверхность под ним: луч вниз по коллайдерам карты, нижняя грань коллайдера ложится на точку попадания.
    static void Ground(GameObject go, Vector3 surfacePos)
    {
        Physics.SyncTransforms();
        var origin = new Vector3(surfacePos.x, surfacePos.y + 0.05f, surfacePos.z);
        var hits = Physics.RaycastAll(origin, Vector3.down, 0.3f, ~0, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (var h in hits)
        {
            if (h.collider.transform.IsChildOf(go.transform) || h.collider.GetComponentInParent<Prop>() != null) continue;
            float dy = h.point.y - go.GetComponent<Renderer>().bounds.min.y;   // по фактическому мешу, не по коллайдеру
            go.transform.position += Vector3.up * dy;
            Physics.SyncTransforms();
            return;
        }
        Debug.LogWarning($"Grounding: под точкой {surfacePos} нет поверхности, предмет {go.name} оставлен как есть");
    }

    // Слой для собственного тела игрока (исключается из камеры от первого лица), см. HiderPlayer.
    static void EnsureLayer(string layerName)
    {
        if (LayerMask.NameToLayer(layerName) >= 0) return;
        var tm = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var layers = tm.FindProperty("layers");
        for (int i = 8; i < layers.arraySize; i++)
        {
            var el = layers.GetArrayElementAtIndex(i);
            if (string.IsNullOrEmpty(el.stringValue)) { el.stringValue = layerName; tm.ApplyModifiedProperties(); return; }
        }
        Debug.LogError("Нет свободного слоя для " + layerName);
    }

    static SpawnPoint MakePoint(Transform parent, string name, Vector3 pos, PropTier maxTier)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.position = pos;
        var sp = go.AddComponent<SpawnPoint>();
        sp.maxTier = maxTier;
        return sp;
    }

    static Material PuffMaterial()
    {
        const string texPath = "Assets/Materials/SoftCircle.png";
        if (!File.Exists(texPath))
        {
            var t = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), new Vector2(31.5f, 31.5f)) / 32f;
                    float a = Mathf.Clamp01(1f - d);
                    t.SetPixel(x, y, new Color(1, 1, 1, a * a));
                }
            File.WriteAllBytes(texPath, t.EncodeToPNG());
            AssetDatabase.ImportAsset(texPath);
            var imp = (TextureImporter)AssetImporter.GetAtPath(texPath);
            imp.alphaIsTransparency = true;
            imp.SaveAndReimport();
        }
        const string matPath = "Assets/Materials/Puff.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (m == null)
        {
            m = new Material(Shader.Find("Sprites/Default"));
            AssetDatabase.CreateAsset(m, matPath);
        }
        m.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
        EditorUtility.SetDirty(m);
        return m;
    }

    static Material Mat(string path, Color c)
    {
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(m, path);
        }
        m.SetColor("_BaseColor", c);
        EditorUtility.SetDirty(m);
        return m;
    }
}
