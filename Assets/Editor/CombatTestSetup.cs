using UnityEditor;
using UnityEngine;

// Боевая часть тестовой сцены: CombatAuthority, точка возрождения охотников, охотник и манекен-прячущийся.
// Идемпотентно (повторный запуск ничего не дублирует); SupermarketMapBuilder вызывает после GameplayTestSetup.Create.
public static class CombatTestSetup
{
    const string Dir = "Assets/Materials/Props";

    [MenuItem("PropHunt/Add Combat Test Setup")]
    public static void AddToOpenScene() { Ensure(); }

    public static void Ensure()
    {
        var gm = GameObject.Find("GameManager");
        if (gm == null) { Debug.LogError("Нет GameManager: сначала соберите карту (PropHunt/Build Supermarket Map)."); return; }
        var auth = gm.GetComponent<CombatAuthority>() ?? gm.AddComponent<CombatAuthority>();
        if (gm.GetComponent<DebugRoleSwitch>() == null) gm.AddComponent<DebugRoleSwitch>();

        auth.hunterMaterial = Mat($"{Dir}/Hunter.mat", new Color(0.75f, 0.2f, 0.15f));
        auth.gunMaterial = Mat($"{Dir}/Shotgun.mat", new Color(0.12f, 0.12f, 0.13f));
        auth.puffMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Puff.mat");

        var spawn = GameObject.Find("HunterSpawn");
        if (spawn == null) spawn = new GameObject("HunterSpawn");
        spawn.transform.SetPositionAndRotation(new Vector3(2.5f, 0.1f, -5.5f), Quaternion.identity);
        auth.hunterSpawn = spawn.transform;

        var player = GameObject.Find("Player");
        var cam = player != null ? player.GetComponentInChildren<Camera>(true) : null;
        if (Object.FindFirstObjectByType<HunterPlayer>() == null)
        {
            var h = HunterPlayer.Create(spawn.transform.position, 0f, cam, false, auth.hunterMaterial, auth.gunMaterial);
        }
        if (GameObject.Find("DummyHider") == null && player != null)
        {
            var src = player.GetComponent<HiderPlayer>();
            var d = new GameObject("DummyHider");
            d.transform.position = new Vector3(0f, 0.1f, 4.5f);
            var cc = d.AddComponent<CharacterController>();
            cc.height = 1.8f; cc.radius = 0.25f; cc.center = new Vector3(0, 0.9f, 0); cc.skinWidth = 0.01f;
            var stick = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            stick.name = "Stickman";
            Object.DestroyImmediate(stick.GetComponent<Collider>());
            stick.transform.SetParent(d.transform, false);
            stick.transform.localPosition = new Vector3(0, 0.9f, 0);
            stick.transform.localScale = new Vector3(0.5f, 0.9f, 0.5f);
            stick.GetComponent<Renderer>().sharedMaterial = Mat($"{Dir}/Stickman.mat", new Color(0.2f, 0.2f, 0.22f));
            var hp = d.AddComponent<HiderPlayer>();
            hp.controlled = false; hp.stickman = stick.transform;
            hp.outlineMaterial = src.outlineMaterial; hp.puffMaterial = src.puffMaterial;
        }
        EditorUtility.SetDirty(auth);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gm.scene);
    }

    static Material Mat(string path, Color c)
    {
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, path); }
        m.SetColor("_BaseColor", c);
        EditorUtility.SetDirty(m);
        return m;
    }
}
