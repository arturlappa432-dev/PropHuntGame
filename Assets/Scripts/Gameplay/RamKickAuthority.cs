using UnityEngine;

public struct KickResult
{
    public bool fired;
    public bool hit;
}

// Авторитетная логика тарана и пинка (docs/systems/ram-kick.md). Сейчас в том же процессе;
// при переходе на Fusion переезжает на хост. Создаётся лениво, правок сцены не нужно.
// Числа, которых нет в balance.md, предложены мной и записаны в open-questions.md.
public class RamKickAuthority : MonoBehaviour
{
    static RamKickAuthority inst;
    public static RamKickAuthority Instance
    {
        get
        {
            if (inst == null) inst = new GameObject("RamKickAuthority").AddComponent<RamKickAuthority>();
            return inst;
        }
    }

    // --- Таран ---
    public PropTier ramMinTier = PropTier.Medium;   // «крупным и среднекрупным»
    public float ramBaseSpeed = 3f;                 // м/с к скорости откидывания сверх скорости тарана
    public float ramSpeedFactor = 1f;               // × скорость прячущегося
    public float ramLift = 3f;                      // м/с вверх
    public float ramReach = 0.08f;                  // запас контакта сверх радиуса капсулы охотника
    public float ramMinSpeed = 1f;                  // медленнее таран не засчитывается
    public float ramMinDot = 0.2f;                  // охотник должен быть впереди по ходу движения
    public float knockImmunity = 4f;                // balance.md: окно неуязвимости после подъёма ~3-5 с
    public float ragdollDuration = 3f;              // balance.md: жёсткий таймер ragdoll ~3 с
    public float getUpDuration = 0.8f;              // процедурный подъём без клипов (для демо: «простой подъём»)

    // --- Пинок ---
    public float kickRange = 1.8f;
    public float kickRadius = 0.35f;
    public float kickCooldown = 2f;                 // отдельный от дробовика
    public float kickSpeed = 7f;                    // м/с горизонтально (до множителя тира)
    public float kickLift = 3.5f;
    public float kickSpin = 9f;                     // рад/с хаотичного вращения
    public float stunDuration = 3f;                 // полный таймер оглушения от момента пинка
    public float flightMaxTime = 1.5f;              // «меньшее из двух»: осел или прошло столько
    public float realignDuration = 0.4f;            // balance/ram-kick: ~0,3-0,5 с

    static readonly float[] KickTierFactor = { 1.2f, 1f, 0.8f, 0.65f };
    static readonly float[] TierMass = { 0.5f, 2f, 8f, 20f };

    public static float KickFactor(PropTier t) => KickTierFactor[(int)t];
    public static float MassOf(PropTier t) => TierMass[(int)t];

    // Слои, на которых нет «настоящей» геометрии: игроки и их тела.
    public static int StaticMask
    {
        get
        {
            int m = ~0;
            int h = LayerMask.NameToLayer("Hunter"), o = LayerMask.NameToLayer("OwnBody");
            if (h >= 0) m &= ~(1 << h);
            if (o >= 0) m &= ~(1 << o);
            m &= ~(1 << 2);   // Ignore Raycast
            return m;
        }
    }

    // --- Пинок ---

    public KickResult TryKick(HunterPlayer kicker, Vector3 origin, Vector3 forward)
    {
        var res = new KickResult();
        if (kicker.Knocked || kicker.IsBlocked || Time.time < kicker.NextKickTime) return res;
        kicker.NextKickTime = Time.time + kickCooldown;
        res.fired = true;

        HiderPlayer victim = null;
        var hits = Physics.SphereCastAll(origin, kickRadius, forward, kickRange, ~0, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (var h in hits)
        {
            if (h.collider.GetComponentInParent<HunterPlayer>() == kicker) continue;
            victim = h.collider.GetComponentInParent<HiderPlayer>();
            break;   // первое чужое попадание заслоняет всё за ним
        }
        if (victim == null || !victim.IsAlive || victim.CurrentProp == null || victim.Stun != HiderPlayer.StunPhase.None) return res;

        res.hit = true;
        Vector3 dir = new Vector3(forward.x, 0f, forward.z);
        dir = dir.sqrMagnitude < 1e-4 ? kicker.transform.forward : dir.normalized;
        float f = KickFactor(victim.CurrentProp.tier);
        Vector3 launch = dir * kickSpeed * f + Vector3.up * kickLift * Mathf.Lerp(1f, f, 0.5f);
        Vector3 spin = Random.onUnitSphere * kickSpin;
        victim.BeginKicked(launch, spin, this);
        return res;
    }

    // --- Таран ---

    // Вызывается каждый кадр у прячущегося, пока идёт ускорение. Охотника двигаем явно (нокдаун с импульсом),
    // а не контактом капсул: CharacterController сам по себе охотника не толкает.
    public void TryRam(HiderPlayer ram)
    {
        if (ram.CurrentProp == null || ram.Stun != HiderPlayer.StunPhase.None || !ram.Sliding) return;
        if (ram.CurrentProp.tier < ramMinTier) return;
        if (RoundState.Instance != null && RoundState.Instance.Phase == RoundPhase.Prep) return;
        Vector3 vel = ram.HorizontalVelocity;
        float speed = vel.magnitude;
        if (speed < ramMinSpeed) return;
        Vector3 velDir = vel / speed;

        foreach (var hunter in HunterPlayer.All)
        {
            if (hunter.Knocked || Time.time < hunter.KnockImmuneUntil) continue;
            if (!Touches(ram.CurrentProp, hunter)) continue;
            Vector3 to = hunter.transform.position - ram.transform.position; to.y = 0f;
            Vector3 toDir = to.sqrMagnitude < 1e-4 ? velDir : to.normalized;
            float dot = Vector3.Dot(velDir, toDir);
            if (dot < ramMinDot) continue;   // задел боком/сзади: не таран
            Vector3 dir = (velDir * 0.7f + toDir * 0.3f).normalized;
            float tierK = ram.CurrentProp.tier == PropTier.Large ? 1.15f : 0.9f;
            float strength = Mathf.Lerp(0.6f, 1f, Mathf.InverseLerp(ramMinDot, 1f, dot));
            float horiz = (ramBaseSpeed + speed * ramSpeedFactor) * tierK * strength;
            Vector3 launch = dir * horiz + Vector3.up * ramLift * strength;
            hunter.Knock(launch, Random.onUnitSphere * 6f, this);
        }
    }

    // Контакт тела-предмета с капсулой охотника: берём ближайшие точки коллайдеров предмета к оси охотника.
    static bool Touches(Prop prop, HunterPlayer hunter)
    {
        float r = 0.25f + Instance.ramReach;
        Vector3 basePos = hunter.transform.position;
        foreach (var c in prop.Colliders)
        {
            if (c == null || !c.enabled) continue;
            for (float y = 0.15f; y <= 1.75f; y += 0.3f)
            {
                Vector3 p = basePos + Vector3.up * y;
                if (Vector3.Distance(c.ClosestPoint(p), p) <= r) return true;
            }
        }
        return false;
    }

    // --- Коррекция позиции (общая для подъёма охотника и самовыравнивания предмета) ---

    // Ближайшая свободная точка стояния («ноги») возле from: земля лучом вниз, капсула без пересечений со статикой.
    // Если ничего не найдено или улетели слишком далеко/упали, возвращаем fallback.
    public static Vector3 FindStandPoint(Vector3 from, float radius, float height, Vector3 fallback, float maxDistance = 25f)
    {
        if (from.y < -2f || (from - fallback).sqrMagnitude > maxDistance * maxDistance) return fallback;
        int mask = StaticMask;
        for (int ring = 0; ring <= 6; ring++)
        {
            int n = ring == 0 ? 1 : 12;
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2f / n;
                Vector3 c = from + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * ring * 0.3f;
                if (!Physics.Raycast(new Vector3(c.x, from.y + 0.3f, c.z), Vector3.down, out var hit, 4f, mask, QueryTriggerInteraction.Ignore)) continue;
                Vector3 foot = hit.point;
                if (!Physics.CheckCapsule(foot + Vector3.up * (radius + 0.03f), foot + Vector3.up * Mathf.Max(radius + 0.03f, height - radius),
                        radius * 0.95f, mask, QueryTriggerInteraction.Ignore)) return foot;
            }
        }
        return fallback;
    }
}
