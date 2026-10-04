using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// Ввод бота: тот же набор «клавиш», что у игрока (движение, прыжок, Z, E) плюс направление взгляда. HiderPlayer читает его вместо клавиатуры.
public class BotInput
{
    public Vector2 move;          // x вправо, y вперёд относительно yaw (как WASD)
    public float rotate;          // Q/E: -1 влево, +1 вправо (поворот предмета-тела)
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
    public float morphRamChance = 0.3f;   // шанс при побеге резко сменить облик на средний/крупный предмет и сразу таранить (Z)
    public float coverWeight = 2.5f;      // выбор предмета: насколько важна укрытость места (осторожный выше, наглый ниже)
    public float attentionAngle = 12f;    // near-miss: бежать, только если выстрел был направлен на бота в пределах этого угла (°)

    public static BotPersonality Cautious() => new BotPersonality { name = "Cautious", reactionDelay = 0.25f, safeDistance = 10f, ramChance = 0.15f, relocateEvery = 45f, noticeRange = 16f, morphRamChance = 0.3f, coverWeight = 2.5f, attentionAngle = 12f };
    public static BotPersonality Bold() => new BotPersonality { name = "Bold", reactionDelay = 0.6f, safeDistance = 7f, ramChance = 0.7f, relocateEvery = 20f, noticeRange = 8f, morphRamChance = 0.75f, coverWeight = 1.0f, attentionAngle = 7f };
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
    public float weaveAmplitude = 0.9f;   // м, максимум, когда охотник видит бота; не видит — вдвое меньше; в проходе урезается под свободное место
    public float weavePeriod = 1.6f;      // с на полный зигзаг в среднем (полуволны случайные, см. zigHalfMin/Max)
    public float zigHalfMin = 0.45f, zigHalfMax = 0.85f;   // с: длительность одной полуволны (вправо или влево) — неровный, непредсказуемый зигзаг
    public float morphRamRange = 7f;      // м: охотник ближе и виден — можно сменить облик на крупный и таранить
    public float weaveMargin = 0.2f;      // запас до стены сверх радиуса тела
    public float pursuitLookahead = 1.0f; // м: точка следования впереди по пути (больше — сильнее гасит виляние)

    // Выбор предмета для вселения: взвешенный жребий, а не «лучший + шум» (см. DrawCandidate).
    public float pickTemperature = 0.8f;  // чем выше, тем чаще выпадают средние места; ниже — почти всегда лучшие
    public float spreadWeight = 3f, spreadRadius = 3f;     // штраф за близость к предметам других ботов
    public float memoryWeight = 2f, memoryRadius = 2f;     // штраф за места, где этот бот сидел в прошлых раундах
    public float distWeight = 0.03f;      // за метр от бота до предмета
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
    float tickTimer, stateSince, nextRelocateAt, reactAt, ramUntil, lookUntil, lookBaseYaw, nextJumpAt, stuckT;
    float weaveAmp, weaveCenter, lastFleeEndAt = -100f, nextRethinkAt, cornerUntil;
    int relocateRetries;
    float zigT, zigDur = 0.6f, zigAmp = 1f, zigSign = 1f, nextRamRollAt;
    bool morphThenRam;
    public int MorphRams, ZigJumps;
    string morphRamWhy = "";
    public bool RecordWhy;   // тест: включить запись причин (в игре строки не собираются)
    public string MorphRamWhy { get => morphRamWhy; set { if (RecordWhy && value != lastWhy) { lastWhy = value; morphRamWhy = (morphRamWhy.Length > 0 ? morphRamWhy + " / " : "") + $"{Time.time:F1}: {value}"; if (morphRamWhy.Length > 400) morphRamWhy = morphRamWhy.Substring(morphRamWhy.Length - 400); } } }   // тест: история причин
    string lastWhy = "";
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
        // память: где бот сидел в прошлых раундах (последние 3)
        if (lastPickSet) { pastPicks.Add(lastPick); if (pastPicks.Count > 3) pastPicks.RemoveAt(0); lastPickSet = false; }
    }

    readonly List<Vector3> pastPicks = new List<Vector3>();
    Vector3 lastPick; bool lastPickSet;

    public static void NewRound() { RoundSalt = unchecked(RoundSalt * 1103515245 + 12345 + System.Environment.TickCount); foreach (var b in All) b.ReseedRound(); }

    void OnDestroy() { if (hider != null) hider.Detected -= OnDetected; }

    // ---------- Событие обнаружения ----------

    void OnDetected(HiderPlayer.DetectKind kind)
    {
        if (hider.Caught || hider.CurrentProp == null) return;
        Perceive();   // свежая картина: где охотник, видит ли
        // Near-miss «не по мне»: охотник бота не видит (видимость симметрична — значит и не целился в него) или выстрел
        // направлен заметно в сторону (стрелял по соседу). Бот не выдаёт себя бегом, но становится нервнее на 20 с.
        if (kind == HiderPlayer.DetectKind.NearMiss && Current != State.Flee && Current != State.Ram && Current != State.Reacting && !ShotWasAtMe())
        {
            IgnoredNearMisses++;
            nervousUntil = Time.time + 20f;
            return;
        }
        DetectedAt = Time.time;
        LastDetect = kind;
        if (ThreatVisible) { threatPos = LastSeenPos; haveThreat = true; }
        if (Current == State.Reacting || Current == State.Flee || Current == State.Ram) return;
        // небольшой джиттер реакции (±20%), чтобы не ощущалась метрономом
        reactAt = Time.time + personality.reactionDelay * (0.8f + (float)rng.NextDouble() * 0.4f);
        input.move = Vector2.zero;
        SetState(State.Reacting);
    }

    public int IgnoredNearMisses;
    float nervousUntil = -100f;

    // Выстрел был в бота: охотник его видит и направление выстрела в пределах угла внимания от направления на бота.
    // Вблизи допуск шире (разброс и неточность прицела): не меньше atan(0,6 м / дистанция). После испуга — нервнее (×1,5).
    bool ShotWasAtMe()
    {
        if (!ThreatVisible) return false;
        Vector3 dir = hider.LastShotDir;
        if (dir.sqrMagnitude < 0.5f) return true;   // направление неизвестно — по-старому
        Vector3 to = hider.BodyCenter - hider.LastShotOrigin;
        float d = to.magnitude;
        float ang = Vector3.Angle(dir, to);
        float allow = Mathf.Max(personality.attentionAngle, Mathf.Atan2(0.6f, Mathf.Max(0.5f, d)) * Mathf.Rad2Deg);
        if (Time.time < nervousUntil) allow *= 1.5f;
        LastShotAngle = ang; LastShotAllow = allow;
        return ang <= allow;
    }
    public float LastShotAngle, LastShotAllow;   // тест

    // ---------- Основной цикл ----------

    void LateUpdate()
    {
        if (History.Count > 80) History.RemoveRange(60, History.Count - 80);
    }

    bool Active => Current == State.Flee || Current == State.Look || Current == State.Reacting || Current == State.Ram || Current == State.Relocate || Current == State.Morphing;

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
        if (Diag != null && (Current != State.Freeze || TickCount % 30 == 0)) DiagFrame();
    }

    void SetState(State s)
    {
        if (s != Current)
        {
            var p = hider != null ? hider.transform.position : Vector3.zero;
            Trail.Add($"{Time.time:F1} {Current}->{s} ({p.x:F1};{p.y:F2};{p.z:F1}) цель ({dest.x:F1};{dest.y:F2};{dest.z:F1})");
            if (Trail.Count > 40) Trail.RemoveAt(0);
        }
        Current = s; stateSince = Time.time;
    }

    public readonly List<string> Trail = new List<string>();   // для тестов: последние переходы состояний

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
                if (morphThenRam)
                {
                    // сменили облик на крупный — сразу Z и на охотника; не вышло за 0,8 с — бежать дальше
                    bool done = hider.CurrentProp != null && morphTarget != null && hider.CurrentProp.ModelId == morphTarget.ModelId;
                    if (done && hider.Boost != HiderPlayer.BoostPhase.Recharging) { morphThenRam = false; if (ThreatVisible) threatPos = LastSeenPos; BeginRam(); }
                    else if (done || Time.time - stateSince > 0.8f) { morphThenRam = false; BeginRun(); }
                    break;
                }
                if (hider.CurrentProp != null && morphTarget != null && hider.CurrentProp.ModelId == morphTarget.ModelId) { EnterFreeze(); PlanAlign(); }
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
        input.rotate = Current == State.Freeze ? AlignStep() : 0f;
    }

    // ---------- Поворот предмета-тела (Q/E) под соседей ----------
    // После перемещения/побега/смены облика бот стоит под случайным углом; такие же предметы рядом обычно стоят иначе.
    // Цель: угол ближайшего такого же предмета (с учётом симметрии формы), если повёрнутое тело ничего не задевает.
    // Крутится только когда охотник не смотрит вблизи (поворот на глазах выдаёт). Тот же ввод Q/E и скорость, что у игрока.
    bool alignPending;
    float alignYaw;
    public int Aligns;
    public float AlignError { get; private set; }   // тест: оставшаяся разница с соседом, °

    void PlanAlign()
    {
        alignPending = false;
        var cur = hider.CurrentProp;
        if (cur == null || cur.Colliders.Length == 0) return;
        float step = SymmetryStep(cur);
        if (step <= 0f) return;   // круглый: поворот ничего не меняет
        Vector3 pos = cur.transform.position;
        Prop near = null; float bestD = 2.5f;
        foreach (var q in Prop.All)
        {
            if (q == cur || q.ModelId != cur.ModelId) continue;
            if (Mathf.Abs((q.transform.position.y - q.height * 0.5f) - hider.transform.position.y) > 0.3f) continue;   // тот же уровень
            float d = Vector3.Distance(q.transform.position, pos);
            if (d < bestD) { bestD = d; near = q; }
        }
        float now = cur.transform.eulerAngles.y;
        float want = near != null ? near.transform.eulerAngles.y : now;
        // ближайший к текущему из равноценных по симметрии углов, который помещается
        float bestDelta = float.MaxValue; bool found = false;
        if (near != null)
            for (float k = -360f; k <= 360f; k += step)
            {
                float delta = Mathf.DeltaAngle(now, want + k);
                if (Mathf.Abs(delta) >= Mathf.Abs(bestDelta) || Mathf.Abs(delta) > 180f) continue;
                if (!RotatedFits(cur, now + delta)) continue;
                bestDelta = delta; found = true;
            }
        // угол соседа не помещается (сосед сам стоит криво) или соседа нет, а сейчас тело задевает полку/соседей:
        // ближайший к желаемому угол, который помещается (шаг 5°) — без застревания в полке
        if (!found && (near != null || !RotatedFits(cur, now)))
            for (float off = 5f; off <= 180f && !found; off += 5f)
                foreach (float sgn in new[] { 1f, -1f })
                {
                    float delta = Mathf.DeltaAngle(now, want + sgn * off);
                    if (!RotatedFits(cur, now + delta)) continue;
                    bestDelta = delta; found = true; break;
                }
        if (!found || Mathf.Abs(bestDelta) < 2f) { AlignError = found ? Mathf.Abs(bestDelta) : -1f; return; }
        alignYaw = now + bestDelta;
        alignPending = true;
        Aligns++;
    }

    float AlignStep()
    {
        if (!alignPending || hider.CurrentProp == null) return 0f;
        if (HunterLooksAtMe()) return 0f;   // крутиться, когда охотник смотрит в нашу сторону вблизи, — выдать себя
        float delta = Mathf.DeltaAngle(hider.CurrentProp.transform.eulerAngles.y, alignYaw);
        AlignError = Mathf.Abs(delta);
        if (Mathf.Abs(delta) < 1.5f) { alignPending = false; return 0f; }
        // не перекрутить за кадр: ход за кадр = propTurnSpeed·dt
        float frame = hider.propTurnSpeed * Time.deltaTime;
        return Mathf.Clamp(delta / Mathf.Max(0.01f, frame), -1f, 1f);
    }

    // Охотник близко на виду и смотрит в сторону бота (в пределах 50° от направления взгляда) — его взгляд видно по модели.
    bool HunterLooksAtMe()
    {
        if (!ThreatVisible || SeenDist > personality.noticeRange) return false;
        foreach (var h in HunterPlayer.All)
        {
            Vector3 to = Flat(hider.transform.position - h.transform.position);
            if (to.sqrMagnitude < 1e-4f) return true;
            if (Vector3.Angle(Flat(h.transform.forward), to) < 50f) return true;
        }
        return false;
    }

    // Шаг симметрии формы по yaw: коробка с квадратным основанием 90°, вытянутая 180°, круглая — не важно (0).
    static float SymmetryStep(Prop p)
    {
        var c = p.Colliders[0];
        if (c is CapsuleCollider || c is SphereCollider) return 0f;
        if (c is BoxCollider b)
        {
            Vector3 s = Vector3.Scale(b.size, p.transform.lossyScale);
            float sx = Mathf.Abs(s.x), sz = Mathf.Abs(s.z);
            return Mathf.Abs(sx - sz) <= 0.1f * Mathf.Max(sx, sz) ? 90f : 180f;
        }
        return 360f;
    }

    // Тело под углом yaw ничего не задевает (статика, чужие предметы и тела).
    bool RotatedFits(Prop p, float yaw)
    {
        if (!(p.Colliders[0] is BoxCollider b)) return true;
        // с зазором 5 мм по горизонтали (по высоте чуть ужато, чтобы не задевать полку под собой): угол берётся только с реальным запасом
        Vector3 half = Vector3.Scale(b.size, p.transform.lossyScale) * 0.5f + new Vector3(0.005f, -0.01f, 0.005f);
        Vector3 center = p.transform.position + Vector3.up * 0.005f;
        int n = Physics.OverlapBoxNonAlloc(center, new Vector3(Mathf.Abs(half.x), Mathf.Abs(half.y), Mathf.Abs(half.z)), overlapBuf, Quaternion.Euler(0f, yaw, 0f), ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var t = overlapBuf[i].transform;
            if (t.IsChildOf(hider.transform)) continue;
            if (overlapBuf[i].GetComponentInParent<HunterPlayer>() != null) continue;
            return false;
        }
        return true;
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
        // Новый выбор: жребий по весам (см. DrawCandidate); до 6 попыток, не подошедший (нет места с видом / пути) отвергается.
        int nStand = 0, nPath = 0;
        var cands = ScoreCandidates();
        for (int i = 0; i < 6 && cands.Count > 0; i++)
        {
            int k = DrawIndex(cands);
            var (p, sc) = cands[k];
            cands.RemoveAt(k);
            if (!FindStandpoint(p, out var stand)) { rejected.Add(p); nStand++; History.Add($"t={Time.time:F1}: {p.ModelId} y={p.transform.position.y:F2} — нет места с видом"); continue; }
            if (!PlanPath(stand)) { rejected.Add(p); nPath++; History.Add($"t={Time.time:F1}: {p.ModelId} y={p.transform.position.y:F2} — нет пути ({lastPlanFail})"); continue; }
            seekTarget = p; dest = stand;
            lastPick = p.transform.position; lastPickSet = true;
            History.Add(DebugSeek = $"t={Time.time:F1}: выбран {p.ModelId} y={p.transform.position.y:F2} (оценка {sc:F2}, укрытость {Cover(p):F2}), отвергнуто: без вида {nStand}, без пути {nPath}, кандидатов {cands.Count + 1}");
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

    // ---------- Выбор предмета для вселения (не зависит от карты: только Prop, NavMesh и геометрия) ----------
    // Оценка места = естественность (такие же рядом, не посреди прохода) + укрытость × вес личности − расстояние
    //   − близость к предметам других ботов (разброс по карте) − близость к своим местам прошлых раундов (память).
    // Недостижимые за остаток подготовки отбрасываются. Выбор — жребий с весом exp((оценка − лучшая) / температура):
    // хорошие места выпадают чаще, средние тоже бывают, «всегда одно и то же» не выучить.
    public List<(Prop p, float score)> ScoreCandidates() => ScoreCandidates(hider.transform.position);

    public static bool SimPicks;   // тест: «раунды» без похода к предмету — чужой выбор берётся из seekTarget
    public void DebugClearPick() { seekTarget = null; }

    public List<(Prop p, float score)> ScoreCandidates(Vector3 from)
    {
        EnsureRoundCache();
        var cands = new List<(Prop p, float score)>();
        float reach = float.MaxValue;
        if (RoundState.Instance != null && RoundState.Instance.Phase == RoundPhase.Prep)
            reach = hider.walkSpeed * Mathf.Max(3f, RoundState.Instance.PrepRemaining - 2f) / 1.3f;   // путь ~1,3 прямой, 2 с на вселение
        foreach (var p in Prop.All)
        {
            if (!p.IsFree || rejected.Contains(p)) continue;
            float d = Vector3.Distance(p.transform.position, from);
            if (d > reach) continue;
            bool taken = false;
            float sc = natCache[p] + personality.coverWeight * coverCache[p] - distWeight * d;
            foreach (var o in All)
            {
                if (o == this) continue;
                Prop op = SimPicks ? o.seekTarget
                        : o.seekTarget != null && (o.Current == State.Seek || o.Current == State.Possessing) ? o.seekTarget : o.hider != null ? o.hider.CurrentProp : null;
                if (op == null) continue;
                if (op == p) { taken = true; break; }   // боты делят между собой, кто куда идёт (между собой, не про игроков)
                float od = Vector3.Distance(op.transform.position, p.transform.position);
                if (od < spreadRadius) sc -= spreadWeight * (1f - od / spreadRadius);
            }
            if (taken) continue;
            foreach (var past in pastPicks)
            {
                float pd = Vector3.Distance(past, p.transform.position);
                if (pd < memoryRadius) sc -= memoryWeight * (1f - pd / memoryRadius);
            }
            cands.Add((p, sc));
        }
        cands.Sort((a, b) => b.score.CompareTo(a.score));
        return cands;
    }

    // Жребий по весам exp((оценка − лучшая) / температура).
    int DrawIndex(List<(Prop p, float score)> cands)
    {
        float best = float.MinValue;
        foreach (var c in cands) best = Mathf.Max(best, c.score);
        double sum = 0;
        var w = new double[cands.Count];
        for (int i = 0; i < cands.Count; i++) { w[i] = System.Math.Exp((cands[i].score - best) / Mathf.Max(0.05f, pickTemperature)); sum += w[i]; }
        double r = rng.NextDouble() * sum;
        for (int i = 0; i < w.Length; i++) { r -= w[i]; if (r <= 0) return i; }
        return w.Length - 1;
    }

    // Тест/отладка: вытянуть предмет так же, как в подготовке, но без похода к нему (для статистики по многим «раундам»).
    public Prop DebugDrawPick(Vector3 from)
    {
        var cands = ScoreCandidates(from);
        if (cands.Count == 0) { seekTarget = null; return null; }
        var p = cands[DrawIndex(cands)].p;
        seekTarget = p;
        lastPick = p.transform.position; lastPickSet = true;
        return p;
    }

    // Общие для всех ботов оценки места — раз за раунд: естественность и укрытость каждого предмета.
    static int cacheSalt = int.MinValue, cacheCount = -1;
    static readonly Dictionary<Prop, float> natCache = new Dictionary<Prop, float>(), coverCache = new Dictionary<Prop, float>();
    static readonly List<Vector3> viewPoints = new List<Vector3>();
    public static int ViewPointCount => viewPoints.Count;

    static void EnsureRoundCache()
    {
        if (cacheSalt == RoundSalt && cacheCount == Prop.All.Count) return;
        cacheSalt = RoundSalt; cacheCount = Prop.All.Count;
        BuildViewPoints();
        natCache.Clear(); coverCache.Clear();
        foreach (var p in Prop.All) { natCache[p] = Naturalness(p); coverCache[p] = ComputeCover(p); }
    }

    public static float Cover(Prop p) { EnsureRoundCache(); return coverCache.TryGetValue(p, out var c) ? c : ComputeCover(p); }

    // Точки, откуда охотник может смотреть: равномерная выборка пола навмеша обычного агента (по любой карте) + точка появления охотника.
    static void BuildViewPoints()
    {
        viewPoints.Clear();
        var tri = NavMesh.CalculateTriangulation();
        var f = new NavMeshQueryFilter { agentTypeID = 0, areaMask = NavMesh.AllAreas };
        var cells = new HashSet<Vector2Int>();
        const float cell = 2.5f;   // не больше одной точки на клетку 2,5×2,5 м
        for (int i = 0; i + 2 < tri.indices.Length; i += 3)
        {
            Vector3 c = (tri.vertices[tri.indices[i]] + tri.vertices[tri.indices[i + 1]] + tri.vertices[tri.indices[i + 2]]) / 3f;
            var key = new Vector2Int(Mathf.FloorToInt(c.x / cell), Mathf.FloorToInt(c.z / cell));
            if (cells.Contains(key)) continue;
            if (!NavMesh.SamplePosition(c, out var nh, 0.2f, f) || Mathf.Abs(nh.position.y - c.y) > 0.1f) continue;
            if (nh.position.y > 0.3f) continue;   // охотник ходит по полу
            cells.Add(key);
            viewPoints.Add(nh.position);
        }
        var auth = CombatAuthority.Instance;
        if (auth != null && auth.hunterSpawn != null) viewPoints.Add(auth.hunterSpawn.position);
    }

    // Укрытость = доля точек обзора, из глаз охотника (1,6 м) в которых предмет НЕ виден первым попаданием луча.
    static float ComputeCover(Prop p)
    {
        if (viewPoints.Count == 0 || p.Colliders == null || p.Colliders.Length == 0) return 0.5f;
        Vector3 c = PropCenter(p);
        int seen = 0;
        foreach (var vp in viewPoints)
        {
            Vector3 eye = vp + Vector3.up * 1.6f, d = c - eye;
            float len = d.magnitude;
            if (len < 0.3f) { seen++; continue; }
            int n = Physics.RaycastNonAlloc(eye, d / len, rayBuf, len + 0.05f, RamKickAuthority.StaticMask, QueryTriggerInteraction.Ignore);
            float bestD = float.MaxValue; Collider first = null;
            for (int i = 0; i < n; i++)
            {
                if (rayBuf[i].collider.GetComponentInParent<HiderPlayer>() != null && rayBuf[i].collider.GetComponentInParent<Prop>() != p) continue;
                if (rayBuf[i].distance < bestD) { bestD = rayBuf[i].distance; first = rayBuf[i].collider; }
            }
            if (first == null || first.GetComponentInParent<Prop>() == p) seen++;
        }
        return 1f - seen / (float)viewPoints.Count;
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
        alignPending = false;
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

        // Только что убежал (настороже), а охотник снова близко на виду — идёт по следу: бежать дальше, а не надеяться на маскировку.
        if (Alert && ThreatVisible && SeenDist <= personality.safeDistance) { ResumeFlee(); return; }

        // Повторное превращение по ситуации: после перемещения/побега, когда кулдаун смены готов.
        if (wantMorphCheck && Time.time >= hider.NextRepossessTime && TryStartMorph()) return;

        if (Time.time < nextRelocateAt) return;
        // Естественное перемещение: только со своего навмеша (пол или полка у мелких), если охотник не виден вблизи и недавно не было обнаружения.
        // С плохого места (см. BadSpot) — уходим и вскоре после обнаружения, и при охотнике в стороне (не ближе 4 м).
        bool onNav = SampleHere(out _);
        bool bad = BadSpot();
        bool hunterSeen = ThreatVisible && SeenDist <= (bad ? 4f : personality.noticeRange) && !OnSomething();   // стоять на чужом теле нельзя даже на виду
        if ((!onNav && !bad) || hunterSeen || (!bad && Time.time - DetectedAt < 6f) || !StartRelocate()) { nextRelocateAt = Time.time + (bad ? 1.5f : 3f); }
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
            else if (!FindStandpoint(q, out stand) || TakenByOther(stand)) continue;
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
            // замереть, где стоим, — маскировка, только если место годится (не голый пол для мелочи, не на чужом теле/предмете);
            // иначе бот «прятался» на виду на голом полу у торца стеллажа — идём дальше к цели
            if (linkIdx < 0 && !BadSpot()) { EnterFreeze(); ScheduleRelocate(); return; }
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
        holdSince = -1f;
        zigSign = rng.NextDouble() < 0.5 ? 1f : -1f; zigT = 0f; zigDur = 0.5f;
        weaveAmp = weaveAmplitude * 0.5f;   // виляние видно с первых шагов (в воздухе размах только уменьшается)
        nextJumpAt = Time.time + 0.2f + (float)rng.NextDouble() * 0.5f;
        FleeStartedAt = Time.time;
        // Охотник: виден сейчас, иначе последнее известное положение (недавнее).
        haveThreat = false;
        if (ThreatVisible || Time.time - LastSeenAt < 4f) { threatPos = LastSeenPos; haveThreat = true; }

        // Ответный удар решается один раз на побег, по личности: крупный — таран (ramChance), мелкий — сменить облик на крупный
        // и таранить (morphRamChance). Решил — Z на бег не тратит до 5 с, ждёт момента (охотник близко на виду), а мелкий
        // по возможности бежит к крупному предмету рядом. Раньше Z тратился на бег сразу, и ответ был возможен лишь в первый миг.
        var ramA = RamKickAuthority.Instance;
        bool bigBody = ramA != null && hider.CurrentProp != null && hider.CurrentProp.tier >= ramA.ramMinTier;
        aggressive = rng.NextDouble() < (bigBody ? personality.ramChance : personality.morphRamChance);
        aggressiveUntil = Time.time + 5f;
        if (aggressive) AggressiveFlees++;
        if (aggressive && CanRam()) { BeginRam(); return; }
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
        ramUntil = Time.time + hider.boostDuration * Mathf.Max(0.3f, hider.BoostFill);
        RamCount++;
        input.PressBoost();
    }

    void BeginRun()
    {
        fleeOrigin = hider.transform.position;
        SetState(State.Flee);
        nextRethinkAt = Time.time + 1.5f;
        if (aggressive && Time.time < aggressiveUntil && RunToBigProp()) return;
        if (PickHidePoint(false, out var d) || PickHidePoint(true, out d)) dest = d;
        else Cornered();
    }

    bool aggressive;
    float aggressiveUntil;
    float holdSince = -1f;
    public int AggressiveFlees;

    // Мелкий бот, решивший ответить: бежит к ближайшему среднему/крупному предмету на полу (в пределах 8 м), чтобы оказаться
    // в досягаемости смены облика; кулдаун смены должен быть готов к прибытию.
    bool RunToBigProp()
    {
        var ram = RamKickAuthority.Instance;
        var cur = hider.CurrentProp;
        if (ram == null || cur == null || cur.tier >= ram.ramMinTier) return false;
        if (hider.NextRepossessTime - Time.time > 2f || hider.Boost != HiderPlayer.BoostPhase.Ready) return false;
        Vector3 pos = hider.transform.position;
        var near = new List<(Prop q, float d)>();
        foreach (var q in Prop.All)
        {
            if (q.tier < ram.ramMinTier || q == cur || q.OnShelf) continue;
            float d = Vector3.Distance(q.transform.position, pos);
            if (d < 8f) near.Add((q, d));
        }
        near.Sort((a, b) => a.d.CompareTo(b.d));
        // до трёх ближайших: у предмета у стены точки стоянки со стороны бота может не быть
        for (int i = 0; i < Mathf.Min(3, near.Count); i++)
        {
            var big = near[i].q;
            Vector3 away = Flat(pos - big.transform.position);
            if (away.sqrMagnitude < 0.01f) away = Vector3.forward;
            Vector3 want = big.transform.position + away.normalized * (big.footRadius + 1.2f);
            if (!NavMesh.SamplePosition(new Vector3(want.x, 0.05f, want.z), out var nh, 1.5f, Filter) || nh.position.y > 0.1f) continue;
            if (!PlanPath(nh.position)) continue;
            dest = nh.position;
            RunsToBig++;
            return true;
        }
        return false;
    }
    public int RunsToBig;

    void DecideFlee()
    {
        if (ThreatVisible) { threatPos = LastSeenPos; haveThreat = true; }
        // Сначала ответный удар (смена облика + таран): ему нужен полный Z. Потом уже Z на бег — иначе шкала уходила на бег,
        // и после смены облика таранить было нечем.
        if (aggressive && Time.time >= aggressiveUntil) aggressive = false;   // момент не настал — Z на бег
        if (linkIdx < 0 && !dropping && hider.Grounded && TryMorphRam(aggressive)) return;
        // Z при побеге (если предмет есть и шкала готова): ускорение — часть тех же правил HiderPlayer. Если охотник близко на виду,
        // первые 0,4 с Z придерживается для попытки «сменить облик и таранить» (одна неудачная проверка в первый тик тратила шкалу на бег).
        bool holdZ = aggressive || (Time.time - FleeStartedAt < 0.4f && ThreatVisible && SeenDist <= morphRamRange);
        if (!boostIssued && !holdZ && hider.Boost == HiderPlayer.BoostPhase.Ready) { input.PressBoost(); boostIssued = true; }

        // Единственный «хороший» конец побега: устойчиво спокойно (не видим и далеко fleeUnseenHold секунд подряд).
        if (CalmFor >= fleeUnseenHold && linkIdx < 0) { FleeEndUnseen = CalmFor; EndFlee("safe"); BeginLook(); return; }
        // Страховка от бесконечного бега — только если охотник сейчас не видит; на виду бот не замирает никогда.
        // (только на земле: срабатывая в прыжке, «прятала» бота посреди воздуха) и не замирать где попало, а как после удачного побега:
        // осмотр -> перемещение в новое скрытое место; на маленькой карте охотник почти всегда ближе safeDistance, и это частый случай.
        if (Time.time - FleeStartedAt > fleeFailsafe && !ThreatVisible && linkIdx < 0 && !dropping && hider.Grounded) { EndFlee("failsafe"); BeginLook(); return; }
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
            if (PickHidePoint(false, out var d)) { dest = d; FleeLegs++; holdSince = -1f; }
            // Не видим, а все пути ведут мимо охотника (напр. бот в ячейке полки, охотник рядом): затаиться, не выдавать себя бегом.
            // Но не стоя на чужом теле/предмете (неудачный прыжок) — оттуда лучше рвануть; а затаившись дольше 3 с незамеченным,
            // побег кончается: на маленькой карте охотник почти всегда ближе safeDistance, и бот «держался» десятки секунд.
            else if (!ThreatVisible && !OnSomething())
            {
                if (holdSince < 0f) holdSince = Time.time;
                if (Time.time - holdSince >= 3f) { EndFlee("hidden"); holdSince = -1f; if (BadSpot()) BeginLook(); else SetState(State.Settle); return; }
                cornerUntil = Time.time + 0.5f; HoldCount++;
            }
            else if (PickHidePoint(true, out d)) { dest = d; FleeLegs++; holdSince = -1f; }
            else Cornered();
        }
    }

    void EndFlee(string why) { FleeEndReason = why; lastFleeEndAt = Time.time; }

    // Ответный удар посреди побега: охотник близко и видит бота, Z готов. Уже средний/крупный — таран с шансом ramChance;
    // мелкий — резко сменить облик на средний/крупный предмет рядом (обычная смена облика: кулдаун, дистанция, вид) и сразу таранить.
    // Те же правила и ввод, что у игрока. Бросок не чаще раза в 2 с; force — загнан в угол (без броска).
    bool TryMorphRam(bool force)
    {
        if (!force && Time.time < nextRamRollAt) { MorphRamWhy = "бросок не скоро"; return false; }
        var ram = RamKickAuthority.Instance;
        var prop = hider.CurrentProp;
        Vector3 pos = hider.transform.position;
        if (ram == null || prop == null || !ThreatVisible || SeenDist > morphRamRange || SeenDist < 1.2f) { MorphRamWhy = $"охотник: виден={ThreatVisible}, дист {SeenDist:F1}"; return false; }
        if (hider.Boost != HiderPlayer.BoostPhase.Ready || pos.y > 0.1f || !hider.Grounded) { MorphRamWhy = $"Z={hider.Boost}, y={pos.y:F2}, земля={hider.Grounded}"; return false; }
        if (RoundState.Instance != null && RoundState.Instance.Phase == RoundPhase.Prep) return false;
        threatPos = LastSeenPos; haveThreat = true;
        if (prop.tier >= ram.ramMinTier)
        {
            if (!force) nextRamRollAt = Time.time + 2f;
            if (force || rng.NextDouble() < personality.ramChance) { BeginRam(); return true; }
            return false;
        }
        if (Time.time < hider.NextRepossessTime) { MorphRamWhy = $"кулдаун смены {hider.NextRepossessTime - Time.time:F1} с"; return false; }
        if (!force) { nextRamRollAt = Time.time + 2f; if (rng.NextDouble() >= personality.morphRamChance) { MorphRamWhy = "бросок не выпал"; return false; } }
        // образец: средний/крупный в досягаемости, с прямым видом, и новое тело помещается здесь
        Vector3 eye = hider.EyePosition;
        float reach = PossessionAuthority.Instance.pickDistance - 0.3f;
        Prop best = null; float bestD = float.MaxValue;
        foreach (var q in Prop.All)
        {
            if (q.tier < ram.ramMinTier || q == prop || q.Colliders.Length == 0) continue;
            float d = Vector3.Distance(eye, q.Colliders[0].bounds.ClosestPoint(eye));
            if (d > reach || d >= bestD || !BodyFits(q) || !ClearSight(eye, q)) continue;
            best = q; bestD = d;
        }
        if (best == null)
        {
            int inReach = 0, fits = 0;
            foreach (var q in Prop.All)
            {
                if (q.tier < ram.ramMinTier || q == prop || q.Colliders.Length == 0) continue;
                if (Vector3.Distance(eye, q.Colliders[0].bounds.ClosestPoint(eye)) > reach) continue;
                inReach++; if (BodyFits(q)) fits++;
            }
            MorphRamWhy = $"нет образца: в досягаемости {inReach}, помещается {fits}, мешает {fitBlock}";
            return false;
        }
        MorphRamWhy = "ок: " + best.ModelId;
        morphTarget = best;
        morphThenRam = true;
        MorphRams++;
        input.move = Vector2.zero;
        SetState(State.Morphing);
        return true;
    }

    // Тело образца помещается на месте бота (без статики и чужих тел) — иначе крупный облик войдёт в стеллаж.
    bool BodyFits(Prop q)
    {
        float r = Mathf.Clamp(Mathf.Min(q.footRadius, q.height * 0.5f), 0.05f, 0.5f), h = Mathf.Max(q.height, r * 2f);
        Vector3 pos = hider.transform.position;
        int n = Physics.OverlapCapsuleNonAlloc(pos + Vector3.up * (r + 0.03f), pos + Vector3.up * (h - r + 0.03f), r, overlapBuf, RamKickAuthority.StaticMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
            if (!overlapBuf[i].transform.IsChildOf(hider.transform)) { fitBlock = overlapBuf[i].name + (overlapBuf[i].GetComponentInParent<HiderPlayer>() != null ? "(тело " + overlapBuf[i].GetComponentInParent<HiderPlayer>().name + ")" : ""); return false; }
        return true;
    }
    string fitBlock = "";

    // Загнан в угол: таран, если возможен; иначе рывок — Z и прыжок, точка подальше даже ценой пути мимо охотника.
    void Cornered()
    {
        CorneredCount++;
        cornerUntil = Time.time + 0.6f;
        if (CanRam()) { BeginRam(); return; }
        if (TryMorphRam(true)) return;
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
        lookUntil = Time.time + 0.5f + (float)rng.NextDouble() * 0.6f;   // короткий осмотр: дольше выглядело как «застыл у угла»
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
            if (TakenByOther(cand)) continue;
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
            if (Flat(p - from).magnitude > 14f || !FreeSpot(p) || TakenByOther(p)) continue;
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
            if (!FreeSpot(p) || TakenByOther(p)) continue;
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
        if (BadSpot()) { nextRelocateAt = Time.time + 1.5f + (float)rng.NextDouble(); BadSettles++; }   // затаился в плохом месте — скоро перебраться
        else PlanAlign();
    }

    public int BadSettles;

    // Место для затаивания плохое: стоит на чужом теле или предмете (неудачный прыжок, бот на боте), не на своём навмеше
    // (верх стеллажа), мелкий предмет на голом полу без таких же рядом, крупный — посреди прохода без таких же рядом.
    // Стоит на чужом теле или предмете (неудачный прыжок, бот на боте)?
    public bool OnSomething()
    {
        Vector3 pos = hider.transform.position;
        int n = Physics.RaycastNonAlloc(pos + Vector3.up * 0.05f, Vector3.down, rayBuf, 0.2f, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var c = rayBuf[i].collider;
            if (c.transform.IsChildOf(hider.transform)) continue;
            if (c.GetComponentInParent<Prop>() != null || c.GetComponentInParent<HiderPlayer>() != null) return true;
        }
        // капсула сверху на капсуле: луч вниз по оси может пройти мимо — проверяем и чужие тела прямо под собой
        foreach (var h in HiderPlayer.All)
            if (h != hider && !h.Caught && FlatDist(h.transform.position, pos) < h.BodyRadius + hider.BodyRadius && pos.y - h.transform.position.y > 0.05f && pos.y - h.transform.position.y < h.BodyHeight + 0.15f) return true;
        return false;
    }

    public bool BadSpot()
    {
        Vector3 pos = hider.transform.position;
        var cur = hider.CurrentProp;
        if (cur == null) return false;
        if (OnSomething()) return true;
        // вплотную к другому прячущемуся — два одинаковых предмета, прижатых друг к другу, выдают обоих
        foreach (var h in HiderPlayer.All)
            if (h != hider && !h.Caught && FlatDist(h.transform.position, pos) < 0.6f && Mathf.Abs(h.transform.position.y - pos.y) < 0.15f) return true;   // на том же уровне (полка выше/ниже — не соседи)
        // не на своём навмеше — плохо, кроме стоящих на уровне полки: среди плотных предметов вырезы съедают полосу навмеша
        // целиком, и бот на отличном месте среди таких же считался бы «не на навмеше»
        if (!SampleHere(out _) && !(cur.tier >= PropTier.Medium && pos.y > 0.15f) && !OnShelfSlot(pos)) return true;
        if (pos.y < 0.1f)
        {
            if (cur.tier <= PropTier.Small && LocalFitR(pos, cur.ModelId, cur, 1.2f, true) == 0) return true;
            if (cur.tier >= PropTier.Medium && LocalFitR(pos, cur.ModelId, cur, 2.5f, true) == 0 && EnclosedSides(pos + Vector3.up * 0.15f) < 2) return true;
        }
        return false;
    }

    static bool OnShelfSlot(Vector3 foot)
    {
        foreach (var s in ShelfSlot.All) if (s.Contains(foot, 0.08f)) return true;
        return false;
    }

    static int LocalFitR(Vector3 pos, string model, Prop except, float r, bool sameLevel)
    {
        int n = 0;
        foreach (var q in Prop.All)
        {
            if (q == except || q.ModelId != model || (q.transform.position - pos).sqrMagnitude > r * r) continue;
            if (q.occupant != null) continue;   // чужое тело-предмет не в счёт: два бота-Snack в проходе «подтверждали» друг друга
            if (sameLevel && Mathf.Abs(q.transform.position.y - q.height * 0.5f - pos.y) > 0.1f) continue;   // на полу — только на полу (основание стеллажа 0,2 м уже не пол)
            n++;
        }
        return n;
    }

    // Точка занята другим прячущимся (стоит там или идёт туда) — не выбирать: иначе боты садились друг на друга.
    bool TakenByOther(Vector3 p)
    {
        foreach (var h in HiderPlayer.All)
        {
            if (h == hider || h.Caught) continue;
            Vector3 q = h.transform.position;
            if (FlatDist(q, p) < 0.7f && Mathf.Abs(q.y - p.y) < 0.5f) return true;
        }
        foreach (var o in All)
        {
            if (o == this || (o.Current != State.Flee && o.Current != State.Relocate && o.Current != State.Seek)) continue;
            if (FlatDist(o.dest, p) < 0.7f && Mathf.Abs(o.dest.y - p.y) < 0.5f) return true;
        }
        return false;
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
        // Скольжение вдоль стенки: впереди угол/стенка — курс вдоль поверхности (и чуть от неё), а не носом в неё.
        // Раньше бот срезал угол торца стеллажа, упирался в ребро почти перпендикулярно и стоял, «нажимая» вперёд, 2-9 с.
        if (to.sqrMagnitude > 1e-6f && SlideAlongWall(pos, to.normalized, to.magnitude, out var slid)) { input.yaw = Mathf.Atan2(slid.x, slid.z) * Mathf.Rad2Deg; }
        else if (flee)
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

    public int Slides;   // тест: сколько кадров бот скользил вдоль стенки

    // Щуп телом вперёд на 0,3 м: при касании статики (или чужого тела) — направление вдоль поверхности к цели.
    bool SlideAlongWall(Vector3 pos, Vector3 dir, float toTarget, out Vector3 slid)
    {
        slid = dir;
        if (!hider.Grounded) return false;
        float r = hider.BodyRadius;
        Vector3 o = pos + Vector3.up * Mathf.Max(r + 0.03f, Mathf.Min(0.15f, hider.BodyHeight * 0.5f));
        // не дальше цели: точка стоянки у стенки (напр. у полки) не должна «отталкивать» бота от себя
        float len = Mathf.Min(0.3f, toTarget - r);
        if (len <= 0.02f) return false;
        int n = Physics.SphereCastNonAlloc(o, r * 0.9f, dir, rayBuf, len, RamKickAuthority.StaticMask, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue; Vector3 nrm = Vector3.zero;
        for (int i = 0; i < n; i++)
        {
            var h = rayBuf[i];
            if (h.collider.transform.IsChildOf(hider.transform) || h.distance <= 0f) continue;
            if (h.distance < best) { best = h.distance; nrm = h.normal; }
        }
        if (best == float.MaxValue) return false;
        nrm.y = 0f;
        if (nrm.sqrMagnitude < 1e-4f) return false;
        nrm.Normalize();
        if (Vector3.Dot(dir, nrm) > -0.05f) return false;   // поверхность не поперёк пути
        Vector3 along = dir - nrm * Vector3.Dot(dir, nrm);
        if (along.sqrMagnitude < 0.04f)
        {
            // упёрлись точно в лоб: вдоль стенки в сторону следующего угла пути
            Vector3 side = Vector3.Cross(Vector3.up, nrm);
            Vector3 next = ci + 1 < corners.Length ? Flat(corners[ci + 1] - pos) : Flat(corners[ci] - pos);
            along = side * (Vector3.Dot(side, next) >= 0f ? 1f : -1f);
        }
        slid = (along.normalized + nrm * 0.35f).normalized;
        Slides++;
        return true;
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
            float ampMax = ThreatVisible ? weaveAmplitude : weaveAmplitude * 0.5f;   // на виду — шире, чтобы сложнее попасть
            float span = ampMax + m + 0.4f;
            Vector3 q = PursuitPoint(pos, 0f, out _, out _);
            float fr = Mathf.Min(Free(p, n, span), Free(q, n, span)), fl = Mathf.Min(Free(p, -n, span), Free(q, -n, span));
            wantCenter = Mathf.Clamp((fr - fl) * 0.5f, -0.6f, 0.6f);
            wantAmp = Mathf.Clamp((fr + fl) * 0.5f - m, 0f, ampMax);
            // крайние положения center ± amp не дальше свободного места с каждой стороны (центр ограничен ±0,6, поэтому проверяем явно)
            wantAmp = Mathf.Max(0f, Mathf.Min(wantAmp, Mathf.Min(fr - m - wantCenter, fl - m + wantCenter)));
        }
        // сужаться быстро (впереди стена), расширяться медленно (плавная волна)
        float kUp = 1f - Mathf.Exp(-4f * dt), kDown = 1f - Mathf.Exp(-12f * dt);
        weaveAmp = Mathf.Lerp(weaveAmp, wantAmp, wantAmp < weaveAmp ? kDown : kUp);
        weaveCenter = Mathf.Lerp(weaveCenter, wantCenter, Mathf.Abs(wantCenter) < Mathf.Abs(weaveCenter) || wantCenter * weaveCenter < 0f ? kDown : kUp);
        // Зигзаг из полуволн случайной длины и размаха (вправо, влево, вправо...): внутри полуволны плавно, но ритм не угадать.
        zigT += dt;
        if (zigT >= zigDur) NextZig(!hider.Grounded);
        float shape = Mathf.Sin(Mathf.PI * Mathf.Clamp01(zigT / zigDur));
        // у остановки (конец пути, начало перехода) смещение сходит на нет сразу, а не через сглаживание — иначе цель
        // оказывалась в сантиметрах сбоку от бота и курс метался на 180°
        float fade = Mathf.Clamp01((remain - 0.4f) / 1.2f);
        WeaveOffset = (weaveCenter + weaveAmp * zigAmp * zigSign * shape) * fade;
        Vector3 target = p + n * WeaveOffset;
        // цель вне прямой досягаемости по навмешу (срезали угол стеллажа): сперва без смещения, потом угол пути
        Vector3 from = new Vector3(pos.x, corners[Mathf.Max(0, ci - 1)].y, pos.z);
        if (NavMesh.Raycast(from, new Vector3(target.x, p.y, target.z), out _, Filter))
            target = NavMesh.Raycast(from, p, out _, Filter) ? corners[ci] : p;
        return target;
    }

    public float fleeTurnRate = 720f;

    // Следующая полуволна зигзага в другую сторону. В воздухе — короткие (~0,45 с): за прыжок ~0,9 с бот успевает качнуться туда и обратно.
    void NextZig(bool air)
    {
        zigT = 0f;
        zigSign = -zigSign;
        float r = (float)rng.NextDouble();
        zigDur = air ? 0.4f + 0.1f * r : Mathf.Lerp(zigHalfMin, zigHalfMax, r);
        zigAmp = 0.55f + 0.45f * (float)rng.NextDouble();
    }   // °/с: предел поворота курса при побеге (синусоиде нужно ~90°/с), гасит рывки команды

    // Свободное расстояние по горизонтали от точки пути в сторону dir (низ и середина тела).
    float Free(Vector3 p, Vector3 dir, float max)
    {
        float y0 = Mathf.Min(hider.transform.position.y, p.y + 0.02f);   // уровень пути: в прыжке лучи от бота шли бы над стеллажами
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
        // в отрыв — новая короткая полуволна в другую сторону: в воздухе заметно качает влево-вправо
        NextZig(true); zigAmp = 1f; ZigJumps++;
        nextJumpAt = Time.time + 0.6f + (float)rng.NextDouble() * 0.8f;
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
    bool faceStarted;
    Vector3 linkNormal;
    bool linkPush;
    public string LastLinkLog = "";

    void BeginLink(int k)
    {
        linkIdx = k; linkPhase = LinkPhase.Approach; linkT = 0f; faceT = 0f; faceStarted = false; linkPush = false; linkTries = 0;
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
                // Весь подход ограничен по времени: иначе после промаха бот мог уйти с карты (было: из-за стеллажа в дверной проём).
                if (linkT > 3f) { EndLink(false, $"вверх на {e.y:F2}: не подошёл к точке перехода"); return; }
                Vector3 toS = Flat(s - pos);
                // Сначала встать в саму точку перехода (не только напротив по длине полки): после промаха бот мог оказаться
                // по другую сторону стеллажа, считался «напротив» и шёл «к кромке» прочь от неё через весь зал.
                if (!faceStarted && (toS.magnitude > 0.12f || pos.y > s.y + 0.15f))
                {
                    if (toS.sqrMagnitude > 0.0025f) input.yaw = Mathf.Atan2(toS.x, toS.z) * Mathf.Rad2Deg;
                    input.move = toS.sqrMagnitude > 0.0025f ? Vector2.up : Vector2.zero;
                    faceT = 0f;
                    break;
                }
                faceStarted = true;
                faceT += Time.deltaTime;
                bool touching = Physics.Raycast(pos + Vector3.up * 0.05f, dir, hider.BodyRadius + 0.06f, RamKickAuthority.StaticMask, QueryTriggerInteraction.Ignore);
                // кромка в ~0,3 м от точки перехода: дальше 0,6 м или дольше 0,4 с не идём — прыгаем как есть (промах -> повтор)
                bool tooFar = Vector3.Dot(Flat(pos - s), dir) > 0.6f;
                if ((touching || faceT > 0.4f || tooFar) && hider.Grounded)
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
                        else { linkPhase = LinkPhase.Approach; linkT = 0f; faceT = 0f; faceStarted = false; }
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
        Diag.AppendLine($"{Time.time:F3} {Current} плохое={BadSpot()} ci={ci}/{corners.Length} цель ({dest.x:F2};{dest.y:F2};{dest.z:F2}) видит={ThreatVisible} дист {ThreatDist:F2} спокойно {CalmFor:F2} | бот ({p.x:F3};{p.y:F3};{p.z:F3}) охотн ({q.x:F2};{q.y:F2};{q.z:F2}) | вбок {WeaveOffset:F3} ампл {weaveAmp:F2} | ввод ({input.move.x:F2};{input.move.y:F2}) yaw {input.yaw:F0} | земля={hider.Grounded} переход={linkIdx >= 0} {(linkIdx >= 0 ? linkPhase + " push=" + linkPush + " clr=" + linkClear.ToString("F2") : "")}");
    }
}
