using UnityEngine;
using UnityEngine.InputSystem;

// Отладка без сети: F2 передаёт управление между прячущимся и охотником, F3 завершает подготовку.
// Уйдёт, когда появятся лобби и сеть (в игре одна роль на игрока).
public class DebugRoleSwitch : MonoBehaviour
{
    void Update()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        if (kb.f3Key.wasPressedThisFrame && RoundState.Instance != null && RoundState.Instance.Phase == RoundPhase.Prep) RoundState.Instance.EndPrep();
        if (kb.f2Key.wasPressedThisFrame) Swap();
        if (kb.f4Key.wasPressedThisFrame) NewBotRound();
        if (kb.f6Key.wasPressedThisFrame) WhatIsAimed();
    }

    // F6: что под прицелом (центр экрана) — свободный предмет или тело прячущегося (какого, в каком состоянии). Для поиска «призраков».
    static string lastAimed = ""; static float lastAimedUntil;
    public static void WhatIsAimed()
    {
        var cam = Camera.main;
        if (cam == null) return;
        var hits = Physics.RaycastAll(cam.transform.position, cam.transform.forward, 40f, ~0, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        string s = "ничего";
        foreach (var h in hits)
        {
            if (h.collider.GetComponentInParent<HunterPlayer>() != null) continue;
            var prop = h.collider.GetComponentInParent<Prop>();
            var hider = h.collider.GetComponentInParent<HiderPlayer>();
            var bot = hider != null ? hider.GetComponent<HiderBot>() : null;
            if (hider != null) s = $"тело прячущегося {hider.name}{(bot != null ? $" (бот, {bot.Current})" : "")}, облик {(hider.CurrentProp != null ? hider.CurrentProp.ModelId : "-")}, {h.distance:F1} м";
            else if (prop != null) s = $"свободный предмет {prop.ModelId} «{prop.name}», занят={(prop.occupant != null ? prop.occupant.name : "нет")}, {h.distance:F1} м";
            else s = $"{h.collider.name} ({h.collider.transform.root.name}), {h.distance:F1} м";
            break;
        }
        lastAimed = "F6: " + s; lastAimedUntil = Time.time + 4f;
        Debug.Log("[отладка] " + lastAimed);
    }

    void OnGUI()
    {
        if (Time.time > lastAimedUntil) return;
        var st = new GUIStyle(GUI.skin.label) { fontSize = 16, normal = { textColor = Color.yellow } };
        GUI.Label(new Rect(12, 36, 1200, 26), lastAimed, st);
    }

    // F4: вручную вызывает HiderBot.NewRound() у всех ботов, чтобы проверять разнообразие выбора предметов до появления системы раундов.
    public static void NewBotRound()
    {
        HiderBot.NewRound();
        Debug.Log($"[отладка] F4: HiderBot.NewRound() для {HiderBot.All.Count} ботов");
    }

    public static void Swap()
    {
        HiderPlayer hider = HiderPlayer.All.Find(h => h.IsAlive && h.cam != null);   // локальный слот: у манекена камеры нет
        HunterPlayer hunter = HunterPlayer.All.Count > 0 ? HunterPlayer.All[0] : null;
        if (hider == null || hunter == null) return;
        bool toHunter = hider.controlled;
        hider.SetControlled(!toHunter);
        hunter.SetControlled(toHunter);
    }
}
