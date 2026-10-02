using UnityEngine;

public enum PossessMode { Suck, Puff }

// Авторитетная логика вселения (docs/systems/possession.md). Сейчас живёт в том же процессе;
// при переходе на Fusion переезжает на хост, клиент шлёт только запрос «хочу в предмет X».
public class PossessionAuthority : MonoBehaviour
{
    public static PossessionAuthority Instance { get; private set; }
    public float pickDistance = 4f;          // предложено, калибровать
    public float repossessCooldown = 12f;    // диапазон 10-15, калибровать

    void Awake() { Instance = this; }

    // Первое вселение на старте: проверяет запрос и резервирует предмет за игроком (спор: достаётся первому).
    // Смены облика во время охоты сюда не попадают, см. CheckMorph.
    public bool TryRequest(HiderPlayer who, Prop target, out PossessMode mode, out string reason)
    {
        bool prep = RoundState.Instance == null || RoundState.Instance.Phase == RoundPhase.Prep;
        mode = prep ? PossessMode.Suck : PossessMode.Puff;
        reason = null;
        if (target == null) { reason = "нет цели"; return false; }
        if (!target.IsFree) { reason = "предмет занят"; return false; }
        if (!InRange(who, target)) { reason = "слишком далеко"; return false; }
        target.occupant = who;
        return true;
    }

    // Смена облика (копирование вида): ничего не резервирует, предмет-образец не трогается.
    public bool CheckMorph(HiderPlayer who, Prop sample, out string reason)
    {
        reason = null;
        if (sample == null) { reason = "нет цели"; return false; }
        if (Time.time < who.NextRepossessTime) { reason = $"смена через {who.NextRepossessTime - Time.time:0.0} с"; return false; }
        if (!InRange(who, sample)) { reason = "слишком далеко"; return false; }
        return true;
    }

    bool InRange(HiderPlayer who, Prop target)
    {
        Vector3 eye = who.EyePosition;
        return Vector3.Distance(eye, ClosestPoint(target, eye)) <= pickDistance + 0.5f;
    }

    // Автопревращение опоздавшего: ближайший свободный предмет, без проверки дистанции и кулдауна.
    public Prop ReserveNearestFree(HiderPlayer who)
    {
        Prop best = null; float bestD = float.MaxValue;
        foreach (var p in Prop.All)
        {
            if (!p.IsFree) continue;
            float d = (p.transform.position - who.transform.position).sqrMagnitude;
            if (d < bestD) { bestD = d; best = p; }
        }
        if (best != null) best.occupant = who;
        return best;
    }

    static Vector3 ClosestPoint(Prop p, Vector3 from)
    {
        var cols = p.Colliders;
        return cols != null && cols.Length > 0 ? cols[0].ClosestPoint(from) : p.transform.position;
    }
}
