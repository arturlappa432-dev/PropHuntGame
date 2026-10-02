using System.IO;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Серая карта супермаркета 20x14 м (MVP, docs/map-supermarket.md). Меню: PropHunt/Build Supermarket Map.
// Начало координат в центре зала; X: -10..10 (ширина), Z: -7..7 (глубина). Вход и касса на Z-, подсобка на Z+.
public static class SupermarketMapBuilder
{
    const string ScenePath = "Assets/Scenes/Supermarket.unity";
    const string MatDir = "Assets/Materials/Map";

    // Параметры карты (метры)
    const float W = 20f, D = 14f, H = 3.5f, WallT = 0.3f;
    const float BackRoomDepth = 3f;     // подсобка вдоль задней стены
    const float BackRoomZ = D / 2f - BackRoomDepth; // z = 4: перегородка подсобки
    const int ShelfRows = 3;
    const float ShelfLen = 10f, ShelfDepth = 0.6f, ShelfH = 2f;

    static Material floorM, wallM, ceilM, shelfM, counterM, backM;

    [MenuItem("PropHunt/Build Supermarket Map")]
    public static void Build()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        MakeMaterials();

        var root = new GameObject("Supermarket").transform;
        var shell = Group("Shell", root);
        var shelves = Group("Shelves", root);
        var checkout = Group("Checkout", root);
        var backroom = Group("BackRoom", root);

        // Пол и потолок
        Box("Floor", shell, new Vector3(0, -0.1f, 0), new Vector3(W + 2 * WallT, 0.2f, D + 2 * WallT), floorM);
        Box("Ceiling", shell, new Vector3(0, H + 0.1f, 0), new Vector3(W + 2 * WallT, 0.2f, D + 2 * WallT), ceilM);

        // Внешние стены; в южной стене проём входа шириной 3 м по центру
        Box("Wall_N", shell, new Vector3(0, H / 2, D / 2 + WallT / 2), new Vector3(W + 2 * WallT, H, WallT), wallM);
        Box("Wall_E", shell, new Vector3(W / 2 + WallT / 2, H / 2, 0), new Vector3(WallT, H, D), wallM);
        Box("Wall_W", shell, new Vector3(-W / 2 - WallT / 2, H / 2, 0), new Vector3(WallT, H, D), wallM);
        float doorW = 3f, doorH = 2.5f;
        float sideW = (W - doorW) / 2f;
        Box("Wall_S_L", shell, new Vector3(-(doorW / 2 + sideW / 2), H / 2, -D / 2 - WallT / 2), new Vector3(sideW + WallT, H, WallT), wallM);
        Box("Wall_S_R", shell, new Vector3(doorW / 2 + sideW / 2, H / 2, -D / 2 - WallT / 2), new Vector3(sideW + WallT, H, WallT), wallM);
        Box("Wall_S_Lintel", shell, new Vector3(0, doorH + (H - doorH) / 2, -D / 2 - WallT / 2), new Vector3(doorW, H - doorH, WallT), wallM);

        // Подсобка: перегородка с дверью 1.2 м ближе к правому краю
        float bd = 1.2f, bx = 6f;
        float leftLen = (bx - bd / 2) + W / 2;          // от x=-10 до левой кромки двери
        float rightLen = W / 2 - (bx + bd / 2);
        Box("BackWall_L", backroom, new Vector3(-W / 2 + leftLen / 2, H / 2, BackRoomZ), new Vector3(leftLen, H, WallT), backM);
        Box("BackWall_R", backroom, new Vector3(W / 2 - rightLen / 2, H / 2, BackRoomZ), new Vector3(rightLen, H, WallT), backM);
        Box("BackWall_Lintel", backroom, new Vector3(bx, 2.2f + (H - 2.2f) / 2, BackRoomZ), new Vector3(bd, H - 2.2f, WallT), backM);
        // Стеллажи подсобки (ящики/полки у задней стены)
        for (int i = 0; i < 3; i++)
            Box($"BackShelf_{i}", backroom, new Vector3(-7f + i * 3.2f, 1f, D / 2 - 0.4f), new Vector3(2.4f, 2f, 0.6f), shelfM);
        Box("BackCrates", backroom, new Vector3(6.5f, 0.4f, D / 2 - 1f), new Vector3(2f, 0.8f, 1.2f), shelfM);

        // 3 ряда стеллажей вдоль X; Z между кассой (z≈-4) и подсобкой (z=4)
        float[] rowZ = { -1.8f, 0.4f, 2.6f };
        for (int r = 0; r < ShelfRows; r++)
        {
            float z = rowZ[r];
            // Корпус стеллажа: основание + задняя панель + 4 полки (полки — будущие поверхности спавна)
            var row = Group($"ShelfRow_{r + 1}", shelves);
            row.position = new Vector3(0, 0, z);
            Box("Base", row, new Vector3(0, 0.1f, 0) + row.position, new Vector3(ShelfLen, 0.2f, ShelfDepth), shelfM);
            Box("Back", row, new Vector3(0, ShelfH / 2, 0) + row.position, new Vector3(ShelfLen, ShelfH, 0.05f), shelfM);
            for (int s = 1; s <= 4; s++)
                Box($"Shelf_{s}", row, new Vector3(0, 0.2f + s * 0.4f, 0) + row.position, new Vector3(ShelfLen, 0.04f, ShelfDepth), shelfM);
            Box("EndCap_L", row, new Vector3(-ShelfLen / 2, ShelfH / 2, 0) + row.position, new Vector3(0.05f, ShelfH, ShelfDepth), shelfM);
            Box("EndCap_R", row, new Vector3(ShelfLen / 2, ShelfH / 2, 0) + row.position, new Vector3(0.05f, ShelfH, ShelfDepth), shelfM);
        }

        // Касса у входа: прилавок слева от входа + лента + рамка кассы
        Box("CashDesk", checkout, new Vector3(-5.5f, 0.5f, -5.4f), new Vector3(3.0f, 1.0f, 0.8f), counterM);
        Box("CashDesk_Side", checkout, new Vector3(-7.15f, 0.5f, -4.6f), new Vector3(0.8f, 1.0f, 1.6f), counterM);
        Box("CashRegister", checkout, new Vector3(-5.0f, 1.15f, -5.4f), new Vector3(0.5f, 0.3f, 0.4f), shelfM);
        Box("CashDesk2", checkout, new Vector3(5.5f, 0.5f, -5.4f), new Vector3(3.0f, 1.0f, 0.8f), counterM);
        Box("CheckoutGate", checkout, new Vector3(-3.6f, 0.45f, -4.8f), new Vector3(0.1f, 0.9f, 1.2f), counterM);

        // Свет (realtime, без запекания — MVP)
        var sun = new GameObject("Directional Light");
        var l = sun.AddComponent<Light>(); l.type = LightType.Directional; l.intensity = 0.4f;
        sun.transform.rotation = Quaternion.Euler(50, 30, 0);
        for (int x = -6; x <= 6; x += 6)
            for (int z = -4; z <= 4; z += 4)
            {
                var go = new GameObject($"Light_{x}_{z}");
                go.transform.position = new Vector3(x, H - 0.3f, z);
                var pl = go.AddComponent<Light>(); pl.type = LightType.Point; pl.range = 9f; pl.intensity = 1.5f;
            }
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.45f, 0.45f, 0.45f);

        // Точки старта и камера для скриншота
        var spawn = new GameObject("PlayerStart"); spawn.transform.position = new Vector3(0, 0.1f, -5.5f);
        var camGo = new GameObject("Main Camera"); camGo.tag = "MainCamera";
        camGo.AddComponent<Camera>(); camGo.AddComponent<AudioListener>();
        camGo.transform.position = new Vector3(0, 12f, -15f);
        camGo.transform.rotation = Quaternion.Euler(45, 0, 0);

        // NavMesh: поверхность собирает только геометрию Supermarket
        var surf = root.gameObject.AddComponent<NavMeshSurface>();
        surf.collectObjects = CollectObjects.Children;
        surf.useGeometry = UnityEngine.AI.NavMeshCollectGeometry.PhysicsColliders;

        // Сцена сохраняется до запекания, чтобы NavMeshData лёг рядом со сценой
        Directory.CreateDirectory("Assets/Scenes");
        EditorSceneManager.SaveScene(scene, ScenePath);
        surf.BuildNavMesh();
        const string navPath = "Assets/Scenes/Supermarket_NavMesh.asset";
        if (surf.navMeshData != null && !AssetDatabase.Contains(surf.navMeshData))
        {
            AssetDatabase.DeleteAsset(navPath);
            AssetDatabase.CreateAsset(surf.navMeshData, navPath);
        }
        EditorUtility.SetDirty(surf);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log("Supermarket map built: " + ScenePath);
    }

    static Transform Group(string name, Transform parent)
    {
        var t = new GameObject(name).transform; t.SetParent(parent, false); return t;
    }

    // Куб с BoxCollider; позиция мировая (центр), размер в метрах. Статичный.
    static GameObject Box(string name, Transform parent, Vector3 pos, Vector3 size, Material m)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, true);
        go.transform.position = pos;
        go.transform.localScale = size;
        go.GetComponent<Renderer>().sharedMaterial = m;
        GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.ContributeGI | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic | StaticEditorFlags.ReflectionProbeStatic);
        return go;
    }

    static void MakeMaterials()
    {
        Directory.CreateDirectory(MatDir);
        floorM = Mat("Floor", new Color(0.45f, 0.45f, 0.47f));
        wallM = Mat("Wall", new Color(0.75f, 0.75f, 0.75f));
        ceilM = Mat("Ceiling", new Color(0.85f, 0.85f, 0.85f));
        shelfM = Mat("Shelf", new Color(0.55f, 0.58f, 0.62f));
        counterM = Mat("Counter", new Color(0.6f, 0.5f, 0.4f));
        backM = Mat("BackRoom", new Color(0.65f, 0.65f, 0.7f));
    }

    static Material Mat(string name, Color c)
    {
        string path = $"{MatDir}/{name}.mat";
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
