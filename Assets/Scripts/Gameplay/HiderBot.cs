using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// Ввод бота: тот же набор «клавиш», что у игрока (движение, прыжок, Z, E) плюс направление взгляда. HiderPlayer читает его вместо клавиатуры.
public class BotInput
{
    public Vector2 move;          // x вправо, y вперёд относительно yaw (как WASD)
    public float yaw, pitch;
    bool jump, boost, possess;
    public void PressJump() { jump = true; }
    public void PressBoost() { boost = true; }
    public void PressPossess() { possess = true; }
    public bool TakeJump() { bool r = jump; jump = false; return r; }
    public bool TakeBoost() { bool r = boost; boost = false; return r; }
    public bool TakePossess() { bool r = possess; possess = false; return r; }
}

// «Личность» бота: только параметры, читов нет (bots.md).
[System.Serializable]
public class BotPersonality
{
    public string name = "Cautious";
    public float reactionDelay = 0.25f;   // сек от обнаружения (попадание / near-miss / пинок) до первого шага побега
    public float safeDistance = 10f;      // побег кончается, когда охотник не виден И дальше этой дистанции (а не по таймеру)
    public float ramChance = 0.15f;       // шанс ответить тараном при побеге (только Medium/Large с готовым Z и охотником рядом)
    public float relocateEvery = 45f;     // средний интервал «естественного» перемещения, сек (0 = не перемещается)
    public float noticeRange = 16f;       // дальше этого охотника бот не замечает; ближе и в прямой видимости — не двигается сам

    public static BotPersonality Cautious() => new BotPersonality { name = "Cautious", reactionDelay = 0.25f, safeDistance = 10f, ramChance = 0.15f, relocateEvery = 45f, noticeRange = 16f };
    public static BotPersonality Bold() => new BotPersonality { name = "Bold", reactionDelay = 0.6f, safeDistance = 7f, ramChance = 0.7f, relocateEvery = 20f, noticeRange = 8f };
}

// Бот-прячущийся (docs/systems/bots.md). Три слоя:
//  1. Восприятие (Perceive, каждый кадр в активных состояниях): видит ли охотник бота — симметричный луч из глаз охотника к центру
//     и верху тела бота, свои коллайдеры обеих сторон не считаются помехой. Отсюда «видим/не видим», последнее место охотника,
//     сколько секунд подряд спокойно (не видим и далеко). Решения читают только эти данные, своих лучей не пускают.
//  2. Решения (Decide, по тику): конечный автомат Seek -> Freeze (основное) -> изредка Relocate; Reacting -> Flee/Ram только
//     при реальном обнаружении (попадание, near-miss, пинок). Побег кончается только реальной безопасностью; дойти до точки
//     на виду у охотника — значит выбрать следующую, а не замереть. Загнан в угол — таран или рывок с прыжком мимо охотника.
//  3. Движение (Act, каждый кадр): путь по NavMesh своего типа агента (мелкие предметы — HiderSmall с уровнями полок и
//     переходами Jump), следование за точкой впереди по пути (pure pursuit) с плавной синусоидой вбок при побеге — и на земле,
//     и в воздухе. Ввод идёт через BotInput и правила HiderPlayer (скорость, прыжок, Z, вселение) — как у игрока.
// Приближение охотника само по себе побег не вызывает (маскировка — главное).
[DefaultExecutionOrder(-10)]
[RequireComponent(typeof(HiderPlayer))]
public class HiderBot : MonoBehaviour
{
    public static readonly List<HiderBot> All = new List<HiderBot>();

    public enum State { Seek, Possessing, Freeze, Relocate, Reacting, Flee, Ram, Settle, Morphing, Look }

    public BotPersonality personality = BotPersonality.Cautious();
    public int seed = 1;

    // Оптимизация (решено в bots.md, на поведение не влияет): пересчёт решений реже, когда охотник далеко.
    public float farTick = 0.3f;         // ~0,3 с вдали
    public float nearRange = 8f;         // ближе этого до охотника — решения каждый кадр; дальше — раз в farTick
    public float pickReach = 3.2f;       // насколько близко к предмету подходит бот при выборе (меньше pickDistance=4 м)
    public float ramRange = 6f;
    public float arriveDist = 0.35f;
    public float finalArriveDist = 0.08f;
    public float morphWindow = 1.5f;

    public float fleeFailsafe = 25f;      // страховка: после этого побег может кончиться, только если охотник сейчас не видит
    public float fleeUnseenHold = 1.25f;  // сколько секунд подряд охотник не видит бота (и далеко), чтобы побег считался законченным
    public float corneredRange = 3f;      // застрял/некуда бежать при охотнике ближе этого — «загнан в угол»
    public float alertWindow = 8f;        // после побега столько секунд бот настороже: охотник снова близко на виду — бежать дальше

    // Виляние при побеге: смещение точки следования вбок по синусу (метры), а не дёрганье клавиш A/D.
    public float weaveAmplitude = 0.6f;   // м, максимум; в узком проходе урезается под свободное место
    public float weavePeriod = 1.6f;      // с на полный зигзаг (длина волны ~6,4 м при 4 м/с)
    public float weaveMargin = 0.2f;      // запас до стены сверх радиуса тела
    public float pursuitLookahead = 1.0f; // м: точка следования впереди по пути (больше — сильнее гасит виляние)

    public float noiseAmplitude = 2.2f;   // разброс оценки выбора предмета, перемешивается каждый раунд
    public static int RoundSalt = System.Environment.TickCount;   // новый раунд — новая соль (ReseedRound)

    // Мелкие тела (Tiny/Small) ходят по навмешу HiderSmall: пол + все уровни полок, связи Jump (ShelfNavBuilder).
    public float smallMaxRadius = 0.13f, smallMaxHeight = 0.34f;
    public float shelfBonus = 1.0f;       // предпочтение полок как укрытия для мелких тел (там много таких же предметов)

    public State Current { get; private set; } = State.Seek;
    public int TickCount { get; private set; }
    public float DetectedAt { get; private set; } = -100f;
    public float FleeStartedAt { get; private set; } = -100f;
    public float SettledAt { get; private set; } = -100f;
    public HiderPlayer.DetectKind LastDetect { get; private set; }
    public int RamCount { get; private set; }
    public float FirstFreezeAt { get; private set; } = -1f;
    public readonly List<string> History = new List<string>();   // для тестов: события выбора предмета
    public string DebugSeek { get; private set; } = "";   // для тестов: чем кончился последний выбор предмета
    public HiderPlayer Hider => hider;
    public Vector3 Dest => dest;
    public Vector3[] PathCorners => corners;

    // Отладка/тесты
    public float FleeEndUnseen;           // сколько секунд подряд было спокойно к концу последнего побега
    public int UnseenResets;              // сколько раз накопленное спокойствие сбрасывалось во время побега
    public string FleeEndReason = "";     // safe / failsafe
    public int FleeLegs, CorneredCount, ResumedFlees, ShelfJumpsOk, ShelfJumpsFail, HoldCount, JumpChecks, EvadeJumps;
    public string JumpBlockedBy = "";
    public bool LinkActive => linkIdx >= 0;
    public float WeaveOffset { get; private set; }   // текущее смещение точки следования вбок, м

    // ---------- Восприятие (слой 1) ----------
    public bool ThreatVisible { get; private set; }
    public float SeenDist { get; private set; } = float.MaxValue;     // до видимого охотника
    public float ThreatDist { get; private set; } = float.MaxValue;   // до ближайшего охотника (как бот его «слышит»)
    public float LastSeenAt { get; private set; } = -100f;
    public Vector3 LastSeenPos { get; private set; }
    public float CalmFor => calmSince < 0f ? 0f : Time.time - calmSince;   // не видим и далеко непрерывно, с
    float calmSince = -1f;

    HiderPlayer hider;
    BotInput input;
    System.Random rng;
    float tickTimer, stateSince, nextRelocateAt, reactAt, ramUntil, lookUntil, lookBaseYaw, weavePhase, nextJumpAt, stuckT;
    float weaveAmp, weaveCenter, lastFleeEndAt = -100f, nextRethinkAt, cornerUntil;
    int relocateRetries;
    bool debugFixedDest;   // тест: бежать в заданную точку до конца, без пересборки маршрута
    bool relocAfterFlee;
    bool wantMorphCheck, boostIssued;
    Vector3 lastPos, threatPos;
    bool haveThreat;
    Vector3 fleeOrigin;
    float speedFactor = 1f;
    Vector3[] corners = System.Array.Empty<Vector3>();
    int ci;
    Prop seekTarget, morphTarget;
    readonly HashSet<Prop> rejected = new HashSet<Prop>();
    Vector3 dest;
    int seekTries;
    string lastPlanFail = "";
    bool dropping, stuckFailed;
    Vector3 dropTarget;
    float dropT;
    int stuckStrikes;
    static readonly RaycastHit[] rayBuf = new RaycastHit[16];
    static readonly Collider[] overlapBuf = new Collider[32];

    void Awake() { hider = GetComponent<HiderPlayer>(); }

    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }

    void Start()
    {
        input = new BotInput();
        hider.botInput = input;
        hider.Detected += OnDetected;
        ReseedRound();
        // небольшая индивидуальная разница скорости между ботами, чтобы не были клонами
        speedFactor = 0.94f + (float)rng.NextDouble() * 0.12f;
        hider.walkSpeed *= speedFactor;
        input.yaw = hider.transform.eulerAngles.y;
        ScheduleRelocate();
        SetState(State.Seek);
        tickTimer = (float)rng.NextDouble() * farTick;   // разводим тики ботов по кадрам
    }

    // Новый раунд: другой разброс оценки «естественности», выбор предметов не повторяется из раунда в раунд.
    public void ReseedRound()
    {
        rng = new System.Random(unchecked(seed * 7919 + 13 + RoundSalt * 104729));
    }

    public static void NewRound() { RoundSalt = unchecked(RoundSalt * 1103515245 + 12345 + System.Environment.TickCount); foreach (var b in All) b.ReseedRound(); }

    void OnDestroy() { if (hider != null) hider.Detected -= OnDetected; }

    // ---------- Событие обнаружения ----------

    void OnDetected(HiderPlayer.DetectKind kind)
    {
        if (hider.Caught || hider.CurrentProp == null) return;
        DetectedAt = Time.time;
        LastDetect = kind;
        Perceive();   // свежая картина: где охотник, видит ли
        if (ThreatVisible) { threatPos = LastSeenPos; haveThreat = true; }
        if (Current == State.Reacting || Current == State.Flee || Current == State.Ram) return;
        // небольшой джиттер реакции (±20%), чтобы не ощущалась метрономом
        reactAt = Time.time + personality.reactionDelay * (0.8f + (float)rng.NextDouble() * 0.4f);
        input.move = Vector2.zero;
        SetState(State.Reacting);
    }

    // ---------- Основной цикл ----------

    void LateUpdate()
    {
        if (History.Count > 80) History.RemoveRange(60, History.Count - 80);
    }

    bool Active => Current == State.Flee || Current == State.Look || Current == State.Reacting || Current == State.Ram || Current == State.Relocate;

    void Update()
    {
        if (hider == null || input == null || hider.Caught) return;

        // Оглушён пинком / идёт засасывание: ввода нет.
        if (hider.Stun != HiderPlayer.StunPhase.None) { input.move = Vector2.zero; corners = System.Array.Empty<Vector3>(); linkIdx = -1; return; }
        if (hider.Busy) { input.move = Vector2.zero; return; }

        tickTimer -= Time.deltaTime;
        bool tick = tickTimer <= 0f;
        // Восприятие каждый кадр, пока бот в активном состоянии (таймер спокойствия не зависит от частоты тиков); иначе по тику.
        if (Active || tick) Perceive();

        // Реакция на обнаружение отсчитывается каждый кадр, а не по тику: частота решений на поведение не влияет.
        if (Current == State.Reacting && Time.time >= reactAt) StartFlee();

        if (tick)
        {
            Decide();
            tickTimer = ThreatDist < nearRange || Current == State.Flee ? 0f : farTick;
        }
        Act();
        if (Diag != null && (Current == State.Flee || Current == State.Look || Current == State.Reacting || Current == State.Ram || linkIdx >= 0)) DiagFrame();
    }

    void SetState(State s) { Current = s; stateSince = Time.time; }

    // Луч из глаз охотника к точке тела бота. Помеха — любая геометрия, кроме собственных коллайдеров бота
    // (CharacterController корня на слое Default) и охотника. Раньше луч упирался в свою же капсулу бота почти всегда.
    bool HunterSees(HunterPlayer h, Vector3 target)
    {
        Vector3 o = h.EyePosition, d = target - o;
        float len = d.magnitude;
        if (len < 0.01f) return true;
        int n = Physics.RaycastNonAlloc(o, d / len, rayBuf, len, RamKickAuthority.StaticMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var t = rayBuf[i].collider.transform;
            if (t.IsChildOf(hider.transform) || t.IsChildOf(h.transform)) continue;
            return false;
        }
        return true;
    }

    void Perceive()
    {
        bool vis = false;
        float seen = float.MaxValue, nearest = float.MaxValue;
        Vector3 seenPos = default;
        Vector3 c = hider.BodyCenter;
        Vector3 top = hider.Stun != HiderPlayer.StunPhase.None ? c : hider.transform.position + Vector3.up * hider.BodyHeight * 0.9f;
        foreach (var h in HunterPlayer.All)
        {
            float d = Vector3.Distance(h.transform.position, hider.transform.position);
            nearest = Mathf.Min(nearest, d);
            if (d > 60f || d >= seen) continue;
            if (HunterSees(h, c) || HunterSees(h, top)) { vis = true; seen = d; seenPos = h.transform.position; }
        }
        if (vis) { LastSeenPos = seenPos; LastSeenAt = Time.time; }
        ThreatVisible = vis; SeenDist = seen; ThreatDist = nearest;
        bool calm = !vis && nearest >= personality.safeDistance;
        if (calm) { if (calmSince < 0f) calmSince = Time.time; }
        else { if (calmSince >= 0f && Current == State.Flee) UnseenResets++; calmSince = -1f; }
    }

    // Решения (по тику).
    void Decide()
    {
        TickCount++;
        if (hider.CurrentProp != null && (Current == State.Seek || Current == State.Possessing)) { EnterFreeze(); return; }
        switch (Current)
        {
            case State.Seek: DecideSeek(); break;
            case State.Possessing:
                if (Time.time - stateSince > 2f) { History.Add($"t={Time.time:F1}: не вселился за 2 с в {(seekTarget != null ? seekTarget.ModelId : "?")}: Target={(hider.Target != null ? hider.Target.ModelId : "null")}, свободен={(seekTarget != null && seekTarget.IsFree)}, до цели {(seekTarget != null ? Vector3.Distance(hider.EyePosition, PropCenter(seekTarget)) : 0):F2} м, бот {hider.transform.position}, стоянка {dest}"); if (seekTarget != null) rejected.Add(seekTarget); corners = System.Array.Empty<Vector3>(); SetState(State.Seek); }
                break;
            case State.Freeze: DecideFreeze(); break;
            case State.Relocate: DecideRelocate(); break;
            case State.Flee: DecideFlee(); break;
            case State.Ram: DecideRam(); break;
            case State.Settle: DecideSettle(); break;
            case State.Look: DecideLook(); break;
            case State.Morphing:
                if (hider.CurrentProp != null && morphTarget != null && hider.CurrentProp.ModelId == morphTarget.ModelId) EnterFreeze();
                else if (Time.time - stateSince > morphWindow) EnterFreeze();
                break;
        }
    }

    // Исполнение (каждый кадр): шаг по пути, взгляд, нажатия.
    void Act()
    {
        switch (Current)
        {
            case State.Possessing:
                if (seekTarget != null) { AimAtProp(seekTarget); if (hider.Target == seekTarget) input.PressPossess(); }
                input.move = Vector2.zero;
                break;
            case State.Morphing:
                if (morphTarget != null) { AimAtProp(morphTarget); if (hider.Target == morphTarget) input.PressPossess(); }
                input.move = Vector2.zero;
                break;
            case State.Seek:
            case State.Relocate:
                Follow(false);
                break;
            case State.Flee:
                Follow(true);
                EvadeJump();
                break;
            case State.Look:
                // короткий «осмотр»: стоит и поворачивает голову, не двигаясь
                input.move = Vector2.zero;
                input.yaw = lookBaseYaw + Mathf.Sin((Time.time - stateSince) * 3.2f) * 70f;
                break;
            case State.Ram:
                RamSteer();
                break;
            default:
                input.move = Vector2.zero;
                break;
        }
    }

    // ---------- Выбор предмета (старт) ----------

    void DecideSeek()
    {
        if (hider.CurrentProp != null) { EnterFreeze(); return; }
        if (stuckFailed)
        {
            History.Add($"t={Time.time:F1}: застрял на пути к {(seekTarget != null ? seekTarget.ModelId : "?")}, бот {hider.transform.position}");
            if (seekTarget != null) rejected.Add(seekTarget);
            stuckFailed = false; stuckStrikes = 0; seekTarget = null; corners = System.Array.Empty<Vector3>();
        }
        if (corners.Length > 0 && seekTarget != null && seekTarget.IsFree)
        {
            if (ci >= corners.Length || Vector3.Distance(Flat(hider.transform.position), Flat(dest)) < finalArriveDist + 0.05f) { SetState(State.Possessing); input.move = Vector2.zero; }
            return;
        }
        // Новый выбор: лучшие по «естественности» кандидаты, к которым есть путь и вид.
        int nStand = 0, nPath = 0;
        var cands = ScoreCandidates();
        for (int i = 0; i < Mathf.Min(6, cands.Count); i++)
        {
            var p = cands[i].p;
            if (!FindStandpoint(p, out var stand)) { rejected.Add(p); nStand++; History.Add($"t={Time.time:F1}: {p.ModelId} y={p.transform.position.y:F2} — нет места с видом"); continue; }
            if (!PlanPath(stand)) { rejected.Add(p); nPath++; History.Add($"t={Time.time:F1}: {p.ModelId} y={p.transform.position.y:F2} — нет пути ({lastPlanFail})"); continue; }
            seekTarget = p; dest = stand;
            History.Add(DebugSeek = $"t={Time.time:F1}: выбран {p.ModelId} y={p.transform.position.y:F2} (рейтинг {cands[i].score:F2}), отвергнуто: без вида {nStand}, без пути {nPath}, кандидатов {cands.Count}");
            return;
        }
        // ничего подходящего: сбрасываем чёрный список и ждём; если подготовка кончится, автовселение (RoundState) выдаст предмет
        if (cands.Count == 0)
        {
            if (History.Count < 60) History.Add(DebugSeek = $"t={Time.time:F1}: свободных кандидатов не осталось, сброс отвергнутых");
            rejected.Clear();
        }
        seekTarget = null; corners = System.Array.Empty<Vector3>();
        seekTries++;
    }

    // Кандидаты стартового предмета с оценкой: естественность − расстояние + шум раунда (noiseAmplitude, rng пересоздаётся каждый раунд).
    public List<(Prop p, float score)> ScoreCandidates()
    {
        var cands = new List<(Prop p, float score)>();
        foreach (var p in Prop.All)
        {
            if (!p.IsFree || rejected.Contains(p)) continue;
            float sc = Naturalness(p) - 0.04f * Vector3.Distance(p.transform.position, hider.transform.position) + (float)rng.NextDouble() * noiseAmplitude;
            foreach (var o in All) if (o != this && o.seekTarget == p && (o.Current == State.Seek || o.Current == State.Possessing)) sc -= 3f;   // боты делят между собой, кто куда идёт (между собой, не про игроков)
            cands.Add((p, sc));
        }
        cands.Sort((a, b) => b.score.CompareTo(a.score));
        return cands;
    }

    // Точка на полу (NavMesh своего агента) вокруг предмета: в досягаемости вселения, с прямым лучом из глаз к предмету.
    bool FindStandpoint(Prop p, out Vector3 stand)
    {
        stand = default;
        Vector3 c = PropCenter(p);
        float eyeH = hider.EyePosition.y - hider.transform.position.y;
        Vector3 start = hider.transform.position;
        float bestLen = float.MaxValue;
        bool ok = false;
        var f = Filter;
        for (int ring = 0; ring < 3; ring++)
        {
            float r = pickReach * (0.55f + 0.2f * ring);
            for (int k = 0; k < 14; k++)
            {
                float a = k * Mathf.PI * 2f / 14f + ring * 0.2f;
                Vector3 want = new Vector3(c.x + Mathf.Cos(a) * r, 0f, c.z + Mathf.Sin(a) * r);
                if (!NavMesh.SamplePosition(new Vector3(want.x, 0.1f, want.z), out var nh, 0.6f, f) || nh.position.y > 0.1f) continue;
                Vector3 eye = nh.position + Vector3.up * eyeH;
                if (Vector3.Distance(eye, p.Colliders[0].bounds.ClosestPoint(eye)) > pickReach + 0.5f) continue;
                if (!ClearSight(eye, p)) continue;
                float len = (nh.position - start).sqrMagnitude;
                if (len < bestLen) { bestLen = len; stand = nh.position; ok = true; }
            }
            if (ok) return true;
        }
        return false;
    }

    // Луч из глаз к центру предмета первым попадает именно в него (чужое тело перед ним — нет).
    bool ClearSight(Vector3 eye, Prop p)
    {
        Vector3 c = PropCenter(p);
        Vector3 d = c - eye;
        var hits = Physics.RaycastAll(eye, d.normalized, d.magnitude + 0.5f, ~0, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (var h in hits)
        {
            if (h.collider.GetComponentInParent<HiderPlayer>() == hider) continue;
            return h.collider.GetComponentInParent<Prop>() == p;
        }
        return false;
    }

    static Vector3 PropCenter(Prop p)
    {
        return p.Colliders != null && p.Colliders.Length > 0 && p.Colliders[0] != null ? p.Colliders[0].bounds.center : p.transform.position;
    }

    // ---------- «Естественность» места ----------

    public static float Naturalness(Prop p)
    {
        int same = 0, any = 0;
        Vector3 c = p.transform.position;
        foreach (var q in Prop.All)
        {
            if (q == p) continue;
            if ((q.transform.position - c).sqrMagnitude > 2.5f * 2.5f) continue;
            any++;
            if (q.ModelId == p.ModelId) same++;
        }
        float score = Mathf.Min(same, 3) * 1.0f + Mathf.Min(any, 5) * 0.2f;
        // Предмет на полу посреди прохода (вокруг нет стен и полок) выдаёт себя; на полке и у стены — нормально.
        bool floorProp = c.y - p.height * 0.5f < 0.1f;
        if (floorProp) score += EnclosedSides(c) >= 2 ? 0.5f : -1.5f;
        return score;
    }

    // Сколько из 8 горизонтальных направлений упирается в статику (стену/стеллаж) в пределах 1,3 м.
    static int EnclosedSides(Vector3 c)
    {
        int n = 0;
        int mask = RamKickAuthority.StaticMask;
        for (int i = 0; i < 8; i++)
        {
            Vector3 d = Quaternion.Euler(0, i * 45f, 0) * Vector3.forward;
            if (Physics.Raycast(c + Vector3.up * 0.15f, d, out var hit, 1.3f, mask, QueryTriggerInteraction.Ignore) && hit.collider.GetComponentInParent<Prop>() == null) n++;
        }
        return n;
    }

    // Сколько предметов этой модели вокруг точки (для решения о смене облика на месте).
    static int LocalFit(Vector3 pos, string model, Prop except)
    {
        int n = 0;
        foreach (var q in Prop.All)
        {
            if (q == except || q.ModelId != model) continue;
            if ((q.transform.position - pos).sqrMagnitude <= 2.5f * 2.5f) n++;
        }
        return n;
    }

    // ---------- Замереть ----------

    void EnterFreeze()
    {
        corners = System.Array.Empty<Vector3>();
        linkIdx = -1;
        input.move = Vector2.zero;
        SetState(State.Freeze);
        SettledAt = Time.time;
        if (FirstFreezeAt < 0f) FirstFreezeAt = Time.time;
    }

    void ScheduleRelocate()
    {
        nextRelocateAt = personality.relocateEvery <= 0f ? float.MaxValue : Time.time + personality.relocateEvery * (0.5f + (float)rng.NextDouble());
    }

    bool Alert => Time.time - lastFleeEndAt < alertWindow;

    void DecideFreeze()
    {
        if (RoundState.Instance != null && RoundState.Instance.Phase == RoundPhase.Prep) return;

        // Повторное превращение по ситуации: после перемещения/побега, когда кулдаун смены готов.
        if (wantMorphCheck && Time.time >= hider.NextRepossessTime && TryStartMorph()) return;

        if (Time.time < nextRelocateAt) return;
        // Естественное перемещение: только со своего навмеша (пол или полка у мелких), если охотник не виден вблизи и недавно не было обнаружения.
        bool onNav = SampleHere(out _);
        bool hunterSeen = ThreatVisible && SeenDist <= personality.noticeRange;
        if (!onNav || hunterSeen || Time.time - DetectedAt < 6f || !StartRelocate()) { nextRelocateAt = Time.time + 3f; }
    }

    // ---------- Перемещение ----------

    bool StartRelocate(bool afterFlee = false)
    {
        // Цель: стоянка рядом с «естественным» кластером однотипных предметов в пределах 10 м (мелким телам — и на полке рядом с ними).
        Vector3 pos = hider.transform.position;
        var scored = new List<(Prop q, float s)>();
        foreach (var q in Prop.All)
        {
            if (q == hider.CurrentProp) continue;
            float d = Vector3.Distance(q.transform.position, pos);
            if (d < 2f || d > 10f) continue;
            if (afterFlee && Vector3.Distance(q.transform.position, fleeOrigin) < 3f) continue;   // новое место, не то же самое
            if (LocalFit(q.transform.position, q.ModelId, q) < 2) continue;
            float s = Naturalness(q) - 0.05f * d + (float)rng.NextDouble() * 1.0f;
            if (afterFlee && haveThreat && Physics.Linecast(threatPos + Vector3.up * 1.6f, q.transform.position + Vector3.up * 0.2f, RamKickAuthority.StaticMask, QueryTriggerInteraction.Ignore)) s += 1.5f;   // скрытее от охотника
            if (SmallBody && OnShelf(q)) s += shelfBonus;
            scored.Add((q, s));
        }
        scored.Sort((a, b) => b.s.CompareTo(a.s));
        for (int i = 0; i < Mathf.Min(4, scored.Count); i++)
        {
            var q = scored[i].q;
            bool shelf = SmallBody && OnShelf(q);
            Vector3 stand;
            if (shelf) { if (!ShelfSpotNear(q, out stand)) continue; }
            else if (!FindStandpoint(q, out stand)) continue;
            if (!PlanPath(stand)) continue;
            dest = stand;
            morphTarget = q;
            wantMorphCheck = true;
            if (Current != State.Relocate) relocateRetries = 0;
            relocAfterFlee = afterFlee;
            SetState(State.Relocate);
            return true;
        }
        return false;
    }

    void DecideRelocate()
    {
        // Увидели охотника близко: после недавнего побега (настороже) — бежать дальше; иначе замереть, где стоим (двигаться на виду нельзя).
        if (ThreatVisible && SeenDist <= personality.noticeRange)
        {
            if (Alert && SeenDist <= personality.safeDistance && linkIdx < 0) { ResumeFlee(); return; }
            if (linkIdx < 0) { EnterFreeze(); ScheduleRelocate(); return; }   // посреди прыжка на полку не замираем
        }
        if (ci >= corners.Length)
        {
            // упёрлись (другой предмет занял проход, не вышел прыжок на полку) — ещё одна попытка к другому месту, потом затаиться
            bool stuck = stuckFailed;
            stuckFailed = false;
            if (stuck && relocateRetries++ < 2 && StartRelocate(relocAfterFlee)) return;
            SetState(State.Settle); ScheduleRelocate();
        }
    }

    // ---------- Побег ----------

    void StartFlee()
    {
        boostIssued = false;
        FleeEndReason = "";
        debugFixedDest = false;
        FleeLegs = 0;
        weavePhase = (float)rng.NextDouble() * 6.28f;
        weaveAmp = weaveAmplitude * 0.5f;   // виляние видно с первых шагов (в воздухе размах только уменьшается)
        nextJumpAt = Time.time + 0.2f + (float)rng.NextDouble() * 0.5f;
        FleeStartedAt = Time.time;
        // Охотник: виден сейчас, иначе последнее известное положение (недавнее).
        haveThreat = false;
        if (ThreatVisible || Time.time - LastSeenAt < 4f) { threatPos = LastSeenPos; haveThreat = true; }

        // Таран: Medium/Large, Z готов, охотник близко и виден, шанс по личности.
        if (CanRam() && rng.NextDouble() < personality.ramChance) { BeginRam(); return; }
        BeginRun();
    }

    void ResumeFlee() { ResumedFlees++; StartFlee(); }

    bool CanRam()
    {
        var prop = hider.CurrentProp;
        return haveThreat && prop != null && RamKickAuthority.Instance != null && prop.tier >= RamKickAuthority.Instance.ramMinTier
            && hider.Boost == HiderPlayer.BoostPhase.Ready && Vector3.Distance(threatPos, hider.transform.position) <= ramRange;
    }

    void BeginRam()
    {
        linkIdx = -1;
        SetState(State.Ram);
        ramUntil = Time.time + hider.boostDuration;
        RamCount++;
        input.PressBoost();
    }

    void BeginRun()
    {
        fleeOrigin = hider.transform.position;
        SetState(State.Flee);
        nextRethinkAt = Time.time + 1.5f;
        if (PickHidePoint(false, out var d) || PickHidePoint(true, out d)) dest = d;
        else Cornered();
    }

    void DecideFlee()
    {
        // Z при побеге (если предмет есть и шкала готова): ускорение — часть тех же правил HiderPlayer
        if (!boostIssued && hider.Boost == HiderPlayer.BoostPhase.Ready) { input.PressBoost(); boostIssued = true; }
        if (ThreatVisible) { threatPos = LastSeenPos; haveThreat = true; }

        // Единственный «хороший» конец побега: устойчиво спокойно (не видим и далеко fleeUnseenHold секунд подряд).
        if (CalmFor >= fleeUnseenHold && linkIdx < 0) { FleeEndUnseen = CalmFor; EndFlee("safe"); BeginLook(); return; }
        // Страховка от бесконечного бега — только если охотник сейчас не видит; на виду бот не замирает никогда.
        if (Time.time - FleeStartedAt > fleeFailsafe && !ThreatVisible && linkIdx < 0) { EndFlee("failsafe"); SetState(State.Settle); return; }
        if (linkIdx >= 0 || dropping) return;   // манёвр прыжка на полку/спуска не прерываем
        if (!hider.Grounded) return;             // маршрут в воздухе не меняем: поворот посреди прыжка уводил дугу на стеллаж

        // Путь стал вести к охотнику (он сменил позицию) — пересобрать маршрут.
        if (ThreatVisible && Time.time >= nextRethinkAt && ci < corners.Length && !debugFixedDest)
        {
            nextRethinkAt = Time.time + 1.5f;
            if (MinDistToPath(corners, threatPos, ci) < 1.5f && (PickHidePoint(false, out var r) || PickHidePoint(true, out r))) dest = r;
        }

        if (ci >= corners.Length)
        {
            debugFixedDest = false;
            bool stuck = stuckFailed;
            stuckFailed = false;
            if (Time.time < cornerUntil) return;
            if (stuck && ThreatVisible && SeenDist < corneredRange) { Cornered(); return; }
            // Добежал до точки, а небезопасно: следующая точка с учётом свежей позиции охотника (замирать на виду нельзя).
            if (PickHidePoint(false, out var d)) { dest = d; FleeLegs++; }
            // Не видим, а все пути ведут мимо охотника (напр. бот в ячейке полки, охотник рядом): затаиться, не выдавать себя бегом.
            else if (!ThreatVisible) { cornerUntil = Time.time + 0.5f; HoldCount++; }
            else if (PickHidePoint(true, out d)) { dest = d; FleeLegs++; }
            else Cornered();
        }
    }

    void EndFlee(string why) { FleeEndReason = why; lastFleeEndAt = Time.time; }

    // Загнан в угол: таран, если возможен; иначе рывок — Z и прыжок, точка подальше даже ценой пути мимо охотника.
    void Cornered()
    {
        CorneredCount++;
        cornerUntil = Time.time + 0.6f;
        if (CanRam()) { BeginRam(); return; }
        if (hider.Boost == HiderPlayer.BoostPhase.Ready) { input.PressBoost(); boostIssued = true; }
        if (PickHidePoint(true, out var d)) dest = d;
        // рывок с прыжком — сразу в сторону нового пути и только если дуга не уходит на стеллаж (иначе бот садился на его верх)
        if (hider.Grounded && hider.transform.position.y < 0.1f && corners.Length > 1)
        {
            Vector3 to = Flat(corners[Mathf.Min(1, corners.Length - 1)] - hider.transform.position);
            if (to.sqrMagnitude > 0.01f)
            {
                float yaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
                if (JumpCorridorClear(yaw, FlightLength())) { input.yaw = yaw; input.PressJump(); }
            }
        }
    }

    void DecideRam()
    {
        bool hunterDown = false;
        foreach (var h in HunterPlayer.All) if (h.Knocked) hunterDown = true;
        if (Time.time - stateSince < 0.15f) return;   // Z ещё не обработан HiderPlayer
        if (hunterDown || hider.Boost != HiderPlayer.BoostPhase.Active || Time.time > ramUntil) BeginRun();
    }

    void RamSteer()
    {
        // Прямо на охотника, пока он в видимости; Z уже нажат.
        if (ThreatVisible) threatPos = LastSeenPos;
        Vector3 d = threatPos - hider.transform.position; d.y = 0f;
        if (d.sqrMagnitude > 0.01f) input.yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        input.move = Vector2.up;
    }

    void BeginLook()
    {
        corners = System.Array.Empty<Vector3>();
        input.move = Vector2.zero;
        lookBaseYaw = input.yaw;
        lookUntil = Time.time + 1.0f + (float)rng.NextDouble() * 0.8f;
        SetState(State.Look);
    }

    // После осмотра — не затаиваться где стоим, а уйти в НОВОЕ место, скрытое от охотника; не вышло — затаиться тут.
    void DecideLook()
    {
        if (ThreatVisible && SeenDist <= personality.safeDistance) { ResumeFlee(); return; }   // охотник снова рядом на виду
        if (Time.time < lookUntil) return;
        if (!StartRelocate(true)) SetState(State.Settle);
    }

    // Точка укрытия: вне видимости охотника, подальше, естественная, с путём в обход охотника. Мелким телам — и уровни полок.
    // desperate: путь мимо охотника допустим (иначе бежать некуда).
    bool PickHidePoint(bool desperate, out Vector3 best)
    {
        best = default;
        Vector3 pos = hider.transform.position;
        if (!SampleHere(out var from) && !SampleFloor(pos, 4f, out from)) return false;
        var f = Filter;
        float bestScore = float.MinValue;
        bool found = false;
        var path = new NavMeshPath();
        int nFloor = desperate ? 20 : 14;
        int nShelf = SmallBody ? 10 : 0;
        for (int i = 0; i < nFloor + nShelf; i++)
        {
            Vector3 cand;
            bool shelf = i >= nFloor;
            if (!shelf)
            {
                float a = (float)rng.NextDouble() * Mathf.PI * 2f, r = 3f + (float)rng.NextDouble() * 10f;
                Vector3 want = from + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r;
                if (!NavMesh.SamplePosition(new Vector3(want.x, 0.05f, want.z), out var nh, 1.5f, f) || nh.position.y > 0.1f) continue;
                cand = nh.position;
            }
            else if (!RandomShelfSpot(from, out cand)) continue;
            if (Flat(cand - pos).magnitude < 2.5f) continue;   // бежать надо куда-то, не на месте
            if (!NavMesh.CalculatePath(from, cand, f, path) || path.status != NavMeshPathStatus.PathComplete) continue;
            float len = PathLength(path.corners);
            float s = -0.08f * len;
            if (haveThreat)
            {
                bool hidden = Physics.Linecast(threatPos + Vector3.up * 1.6f, cand + Vector3.up * 0.15f, RamKickAuthority.StaticMask, QueryTriggerInteraction.Ignore);
                s += hidden ? 3f : 0f;
                s += Mathf.Min(Vector3.Distance(threatPos, cand), 20f) * 0.15f;
                float near = MinDistToPath(path.corners, threatPos, 0);
                if (near < 2.5f) s -= desperate ? 1f : 3f;
                if (!desperate && near < 1.2f) continue;
            }
            s += Mathf.Min(CountNear(cand, 2.5f), 4) * 0.2f;
            if (shelf) s += shelfBonus;
            else if (EnclosedSides(cand + Vector3.up * 0.1f) >= 2) s += 0.5f;
            if (s > bestScore) { bestScore = s; best = cand; found = true; }
        }
        if (found && !PlanPath(best)) return false;
        return found;
    }

    static int CountNear(Vector3 p, float r)
    {
        int n = 0;
        foreach (var q in Prop.All) if ((q.transform.position - p).sqrMagnitude <= r * r) n++;
        return n;
    }

    // ---------- Полки ----------

    static bool OnShelf(Prop q) => q.transform.position.y - q.height * 0.5f > 0.15f;

    // Свободное место под тело на уровне полки (без предметов и чужих тел).
    bool FreeSpot(Vector3 p)
    {
        float r = hider.BodyRadius + 0.03f, h = hider.BodyHeight * 0.5f;
        var cols = Physics.OverlapBox(p + Vector3.up * (h + 0.01f), new Vector3(r, h - 0.005f, r), Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
        foreach (var c in cols)
        {
            if (c.transform.IsChildOf(hider.transform)) continue;
            if (c.GetComponentInParent<Prop>() != null || c.GetComponentInParent<HiderPlayer>() != null) return false;
        }
        return true;
    }

    bool SpotFits(ShelfSlot s) => hider.BodyHeight + 0.02f <= s.clearance;

    bool RandomShelfSpot(Vector3 from, out Vector3 spot)
    {
        spot = default;
        if (ShelfSlot.All.Count == 0) return false;
        for (int k = 0; k < 4; k++)
        {
            var s = ShelfSlot.All[rng.Next(ShelfSlot.All.Count)];
            if (!SpotFits(s)) continue;
            Vector3 p = s.Point((float)rng.NextDouble());
            if (Flat(p - from).magnitude > 14f || !FreeSpot(p)) continue;
            if (!NavMesh.SamplePosition(p, out var nh, 0.15f, Filter) || Mathf.Abs(nh.position.y - s.top) > 0.05f) continue;
            spot = nh.position;
            return true;
        }
        return false;
    }

    // Место рядом с предметом на той же полке (с ним в одном ряду, чтобы потом принять его облик).
    bool ShelfSpotNear(Prop q, out Vector3 spot)
    {
        spot = default;
        Vector3 foot = q.transform.position - Vector3.up * q.height * 0.5f;
        ShelfSlot slot = null;
        foreach (var s in ShelfSlot.All) if (s.Contains(foot)) { slot = s; break; }
        if (slot == null || !SpotFits(slot)) return false;
        float len = Vector3.Distance(slot.a, slot.b);
        float u0 = slot.Project(foot);
        foreach (float d in new[] { 0.35f, -0.35f, 0.55f, -0.55f, 0.8f, -0.8f, 1.1f, -1.1f })
        {
            float u = u0 + d / len;
            if (u < 0f || u > 1f) continue;
            Vector3 p = slot.Point(u);
            if (!FreeSpot(p)) continue;
            if (!NavMesh.SamplePosition(p, out var nh, 0.15f, Filter) || Mathf.Abs(nh.position.y - slot.top) > 0.05f) continue;
            spot = nh.position;
            return true;
        }
        return false;
    }

    // ---------- Затаиться после побега / перемещения ----------

    void DecideSettle()
    {
        input.move = Vector2.zero;
        wantMorphCheck = true;   // сменить облик под окружение, как только кулдаун позволит
        EnterFreeze();
        ScheduleRelocate();
    }

    // Сменить облик на модель, которой вокруг больше всего (если это заметно лучше текущей). Условия смены (кулдаун, дистанция) проверяет игровая логика.
    bool TryStartMorph()
    {
        wantMorphCheck = false;
        var cur = hider.CurrentProp;
        if (cur == null) return false;
        Vector3 pos = hider.transform.position;
        Vector3 eye = hider.EyePosition;
        int curFit = LocalFit(pos, cur.ModelId, cur);
        Prop best = null; int bestFit = curFit + 1;   // нужен выигрыш хотя бы в одну штуку
        float reach = PossessionAuthority.Instance.pickDistance - 0.3f;
        foreach (var q in Prop.All)
        {
            if (q == cur || q.ModelId == cur.ModelId) continue;
            if (q.Colliders.Length == 0 || Vector3.Distance(eye, q.Colliders[0].bounds.ClosestPoint(eye)) > reach) continue;
            int fit = LocalFit(pos, q.ModelId, cur);
            if (fit < bestFit || !ClearSight(eye, q)) continue;
            best = q; bestFit = fit;
        }
        if (best == null) return false;
        morphTarget = best;
        SetState(State.Morphing);
        return true;
    }

    // ---------- Навигация: тип агента по телу ----------

    static bool smallResolved, hasSmall;
    static int smallAgent;
    static bool HasSmallAgent
    {
        get
        {
            if (!smallResolved)
            {
                smallResolved = true;
                for (int i = 0; i < NavMesh.GetSettingsCount(); i++)
                {
                    int id = NavMesh.GetSettingsByIndex(i).agentTypeID;
                    if (NavMesh.GetSettingsNameFromID(id) == "HiderSmall") { smallAgent = id; hasSmall = true; }
                }
            }
            return hasSmall;
        }
    }

    // Tiny/Small помещаются на полки: их навмеш — HiderSmall; остальное тело (стикмен, Medium/Large) — обычный агент 0.
    public bool SmallBody => HasSmallAgent && hider.CurrentProp != null && hider.BodyRadius <= smallMaxRadius && hider.BodyHeight <= smallMaxHeight;
    NavMeshQueryFilter Filter => new NavMeshQueryFilter { agentTypeID = SmallBody ? smallAgent : 0, areaMask = NavMesh.AllAreas };

    // Бот стоит на своём навмеше (пол или уровень полки)?
    bool SampleHere(out Vector3 p)
    {
        Vector3 pos = hider.transform.position;
        if (NavMesh.SamplePosition(pos + Vector3.up * 0.05f, out var nh, 0.45f, Filter) && Mathf.Abs(nh.position.y - pos.y) < 0.2f) { p = nh.position; return true; }
        p = default;
        return false;
    }

    // Ближайшая точка навмеша на уровне пола, поиск расширяется до maxR.
    bool SampleFloor(Vector3 pos, float maxR, out Vector3 floor)
    {
        var f = Filter;
        foreach (float r in new[] { 0.3f, 0.8f, 1.5f, maxR })
            if (r <= maxR && NavMesh.SamplePosition(new Vector3(pos.x, 0.05f, pos.z), out var nh, r, f) && nh.position.y < 0.1f) { floor = nh.position; return true; }
        floor = default;
        return false;
    }

    bool PlanPath(Vector3 target)
    {
        var path = new NavMeshPath();
        Vector3 pos = hider.transform.position;
        bool drop = false;
        if (!SampleHere(out var from))
        {
            // не на своём навмеше (напр. крупный предмет на полке): сперва спрыгнуть к ближайшей точке пола по прямой
            if (!SampleFloor(pos, 4f, out from)) { lastPlanFail = $"нет навмеша у бота {pos}"; return false; }
            drop = pos.y - from.y > 0.3f;
        }
        if (!NavMesh.CalculatePath(from, target, Filter, path) || path.status != NavMeshPathStatus.PathComplete)
        { lastPlanFail = $"путь {from} -> {target}: {path.status}"; return false; }
        corners = path.corners; ci = 0; stuckT = 0f; lastPos = pos;
        stuckFailed = false;
        linkIdx = -1;
        dropping = drop;
        dropTarget = from; dropT = 0f;
        return true;
    }

    // Отрезок k -> k+1 — переход NavMeshLink (Jump): заметная разница высот между соседними углами пути.
    bool IsLink(int k) => k + 1 < corners.Length && Mathf.Abs(corners[k + 1].y - corners[k].y) > 0.12f;

    // Идём по углам пути; переходы Jump исполняются манёвром прыжка тем же вводом, что у игрока.
    void Follow(bool flee)
    {
        if (ci >= corners.Length) { input.move = Vector2.zero; return; }
        Vector3 pos = hider.transform.position;
        if (dropping)
        {
            Vector3 dd = dropTarget - pos; dd.y = 0f;
            dropT += Time.deltaTime;
            if (pos.y - dropTarget.y < 0.3f) dropping = false;
            else if (dropT > 4f) { stuckFailed = true; ci = corners.Length; input.move = Vector2.zero; return; }
            else
            {
                // точка почти под ботом (стоит на чём-то над ней, напр. на голове охотника) — не крутиться на месте, а сойти вперёд
                if (dd.sqrMagnitude > 0.3f * 0.3f) input.yaw = Mathf.Atan2(dd.x, dd.z) * Mathf.Rad2Deg;
                input.move = Vector2.up;
                return;
            }
        }
        if (linkIdx >= 0) { TraverseLink(); return; }

        // Пройденные углы; с начала перехода начинается манёвр.
        while (ci < corners.Length - 1 && FlatDist(corners[ci], pos) < arriveDist)
        {
            if (IsLink(ci)) { BeginLink(ci); TraverseLink(); return; }
            ci++;
        }
        bool last = ci == corners.Length - 1;
        if (last && FlatDist(corners[ci], pos) < (flee ? arriveDist : finalArriveDist)) { ci = corners.Length; input.move = Vector2.zero; return; }

        // Куда смотреть: при побеге — точка впереди по пути + плавное смещение вбок; иначе (точная стоянка) — прямо на угол.
        Vector3 target = flee ? PursuitTarget(pos) : corners[ci];
        Vector3 to = target - pos; to.y = 0f;
        if (flee)
        {
            // цель ближе 25 см — курс не трогаем (иначе пеленг на точку под собой скачет); поворот ограничен по скорости
            if (to.sqrMagnitude > 0.25f * 0.25f)
                input.yaw = Mathf.MoveTowardsAngle(input.yaw, Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg, fleeTurnRate * Time.deltaTime);
        }
        else if (to.sqrMagnitude > 1e-6f) input.yaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
        input.move = Vector2.up;

        // Застряли (упёрлись): повторное планирование один раз, затем сдаёмся
        stuckT += Time.deltaTime;
        if (stuckT >= 1f)
        {
            if ((pos - lastPos).sqrMagnitude < 0.04f * 0.04f)
            {
                stuckStrikes++;
                if (stuckStrikes >= 2 || !PlanPath(dest)) { stuckFailed = true; ci = corners.Length; }
            }
            else stuckStrikes = 0;
            stuckT = 0f; lastPos = pos;
        }
    }

    // Точка впереди по ломаной пути (pure pursuit), не дальше начала перехода/конца пути; tan — направление пути там, remain — сколько до остановки.
    Vector3 PursuitPoint(Vector3 pos, float look, out Vector3 tan, out float remain)
    {
        Vector3 a = ci > 0 ? corners[ci - 1] : pos, b = corners[ci];
        Vector3 ab = Flat(b - a);
        float t = ab.sqrMagnitude < 1e-4f ? 1f : Mathf.Clamp01(Vector3.Dot(Flat(pos - a), ab) / ab.sqrMagnitude);
        Vector3 q = a + (b - a) * t;
        tan = ab.sqrMagnitude > 1e-4f ? ab.normalized : Flat(b - pos).normalized;
        Vector3 res = default; bool got = false;
        float acc = 0f;
        for (int k = ci; k < corners.Length; k++)
        {
            Vector3 seg = Flat(corners[k] - q);
            float L = seg.magnitude;
            if (L > 1e-4f && !got && acc + L >= look) { res = q + seg / L * (look - acc); tan = seg / L; got = true; }
            else if (L > 1e-4f && !got) tan = seg / L;
            acc += L;
            q = corners[k];
            if (IsLink(k)) break;
        }
        remain = acc;
        return got ? res : q;
    }

    // Синусоида вбок от точки следования. Фаза идёт непрерывно (и в воздухе), амплитуда и центр плавно подстраиваются под
    // свободное место по обе стороны (в проходе 1,6 м бот бежит посередине с меньшим размахом, не трётся о стеллаж).
    Vector3 PursuitTarget(Vector3 pos)
    {
        float dt = Time.deltaTime;
        Vector3 p = PursuitPoint(pos, pursuitLookahead, out var tan, out float remain);
        Vector3 n = Vector3.Cross(Vector3.up, tan);
        float m = hider.BodyRadius + weaveMargin;
        float wantAmp = 0f, wantCenter = 0f;
        // только на полу: на полке 0,275 м глубиной виляние = шаг с кромки (Free не видит обрыв)
        bool floorLevel = corners[ci].y < 0.1f && (ci == 0 || corners[ci - 1].y < 0.1f) && pos.y < 0.15f + (hider.Grounded ? 0f : 3f);
        if (remain > 1.2f && n.sqrMagnitude > 0.5f && floorLevel)
        {
            // свободное место — минимум у бота (его точка на пути) и у точки следования: на выходе из прохода точка впереди
            // уже за торцом стеллажа на открытом месте, а тело ещё рядом со стеллажом — размах раздувался и прыжок сажал на стеллаж
            float span = weaveAmplitude + m + 0.4f;
            Vector3 q = PursuitPoint(pos, 0f, out _, out _);
            float fr = Mathf.Min(Free(p, n, span), Free(q, n, span)), fl = Mathf.Min(Free(p, -n, span), Free(q, -n, span));
            wantCenter = Mathf.Clamp((fr - fl) * 0.5f, -0.6f, 0.6f);
            wantAmp = Mathf.Clamp((fr + fl) * 0.5f - m, 0f, weaveAmplitude);
            // крайние положения center ± amp не дальше свободного места с каждой стороны (центр ограничен ±0,6, поэтому проверяем явно)
            wantAmp = Mathf.Max(0f, Mathf.Min(wantAmp, Mathf.Min(fr - m - wantCenter, fl - m + wantCenter)));
        }
        // в воздухе размах и смещение центра только уменьшаются (дуга прыжка не уводит тело к стеллажу)
        if (!hider.Grounded)
        {
            wantAmp = Mathf.Min(wantAmp, weaveAmp);
            if (Mathf.Abs(wantCenter) > Mathf.Abs(weaveCenter)) wantCenter = weaveCenter;
        }
        // сужаться быстро (впереди стена), расширяться медленно (плавная волна)
        float kUp = 1f - Mathf.Exp(-4f * dt), kDown = 1f - Mathf.Exp(-12f * dt);
        weaveAmp = Mathf.Lerp(weaveAmp, wantAmp, wantAmp < weaveAmp ? kDown : kUp);
        weaveCenter = Mathf.Lerp(weaveCenter, wantCenter, Mathf.Abs(wantCenter) < Mathf.Abs(weaveCenter) || wantCenter * weaveCenter < 0f ? kDown : kUp);
        weavePhase += dt * Mathf.PI * 2f / Mathf.Max(0.3f, weavePeriod);
        // у остановки (конец пути, начало перехода) смещение сходит на нет сразу, а не через сглаживание — иначе цель
        // оказывалась в сантиметрах сбоку от бота и курс метался на 180°
        float fade = Mathf.Clamp01((remain - 0.4f) / 1.2f);
        WeaveOffset = (weaveCenter + weaveAmp * Mathf.Sin(weavePhase)) * fade;
        Vector3 target = p + n * WeaveOffset;
        // цель вне прямой досягаемости по навмешу (срезали угол стеллажа): сперва без смещения, потом угол пути
        Vector3 from = new Vector3(pos.x, corners[Mathf.Max(0, ci - 1)].y, pos.z);
        if (NavMesh.Raycast(from, new Vector3(target.x, p.y, target.z), out _, Filter))
            target = NavMesh.Raycast(from, p, out _, Filter) ? corners[ci] : p;
        return target;
    }

    public float fleeTurnRate = 540f;   // °/с: предел поворота курса при побеге (синусоиде нужно ~90°/с), гасит рывки команды

    // Свободное расстояние по горизонтали от точки пути в сторону dir (низ и середина тела).
    float Free(Vector3 p, Vector3 dir, float max)
    {
        float y0 = hider.transform.position.y;
        float best = max;
        foreach (float hy in new[] { 0.1f, Mathf.Max(0.2f, hider.BodyHeight * 0.6f) })
            if (Physics.Raycast(new Vector3(p.x, y0 + hy, p.z), dir, out var hit, max, RamKickAuthority.StaticMask, QueryTriggerInteraction.Ignore)) best = Mathf.Min(best, hit.distance);
        return best;
    }

    // Прыжки при побеге: только с пола, когда впереди и по бокам свободно и рядом нет перехода на полку
    // (иначе прыжок закидывает на полку). Виляние в воздухе продолжается тем же вводом.
    void EvadeJump()
    {
        if (dropping || linkIdx >= 0 || ci >= corners.Length) return;
        // уклонение имеет смысл, только когда охотник видит бота и может стрелять; без него прыжок лишь привлекает внимание
        if (Time.time < nextJumpAt || !hider.Grounded || !ThreatVisible) return;
        Vector3 pos = hider.transform.position;
        if (pos.y > 0.1f) return;
        Vector3 pp = PursuitPoint(pos, pursuitLookahead, out _, out float remain);
        if (remain < 2.5f) return;
        // только на прямом участке: до поворота пути не меньше 2 м и курс уже вдоль пути. Иначе курс продолжал
        // поворачивать в воздухе к следующему углу и дуга прыжка уходила через стеллаж (бот садился на его верх).
        float flight = FlightLength();
        if (remain < flight + 0.5f) return;
        if (FlatDist(corners[ci], pos) < flight && ci < corners.Length - 1) return;
        Vector3 toP = Flat(pp - pos);
        if (toP.sqrMagnitude > 1e-4f && Mathf.Abs(Mathf.DeltaAngle(input.yaw, Mathf.Atan2(toP.x, toP.z) * Mathf.Rad2Deg)) > 20f) return;
        JumpChecks++;
        if (!JumpCorridorClear(input.yaw, flight)) return;
        EvadeJumps++;
        input.PressJump();
        nextJumpAt = Time.time + 0.9f + (float)rng.NextDouble() * 1.1f;
    }

    // Длина полёта = скорость × время в воздухе (2·√(2H/g) ≈ 0,9 с): ~3,6 м шагом и ~5,4 м с Z.
    float FlightLength() => Mathf.Max(3f, hider.HorizontalVelocity.magnitude * 2f * Mathf.Sqrt(2f * hider.JumpHeight / -hider.gravity)) + hider.BodyRadius;

    // Объём коридора тела по всей дуге прыжка (вперёд на длину полёта, по высоте до верха прыжка) без статики. Лучи на паре высот
    // проходили между полками толщиной 4 см и бот прыгал вплотную к стеллажу. Боковой размах виляния сюда не входит:
    // точка следования и так держит тело в r + weaveMargin от стеллажей (PursuitTarget), и в воздухе тоже.
    bool JumpCorridorClear(float yaw, float flight)
    {
        Vector3 pos = hider.transform.position;
        Quaternion yawQ = Quaternion.Euler(0f, yaw, 0f);
        float half = hider.BodyRadius + 0.08f, top = hider.JumpHeight + hider.BodyHeight;
        Vector3 center = pos + yawQ * new Vector3(0f, 0f, flight * 0.5f) + Vector3.up * (0.05f + top * 0.5f);
        int n = Physics.OverlapBoxNonAlloc(center, new Vector3(half, top * 0.5f, flight * 0.5f), overlapBuf, yawQ, RamKickAuthority.StaticMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
            if (!overlapBuf[i].transform.IsChildOf(hider.transform)) { JumpBlockedBy = overlapBuf[i].name; return false; }
        return true;
    }

    // ---------- Переход Jump (NavMeshLink пол <-> уровень полки) ----------

    enum LinkPhase { Approach, Air }
    int linkIdx = -1, linkTries;
    LinkPhase linkPhase;
    float linkT, linkClear, linkPrevY, faceT;
    Vector3 linkNormal;
    bool linkPush;
    public string LastLinkLog = "";

    void BeginLink(int k)
    {
        linkIdx = k; linkPhase = LinkPhase.Approach; linkT = 0f; faceT = 0f; linkPush = false; linkTries = 0;
        Vector3 e = corners[k + 1];
        linkClear = Physics.Raycast(e + Vector3.up * 0.03f, Vector3.up, out var h, 3f, RamKickAuthority.StaticMask, QueryTriggerInteraction.Ignore) ? h.distance + 0.03f : 3f;
        linkPrevY = hider.transform.position.y;
        // нормаль полки: вверх — к стеллажу (против нормали уровня), вниз — от него
        linkNormal = Vector3.zero;
        bool up = e.y > corners[k].y;
        Vector3 shelfEnd = up ? e : corners[k];
        foreach (var sl in ShelfSlot.All)
            if (sl.Contains(shelfEnd, 0.08f)) { linkNormal = up ? -sl.normal : sl.normal; break; }
    }

    // Вверх: подойти вплотную к кромке, прыгнуть без хода вперёд и нажать «вперёд» в тот кадр, когда низ тела окажется между
    // нужной полкой и полкой над ней (на подъёме или на спуске). Дальше полка сверху гасит подъём (HiderPlayer), тело ложится на уровень.
    // Вниз: просто шагнуть с полки к точке на полу.
    void TraverseLink()
    {
        Vector3 s = corners[linkIdx], e = corners[linkIdx + 1], pos = hider.transform.position;
        // Перпендикулярно кромке полки (нормаль ShelfSlot), а не по вектору s->e: у широкой связи концы пути
        // берутся независимо и могут разъехаться по длине полки — тогда бот запрыгивал под углом и перелетал верхнюю полку.
        Vector3 dir = linkNormal.sqrMagnitude > 0.5f ? linkNormal : Flat(e - s);
        if (dir.sqrMagnitude < 1e-4f) dir = Flat(e - pos);
        dir.Normalize();
        float along = Vector3.Dot(Flat(e - pos), dir);   // сколько ещё до точки уровня вдоль нормали
        input.yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        float dt = Mathf.Max(Time.deltaTime, 1e-4f);
        float vy = (pos.y - linkPrevY) / dt;
        linkPrevY = pos.y;
        linkT += Time.deltaTime;
        if (e.y < s.y)
        {
            // шаг с полки наружу; если за секунду не спустились (упёрлись) — прямо к точке на полу
            if (linkT > 1f && Mathf.Abs(pos.y - s.y) < 0.1f) { Vector3 w = Flat(e - pos); if (w.sqrMagnitude > 1e-4f) input.yaw = Mathf.Atan2(w.x, w.z) * Mathf.Rad2Deg; }
            input.move = Vector2.up;
            if (hider.Grounded && pos.y < e.y + 0.1f) EndLink(true, "вниз");
            else if (hider.Grounded && linkT > 0.3f && pos.y < s.y - 0.1f) EndLink(false, $"вниз: приземлился на {pos.y:F2}");   // не на пол (другая полка, чужое тело) — перепланировать отсюда
            else if (linkT > 3f) EndLink(false, "вниз: таймаут");
            return;
        }
        float lo = e.y + 0.01f, hi = e.y + linkClear - hider.BodyHeight - 0.015f;
        switch (linkPhase)
        {
            case LinkPhase.Approach:
                // Сначала встать точно напротив точки перехода по длине полки: начав манёвр в 0,3 м от неё у торца стеллажа,
                // бот прыгал вдоль торцевой стенки, упирался в неё и садился на её верх (y=2,0).
                Vector3 toS = Flat(s - pos);
                float lateral = Vector3.Dot(toS, Vector3.Cross(Vector3.up, dir));
                if ((Mathf.Abs(lateral) > 0.06f || pos.y > s.y + 0.15f) && linkT < 2f)
                {
                    if (toS.sqrMagnitude > 0.0025f) input.yaw = Mathf.Atan2(toS.x, toS.z) * Mathf.Rad2Deg;
                    input.move = toS.sqrMagnitude > 0.0025f ? Vector2.up : Vector2.zero;
                    faceT = 0f;
                    break;
                }
                faceT += Time.deltaTime;
                bool touching = Physics.Raycast(pos + Vector3.up * 0.05f, dir, hider.BodyRadius + 0.06f, RamKickAuthority.StaticMask, QueryTriggerInteraction.Ignore);
                if ((touching || faceT > 1.2f) && hider.Grounded)
                {
                    input.move = Vector2.zero;
                    input.PressJump();
                    linkPhase = LinkPhase.Air; linkT = 0f; linkPush = false;
                }
                else input.move = Vector2.up;
                break;
            case LinkPhase.Air:
                float yNext = pos.y + vy * dt;
                if (!linkPush && linkT > 0.12f && yNext >= lo && yNext <= hi) linkPush = true;
                input.move = linkPush && along > 0.02f ? Vector2.up : Vector2.zero;   // над серединой уровня — дальше не лететь, опуститься
                if (linkT > 0.25f && hider.Grounded)
                {
                    if (linkPush && Mathf.Abs(pos.y - e.y) < 0.08f) { EndLink(true, $"вверх на {e.y:F2}, попыток {linkTries + 1}"); return; }
                    if (pos.y < e.y - 0.1f && pos.y < 0.1f)
                    {
                        if (++linkTries >= 3) EndLink(false, $"вверх на {e.y:F2}: 3 промаха");
                        else { linkPhase = LinkPhase.Approach; linkT = 0f; faceT = 0f; }
                        return;
                    }
                    if (Mathf.Abs(pos.y - e.y) >= 0.08f) EndLink(false, $"вверх на {e.y:F2}: приземлился на {pos.y:F2}");
                }
                else if (linkT > 3f) EndLink(false, $"вверх на {e.y:F2}: таймаут");
                break;
        }
    }

    void EndLink(bool ok, string what)
    {
        LastLinkLog = $"t={Time.time:F2} {(ok ? "OK" : "FAIL")} {what}";
        if (ok) { ShelfJumpsOk++; ci = linkIdx + 1; }
        else { ShelfJumpsFail++; stuckFailed = true; ci = corners.Length; }
        linkIdx = -1;
        input.move = Vector2.zero;
    }

    void AimAtProp(Prop p)
    {
        Vector3 d = PropCenter(p) - hider.EyePosition;
        input.yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        input.pitch = -Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;
    }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
    static float FlatDist(Vector3 a, Vector3 b) { a.y = 0f; b.y = 0f; return Vector3.Distance(a, b); }

    static float PathLength(Vector3[] c)
    {
        float l = 0f;
        for (int i = 1; i < c.Length; i++) l += Vector3.Distance(c[i - 1], c[i]);
        return l;
    }

    static float MinDistToPath(Vector3[] c, Vector3 p, int from)
    {
        float best = float.MaxValue;
        for (int i = Mathf.Max(1, from); i < c.Length; i++)
        {
            Vector3 a = c[i - 1], b = c[i], ab = b - a;
            float t = ab.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude);
            Vector3 q = a + ab * t;
            best = Mathf.Min(best, Vector2.Distance(new Vector2(q.x, q.z), new Vector2(p.x, p.z)));
        }
        return best;
    }

    // Отладка/тест: немедленно запустить естественное перемещение.
    public bool DebugRelocateNow()
    {
        if (Current != State.Freeze) return false;
        return StartRelocate();
    }

    // Отладка/тест: побег в заданную точку (как после обнаружения, без задержки реакции и выбора укрытия).
    public bool DebugFleeTo(Vector3 p)
    {
        if (hider.CurrentProp == null || !PlanPath(p)) return false;
        dest = p;
        boostIssued = true;   // без Z: проверяем виляние и прыжки на обычной скорости
        FleeEndReason = "";
        debugFixedDest = false;
        FleeStartedAt = Time.time;
        haveThreat = ThreatVisible; threatPos = LastSeenPos;
        nextJumpAt = Time.time + 0.3f;
        weaveAmp = weaveAmplitude * 0.5f;
        debugFixedDest = true;
        SetState(State.Flee);
        return true;
    }

    // Отладка/тест: идти в точку (полка или пол) обычным перемещением, как при Relocate.
    public bool DebugGoTo(Vector3 p)
    {
        if (!PlanPath(p)) return false;
        dest = p;
        wantMorphCheck = false;
        relocateRetries = 99;   // тест сам выбирает точку: при неудаче не подменять её другой
        SetState(State.Relocate);
        return true;
    }

    // ---------- Диагностика (включается тестом: Diag = new StringBuilder()) ----------
    public System.Text.StringBuilder Diag;

    void DiagFrame()
    {
        var h = HunterPlayer.All.Count > 0 ? HunterPlayer.All[0] : null;
        if (h == null) return;
        var p = hider.transform.position; var q = h.transform.position;
        Diag.AppendLine($"{Time.time:F3} {Current} видит={ThreatVisible} дист {ThreatDist:F2} спокойно {CalmFor:F2} | бот ({p.x:F3};{p.y:F3};{p.z:F3}) охотн ({q.x:F2};{q.y:F2};{q.z:F2}) | вбок {WeaveOffset:F3} ампл {weaveAmp:F2} | ввод ({input.move.x:F2};{input.move.y:F2}) yaw {input.yaw:F0} | земля={hider.Grounded} переход={linkIdx >= 0} {(linkIdx >= 0 ? linkPhase + " push=" + linkPush + " clr=" + linkClear.ToString("F2") : "")}");
    }
}
