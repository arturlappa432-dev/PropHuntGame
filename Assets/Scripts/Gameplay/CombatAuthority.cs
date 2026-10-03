using System.Collections.Generic;
using UnityEngine;

public struct ShotResult
{
    public bool fired;
    public bool hit;
    public int damage;
    public float distance;
    public bool caught;
    public int nearMisses;
    public Vector3[] pelletEnds;
}

// Авторитетная логика стрельбы (docs/systems/hunter-combat.md, balance.md). Сейчас в том же процессе;
// при переходе на Fusion переезжает на хост, клиент шлёт только «выстрел из точки в направлении».
public class CombatAuthority : MonoBehaviour
{
    public static CombatAuthority Instance { get; private set; }

    // balance.md: 50 в упор, 15 на ~14 м, дальше 0; интервал ~1,2 с, одинаковый после попадания и промаха
    public const float DamageClose = 50f, DamageFar = 15f, MaxRange = 14f;
    public float fireInterval = 1.2f;

    // В документах не заданы, предложено (записано в open-questions.md): число и разброс дробинок, радиус свиста
    public int pellets = 9;
    public float spreadDegrees = 1.5f;
    public float nearMissRadius = 1.5f;        // hunter-combat.md: ~1,5 м
    public float nearMissMinDistance = 2f;     // вплотную свист не слышен (громкость 0)

    public Material hunterMaterial, gunMaterial, puffMaterial;
    public Transform hunterSpawn;

    void Awake() { Instance = this; }

    // Урон по расстоянию: линейно 50 -> 15 на 0..14 м, дальше 0. Округление половина вверх.
    public static int DamageAt(float distance)
    {
        if (distance > MaxRange) return 0;
        float d = Mathf.Lerp(DamageClose, DamageFar, Mathf.Clamp01(distance / MaxRange));
        return (int)System.Math.Round((double)d, System.MidpointRounding.AwayFromZero);
    }

    public ShotResult TryFire(HunterPlayer shooter, Vector3 origin, Vector3 forward)
    {
        var res = new ShotResult();
        if (Time.time < shooter.NextShotTime) return res;
        if (RoundState.Instance != null && RoundState.Instance.Phase == RoundPhase.Prep) return res;   // охотник заблокирован на подготовке
        shooter.NextShotTime = Time.time + fireInterval;   // штрафа за промах нет: тот же интервал
        res.fired = true;

        var basis = Quaternion.LookRotation(forward);
        float spread = Mathf.Tan(spreadDegrees * Mathf.Deg2Rad);
        var ends = new Vector3[pellets];
        var hitDist = new Dictionary<HiderPlayer, float>();

        for (int i = 0; i < pellets; i++)
        {
            Vector2 o = i == 0 ? Vector2.zero : Random.insideUnitCircle * spread;
            Vector3 dir = (basis * new Vector3(o.x, o.y, 1f)).normalized;
            var hits = Physics.RaycastAll(origin, dir, MaxRange, ~0, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            Vector3 end = origin + dir * MaxRange;
            foreach (var h in hits)
            {
                if (h.collider.GetComponentInParent<HunterPlayer>() == shooter) continue;
                end = h.point;
                var hider = h.collider.GetComponentInParent<HiderPlayer>();
                if (hider != null && hider.IsAlive)
                {
                    if (!hitDist.TryGetValue(hider, out float prev) || h.distance < prev) hitDist[hider] = h.distance;
                }
                break;   // первое чужое попадание останавливает дробинку (настоящий предмет = промах)
            }
            ends[i] = end;
        }
        res.pelletEnds = ends;

        // Попадания: один урон за выстрел на прячущегося, по ближайшей попавшей дробинке.
        foreach (var kv in hitDist)
        {
            int dmg = DamageAt(kv.Value);
            if (dmg <= 0) continue;
            res.hit = true; res.damage = dmg; res.distance = kv.Value;
            if (kv.Key.ApplyHit(dmg)) { kv.Key.BeginCatch(shooter); res.caught = true; }
        }

        // Near-miss: прячущийся, которого не задело, в пределах 14 м, дробинка прошла в радиусе nearMissRadius.
        foreach (var h in HiderPlayer.All)
        {
            if (!h.IsAlive || hitDist.ContainsKey(h)) continue;
            Vector3 c = h.BodyCenter;
            float hd = Vector3.Distance(origin, c);
            if (hd > MaxRange) continue;
            foreach (var e in ends)
                if (DistToSegment(c, origin, e) <= nearMissRadius)
                {
                    h.OnNearMiss(hd);
                    res.nearMisses++;
                    break;
                }
        }
        return res;
    }

    static float DistToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float t = ab.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude);
        return Vector3.Distance(p, a + ab * t);
    }

    // Пойманный возрождается в команде охотников у точки входа.
    public HunterPlayer SpawnHunter(bool controlled, Camera cam)
    {
        Vector3 pos = hunterSpawn != null ? hunterSpawn.position : Vector3.zero;
        float yaw = hunterSpawn != null ? hunterSpawn.eulerAngles.y : 0f;
        return HunterPlayer.Create(pos, yaw, cam, controlled, hunterMaterial, gunMaterial);
    }
}
