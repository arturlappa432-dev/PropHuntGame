using UnityEngine;
using UnityEngine.AI;

// Спавнит ботов-прячущихся при старте сцены (демо без людей в лобби). Количество и личности — параметры.
// Бот собирается так же, как манекен: CharacterController + стикмен + HiderPlayer, плюс HiderBot вместо клавиатуры.
public class BotSpawner : MonoBehaviour
{
    public int count = 4;
    public Vector3 spawnCenter = new Vector3(0f, 0.1f, -4.5f);   // у входа
    public float spawnSpread = 6f;
    public int seed = 17;

    void Start()
    {
        var template = HiderPlayer.All.Find(h => h.cam != null) ?? HiderPlayer.All.Find(h => true);
        var rng = new System.Random(seed);
        for (int i = 0; i < count; i++)
        {
            Vector3 want = spawnCenter + new Vector3((float)(rng.NextDouble() * 2 - 1) * spawnSpread, 0f, (float)(rng.NextDouble() * 2 - 1) * 1.5f);
            Vector3 pos = NavMesh.SamplePosition(want, out var nh, 3f, new NavMeshQueryFilter { agentTypeID = 0, areaMask = NavMesh.AllAreas }) ? nh.position : want;   // агент 0: иначе может попасть на навмеш полок (HiderSmall)
            var p = i % 2 == 0 ? BotPersonality.Cautious() : BotPersonality.Bold();
            Spawn(pos, p, seed * 100 + i, template, $"Bot_{i + 1}_{p.name}");
        }
    }

    public static HiderBot Spawn(Vector3 pos, BotPersonality personality, int botSeed, HiderPlayer template, string name)
    {
        var d = new GameObject(name);
        d.transform.position = pos;
        var cc = d.AddComponent<CharacterController>();
        cc.height = 1.8f; cc.radius = 0.25f; cc.center = new Vector3(0, 0.9f, 0); cc.skinWidth = 0.01f;
        var stick = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        stick.name = "Stickman";
        Destroy(stick.GetComponent<Collider>());
        stick.transform.SetParent(d.transform, false);
        var tStick = template != null && template.stickman != null ? template.stickman.GetComponent<Renderer>() : null;
        if (tStick != null) stick.GetComponent<Renderer>().sharedMaterial = tStick.sharedMaterial;
        var hp = d.AddComponent<HiderPlayer>();
        hp.controlled = false;
        hp.stickman = stick.transform;
        if (template != null) { hp.outlineMaterial = template.outlineMaterial; hp.puffMaterial = template.puffMaterial; }
        var bot = d.AddComponent<HiderBot>();
        bot.personality = personality;
        bot.seed = botSeed;
        return bot;
    }
}
