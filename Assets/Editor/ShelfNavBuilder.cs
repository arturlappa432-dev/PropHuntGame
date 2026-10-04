using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

// NavMesh для мелких предметов на полках (docs/systems/bots.md, «прятки на полках»).
// Отдельный тип агента HiderSmall (радиус/рост под Tiny/Small) запекается по всему залу: пол + каждый уровень каждого стеллажа.
// Уровни с полом связаны локально: на каждую сторону каждого уровня ряд узких NavMeshLink (область Jump, шаг 0,5 м) от пола прохода
// к средней линии уровня + ShelfSlot с параметрами уровня. Вертикально связный NavMesh не нужен.
// Меню: PropHunt/Build Shelf NavMesh. SupermarketMapBuilder вызывает Build после основной навмеши.
public static class ShelfNavBuilder
{
    public const string AgentName = "HiderSmall";
    const float AgentRadius = 0.1f, AgentHeight = 0.34f, AgentClimb = 0.05f, CellSize = 0.025f;
    const float LinkFloorOut = 0.3f;     // точка на полу: столько от кромки полки в проход
    const float EndMargin = 0.3f;        // отступ от торцов стеллажа
    const float LinkStep = 0.5f;         // шаг узких связей вдоль полки
    const string NavPath = "Assets/Scenes/Supermarket_NavMesh_Small.asset";

    [MenuItem("PropHunt/Build Shelf NavMesh")]
    public static void BuildInOpenScene()
    {
        var root = GameObject.Find("Supermarket");
        if (root == null) { Debug.LogError("Нет корня Supermarket: сначала PropHunt/Build Supermarket Map"); return; }
        Build(root.transform);
        EditorSceneManager.MarkSceneDirty(root.scene);
        EditorSceneManager.SaveScene(root.scene);
        AssetDatabase.SaveAssets();
    }

    public static void Build(Transform root)
    {
        int agent = EnsureAgentType();
        int area = EnsureShelfArea();
        int slots = BuildSlots(root, agent, area);

        // Вторая поверхность на том же корне: тот же сбор геометрии, свой агент и мелкий воксель (полка 0,275 м глубиной).
        NavMeshSurface surf = null;
        foreach (var s in root.GetComponents<NavMeshSurface>()) if (s.agentTypeID == agent) surf = s;
        if (surf == null) surf = root.gameObject.AddComponent<NavMeshSurface>();
        surf.agentTypeID = agent;
        surf.collectObjects = CollectObjects.Children;
        surf.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
        surf.overrideVoxelSize = true;
        surf.voxelSize = CellSize;
        surf.minRegionArea = 0.05f;
        surf.BuildNavMesh();
        if (surf.navMeshData != null && !AssetDatabase.Contains(surf.navMeshData))
        {
            AssetDatabase.DeleteAsset(NavPath);
            AssetDatabase.CreateAsset(surf.navMeshData, NavPath);
        }
        EditorUtility.SetDirty(surf);
        Debug.Log($"Shelf NavMesh: агент {AgentName} id={agent}, уровней-сторон {slots}");
    }

    // Тип агента ищется по имени; если нет — создаётся. Параметры каждый раз приводятся к заданным.
    public static int EnsureAgentType()
    {
        int id = int.MinValue;
        for (int i = 0; i < NavMesh.GetSettingsCount(); i++)
        {
            var s = NavMesh.GetSettingsByIndex(i);
            if (NavMesh.GetSettingsNameFromID(s.agentTypeID) == AgentName) id = s.agentTypeID;
        }
        if (id == int.MinValue) id = NavMesh.CreateSettings().agentTypeID;

        var asset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/NavMeshAreas.asset")[0];
        var so = new SerializedObject(asset);
        var settings = so.FindProperty("m_Settings");
        var names = so.FindProperty("m_SettingNames");
        for (int i = 0; i < settings.arraySize; i++)
        {
            var e = settings.GetArrayElementAtIndex(i);
            if (e.FindPropertyRelative("agentTypeID").intValue != id) continue;
            e.FindPropertyRelative("agentRadius").floatValue = AgentRadius;
            e.FindPropertyRelative("agentHeight").floatValue = AgentHeight;
            e.FindPropertyRelative("agentClimb").floatValue = AgentClimb;
            e.FindPropertyRelative("agentSlope").floatValue = 45f;
            e.FindPropertyRelative("minRegionArea").floatValue = 0.05f;
            e.FindPropertyRelative("manualCellSize").intValue = 1;
            e.FindPropertyRelative("cellSize").floatValue = CellSize;
            while (names.arraySize <= i) names.InsertArrayElementAtIndex(names.arraySize);
            names.GetArrayElementAtIndex(i).stringValue = AgentName;
        }
        so.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
        return id;
    }

    // Область NavMesh «Shelf» с высокой стоимостью: путь заходит на полку, только если цель на ней,
    // а не срезает по полкам через проход (иначе бот при побеге прыгал полка-пол-полка).
    public const string AreaName = "Shelf";
    const int AreaIndex = 3;            // 0 Walkable, 1 Not Walkable, 2 Jump — встроенные
    const float AreaCost = 10f, LinkCost = 4f;

    public static int EnsureShelfArea()
    {
        var asset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/NavMeshAreas.asset")[0];
        var so = new SerializedObject(asset);
        var areas = so.FindProperty("areas");
        var e = areas.GetArrayElementAtIndex(AreaIndex);
        e.FindPropertyRelative("name").stringValue = AreaName;
        e.FindPropertyRelative("cost").floatValue = AreaCost;
        so.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
        return AreaIndex;
    }

    // Стеллажи — группы ShelfRow_* (вдоль X): основание Base, полки Shelf_1..N, задняя панель Back посередине глубины.
    static int BuildSlots(Transform root, int agent, int area)
    {
        int n = 0;
        // ряды собираем заранее: ниже удаляются старые слоты, а они лежали бы в том же снимке иерархии
        var rows = new List<Transform>();
        foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name.StartsWith("ShelfRow_")) rows.Add(t);
        foreach (var row in rows)
        {
            foreach (var old in row.GetComponentsInChildren<ShelfSlot>(true)) Object.DestroyImmediate(old.gameObject);
            var back = row.Find("Back");
            if (back == null) continue;
            var bb = back.GetComponent<Collider>().bounds;
            var boards = new List<Transform>();
            foreach (Transform c in row) if (c.name == "Base" || c.name.StartsWith("Shelf_")) boards.Add(c);
            boards.Sort((x, y) => x.GetComponent<Collider>().bounds.max.y.CompareTo(y.GetComponent<Collider>().bounds.max.y));
            for (int lv = 0; lv < boards.Count; lv++)
            {
                var mod = boards[lv].GetComponent<NavMeshModifier>() ?? boards[lv].gameObject.AddComponent<NavMeshModifier>();
                mod.overrideArea = true;
                mod.area = area;
                var bd = boards[lv].GetComponent<Collider>().bounds;
                float top = bd.max.y;
                float above = lv + 1 < boards.Count ? boards[lv + 1].GetComponent<Collider>().bounds.min.y - top : 3f;
                foreach (int side in new[] { -1, 1 })
                {
                    float edge = side < 0 ? bd.min.z : bd.max.z;
                    float inner = side < 0 ? bb.min.z : bb.max.z;
                    float depth = Mathf.Abs(edge - inner);
                    float zMid = (edge + inner) * 0.5f;
                    float x0 = bd.min.x + EndMargin, x1 = bd.max.x - EndMargin;
                    var go = new GameObject($"Slot_L{lv}_{(side < 0 ? "S" : "N")}");
                    go.transform.SetParent(row, false);
                    go.transform.position = new Vector3((x0 + x1) * 0.5f, 0f, edge);
                    go.transform.rotation = Quaternion.identity;
                    var slot = go.AddComponent<ShelfSlot>();
                    slot.a = new Vector3(x0, top, zMid); slot.b = new Vector3(x1, top, zMid);
                    slot.normal = new Vector3(0f, 0f, side);
                    slot.top = top; slot.clearance = above; slot.depth = depth; slot.level = lv;
                    // Ряд узких перпендикулярных связей через LinkStep, а не одна широкая: у широкой связи Unity выбирает точки
                    // на полу и на полке независимо — путь «прыгал» по диагонали на 2+ м и бот потом шёл вдоль полки.
                    int count = Mathf.Max(1, Mathf.FloorToInt((x1 - x0) / LinkStep) + 1);
                    for (int i = 0; i < count; i++)
                    {
                        float lx = x0 + (x1 - x0) * (count == 1 ? 0.5f : i / (float)(count - 1)) - go.transform.position.x;
                        var link = go.AddComponent<NavMeshLink>();
                        link.agentTypeID = agent;
                        link.startPoint = new Vector3(lx, 0f, side * LinkFloorOut);          // пол прохода
                        link.endPoint = new Vector3(lx, top, zMid - edge);                   // середина глубины уровня
                        link.width = 0f;
                        link.bidirectional = true;
                        link.area = 2;   // Jump
                        link.costModifier = LinkCost;
                        link.autoUpdate = false;
                    }
                    n++;
                }
            }
        }
        return n;
    }
}
