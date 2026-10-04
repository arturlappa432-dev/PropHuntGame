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

// Бот-прячущийся (docs/systems/bots.md). Конечный автомат: Seek (выбор предмета) -> Freeze (замереть, основное состояние)
// -> изредка Relocate; Reacting -> Flee/Ram -> Settle только при реальном обнаружении (попадание, near-miss, пинок).
// Приближение охотника само по себе не триггерит ничего. Ввод идёт через BotInput и правила HiderPlayer (скорость, кулдаун смены,
// дистанция вселения, Z/таран) — как у игрока. Охотника бот «видит» только прямой видимостью (луч), без знания, кто настоящий игрок.
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

    HiderPlayer hider;
    BotInput input;
    System.Random rng;
    float tickTimer, stateSince, nextRelocateAt, reactAt, ramUntil, lookUntil, lookBaseYaw, weavePhase, nextJumpAt, seekGiveUpAt, stuckT;
    bool wantMorphCheck, boostIssued;
    Vector3 lastPos, threatPos, lastKnownThreat;
    float lastKnownThreatAt = -100f;
    bool haveThreat;
    Vector3 fleeOrigin;
    public float fleeFailsafe = 25f;      // страховка от бесконечного бега, если безопасность недостижима
    public float fleeUnseenHold = 1.25f;  // сколько секунд подряд охотник не виден (и далеко), чтобы побег считался законченным
    float unseenSince = -1f;
    public float FleeEndUnseen;           // отладка: сколько секунд подряд охотник не был виден к моменту конца последнего побега
    public int UnseenResets;              // отладка: сколько раз накопленная невидимость сбрасывалась (мигание видимости)
    public float weaveAmplitude = 1.0f;   // боковое виляние при побеге (доля бокового ввода)
    public float weaveFrequency = 3.4f;   // синус sin(t·f·2): период ~0,92 с на полный зигзаг
    public float noiseAmplitude = 2.2f;   // разброс оценки выбора предмета, перемешивается каждый раунд
    public static int RoundSalt = System.Environment.TickCount;   // новый раунд — новая соль (ReseedRound)
    float speedFactor = 1f;
    int fleeRepicks;
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
        // Где охотник: только если он в прямой видимости (при попадании выстрел дошёл по лучу, значит был виден).
        if (Sense(out var hp, out _, 60f)) { lastKnownThreat = hp; lastKnownThreatAt = Time.time; }
        if (Current == State.Reacting) return;
        if (Current == State.Flee || Current == State.Ram) { if (lastKnownThreatAt == Time.time) { haveThreat = true; threatPos = lastKnownThreat; } return; }
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

    void Update()
    {
        if (hider == null || input == null || hider.Caught) return;

        // Оглушён пинком / идёт засасывание: ввода нет.
        if (hider.Stun != HiderPlayer.StunPhase.None) { input.move = Vector2.zero; corners = System.Array.Empty<Vector3>(); return; }
        if (hider.Busy) { input.move = Vector2.zero; return; }

        // Реакция на обнаружение отсчитывается каждый кадр, а не по тику: частота решений на поведение не влияет.
        if (Current == State.Reacting && Time.time >= reactAt) StartFlee();

        tickTimer -= Time.deltaTime;
        if (tickTimer <= 0f)
        {
            Decide();
            tickTimer = NearestHunterDistance() < nearRange ? 0f : farTick;
        }
        Act();
        if (Diag != null && (Current == State.Flee || Current == State.Look || Current == State.Reacting)) DiagFrame();
    }

    // ---------- Диагностика видимости (включается тестом: Diag = new StringBuilder()) ----------
    public System.Text.StringBuilder Diag;

    void DiagFrame()
    {
        var h = HunterPlayer.All.Count > 0 ? HunterPlayer.All[0] : null;
        if (h == null) return;
        Vector3 eye = hider.EyePosition, chest = h.transform.position + Vector3.up * 1.2f;
        string botRay = Physics.Linecast(eye, chest, out var b1, RamKickAuthority.StaticMask, QueryTriggerInteraction.Ignore) ? $"блок {b1.collider.name}@{b1.distance:F2}" : "чисто";
        string hunRay = Physics.Linecast(h.EyePosition, hider.BodyCenter, out var b2, RamKickAuthority.StaticMask, QueryTriggerInteraction.Ignore) ? $"блок {b2.collider.name}@{b2.distance:F2}" : "чисто";
        var p = hider.transform.position; var q = h.transform.position;
        Diag.AppendLine($"{Time.time:F3} {Current} бот->охотн: {botRay} | охотн->бот: {hunRay} | дист {Vector3.Distance(p, q):F2} | бот ({p.x:F2};{p.y:F2};{p.z:F2}) охотн ({q.x:F2};{q.y:F2};{q.z:F2}) | невидим с {(unseenSince < 0 ? "-" : (Time.time - unseenSince).ToString("F2"))} | ввод ({input.move.x:F2};{input.move.y:F2})");
    }

    void SetState(State s) { Current = s; stateSince = Time.time; }

    // Только для частоты тиков: расстояние до ближайшего охотника.
    float NearestHunterDistance()
    {
        float best = float.MaxValue;
        foreach (var h in HunterPlayer.All) best = Mathf.Min(best, Vector3.Distance(h.transform.position, hider.transform.position));
        return best;
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
                Follow();
                break;
            case State.Flee:
                Follow();
                Evade();
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

    // Точка на NavMesh вокруг предмета: в досягаемости вселения, с прямым лучом из глаз к предмету.
    bool FindStandpoint(Prop p, out Vector3 stand)
    {
        stand = default;
        Vector3 c = PropCenter(p);
        float eyeH = Mathf.Max(0.25f, hider.StickmanHeight * 0.9f);
        Vector3 start = hider.transform.position;
        float bestLen = float.MaxValue;
        bool ok = false;
        for (int ring = 0; ring < 3; ring++)
        {
            float r = pickReach * (0.55f + 0.2f * ring);
            for (int k = 0; k < 14; k++)
            {
                float a = k * Mathf.PI * 2f / 14f + ring * 0.2f;
                Vector3 want = new Vector3(c.x + Mathf.Cos(a) * r, 0f, c.z + Mathf.Sin(a) * r);
                if (!NavMesh.SamplePosition(new Vector3(want.x, 0.1f, want.z), out var nh, 0.6f, NavMesh.AllAreas) || nh.position.y > 0.3f) continue;
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
        input.move = Vector2.zero;
        SetState(State.Freeze);
        SettledAt = Time.time;
        if (FirstFreezeAt < 0f) FirstFreezeAt = Time.time;
    }

    void ScheduleRelocate()
    {
        nextRelocateAt = personality.relocateEvery <= 0f ? float.MaxValue : Time.time + personality.relocateEvery * (0.5f + (float)rng.NextDouble());
    }

    void DecideFreeze()
    {
        if (RoundState.Instance != null && RoundState.Instance.Phase == RoundPhase.Prep) return;

        // Повторное превращение по ситуации: после перемещения/побега, когда кулдаун смены готов.
        if (wantMorphCheck && Time.time >= hider.NextRepossessTime && TryStartMorph()) return;

        if (Time.time < nextRelocateAt) return;
        // Естественное перемещение: только с пола, если охотник не виден вблизи и недавно не было обнаружения.
        bool onFloor = OnFloor();
        bool hunterSeen = Sense(out _, out float hd, personality.noticeRange) && hd <= personality.noticeRange;
        if (!onFloor || hunterSeen || Time.time - DetectedAt < 6f || !StartRelocate()) { nextRelocateAt = Time.time + 3f; }
    }

    // Стоит ли бот на полу (под ногами NavMesh), а не на полке.
    bool OnFloor()
    {
        return SampleFloor(hider.transform.position, 0.5f, out var nh) && Mathf.Abs(nh.y - hider.transform.position.y) < 0.3f;
    }

    // ---------- Перемещение ----------

    bool StartRelocate(bool afterFlee = false)
    {
        // Цель: стоянка рядом с «естественным» кластером однотипных предметов в пределах 10 м.
        Prop best = null; float bestScore = float.MinValue;
        Vector3 pos = hider.transform.position;
        foreach (var q in Prop.All)
        {
            if (q == hider.CurrentProp) continue;
            float d = Vector3.Distance(q.transform.position, pos);
            if (d < 2f || d > 10f) continue;
            if (afterFlee && Vector3.Distance(q.transform.position, fleeOrigin) < 3f) continue;   // новое место, не то же самое
            float s = Naturalness(q) - 0.05f * d + (float)rng.NextDouble() * 1.0f;
            if (afterFlee && haveThreat && Physics.Linecast(threatPos + Vector3.up * 1.6f, q.transform.position + Vector3.up * 0.2f, RamKickAuthority.StaticMask, QueryTriggerInteraction.Ignore)) s += 1.5f;   // скрытее от охотника
            if (s > bestScore && LocalFit(q.transform.position, q.ModelId, q) >= 2) { bestScore = s; best = q; }
        }
        if (best == null || !FindStandpoint(best, out var stand) || !PlanPath(stand)) return false;
        dest = stand;
        morphTarget = best;
        wantMorphCheck = true;
        SetState(State.Relocate);
        return true;
    }

    void DecideRelocate()
    {
        // Увидели охотника близко — тут же замираем, где стоим (двигаться на виду нельзя).
        if (Sense(out _, out float hd, personality.noticeRange) && hd <= personality.noticeRange) { EnterFreeze(); ScheduleRelocate(); return; }
        if (ci >= corners.Length) { stuckFailed = false; SetState(State.Settle); ScheduleRelocate(); }
    }

    // ---------- Побег ----------

    void StartFlee()
    {
        boostIssued = false;
        unseenSince = -1f;
        fleeRepicks = 0;
        weavePhase = (float)rng.NextDouble() * 6.28f;
        nextJumpAt = Time.time + 0.2f + (float)rng.NextDouble() * 0.5f;
        FleeStartedAt = Time.time;
        // Охотник: виден сейчас, иначе последнее известное положение (недавнее).
        haveThreat = false;
        if (Sense(out var hp, out _, 60f)) { threatPos = hp; haveThreat = true; }
        else if (Time.time - lastKnownThreatAt < 4f) { threatPos = lastKnownThreat; haveThreat = true; }

        var prop = hider.CurrentProp;
        // Таран: Medium/Large, Z готов, охотник близко и виден, шанс по личности.
        if (haveThreat && prop != null && prop.tier >= RamKickAuthority.Instance.ramMinTier && hider.Boost == HiderPlayer.BoostPhase.Ready
            && Vector3.Distance(threatPos, hider.transform.position) <= ramRange && rng.NextDouble() < personality.ramChance)
        {
            SetState(State.Ram);
            ramUntil = Time.time + hider.boostDuration;
            RamCount++;
            input.PressBoost();
            return;
        }
        BeginRun();
    }

    void BeginRun()
    {
        fleeOrigin = hider.transform.position;
        if (PickHidePoint(out var d)) { dest = d; SetState(State.Flee); }
        else { SetState(State.Settle); }
    }

    void DecideFlee()
    {
        // Z при побеге (если предмет есть и шкала готова): ускорение — часть тех же правил HiderPlayer
        if (!boostIssued && hider.Boost == HiderPlayer.BoostPhase.Ready) { input.PressBoost(); boostIssued = true; }
        // Реальное условие безопасности: охотника не видно И он дальше safeDistance (время не считается).
        bool los = Sense(out var hp, out float seenDist, 60f) | HunterSeesMe();
        if (los && hp != default) { threatPos = hp; haveThreat = true; lastKnownThreat = hp; lastKnownThreatAt = Time.time; }
        float dist = los && seenDist < float.MaxValue ? seenDist : DistToNearestHunter();
        // Устойчивая невидимость: охотник должен быть вне видимости и далеко непрерывно fleeUnseenHold секунд, один кадр не считается.
        if (!los && dist >= personality.safeDistance)
        {
            if (unseenSince < 0f) unseenSince = Time.time;
            if (Time.time - unseenSince >= fleeUnseenHold) { FleeEndUnseen = Time.time - unseenSince; BeginLook(); return; }
        }
        else { if (unseenSince >= 0f) UnseenResets++; unseenSince = -1f; }
        if (Time.time - FleeStartedAt > fleeFailsafe) { SetState(State.Settle); return; }
        // Добежал до точки, а небезопасно — выбираем следующую точку с учётом свежей позиции охотника.
        if (ci >= corners.Length || stuckFailed)
        {
            stuckFailed = false;
            if (++fleeRepicks > 3) { SetState(State.Settle); return; }   // не удаётся ни добежать, ни спуститься (напр. крупный предмет на полке): затаиться
            if (PickHidePoint(out var d)) dest = d; else SetState(State.Settle);
        }
    }

    // Симметричная проверка: со стороны охотника (глаза -> центр тела бота) нет статики на пути.
    bool HunterSeesMe()
    {
        foreach (var h in HunterPlayer.All)
            if (!Physics.Linecast(h.EyePosition, hider.BodyCenter, RamKickAuthority.StaticMask, QueryTriggerInteraction.Ignore)) return true;
        return false;
    }

    float DistToNearestHunter()
    {
        float best = float.MaxValue;
        foreach (var h in HunterPlayer.All) best = Mathf.Min(best, Vector3.Distance(h.transform.position, hider.transform.position));
        return best;
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
        if (Time.time < lookUntil) return;
        if (!StartRelocate(true)) SetState(State.Settle);
    }

    // Уклонение при побеге: боковое виляние поверх пути + прыжки (тот же ввод, что у игрока). Не на полке-спуске.
    void Evade()
    {
        if (dropping || ci >= corners.Length) return;
        float s = Mathf.Sin(Time.time * weaveFrequency * 2f + weavePhase);
        Vector3 pos = hider.transform.position;
        Quaternion yawQ = Quaternion.Euler(0f, input.yaw, 0f);
        // виляем только в сторону, где есть место (в проходе 1 м боком упираться в стеллаж = застревать)
        Vector3 side = yawQ * Vector3.right * Mathf.Sign(s);
        if (Physics.Raycast(pos + Vector3.up * 0.3f, side, 0.6f, RamKickAuthority.StaticMask, QueryTriggerInteraction.Ignore)) s = 0f;
        input.move = new Vector2(s * weaveAmplitude, 1f);
        // прыгаем с пола и только когда впереди свободно: иначе прыжок вбок/вперёд закидывает на полку
        bool onFloor = pos.y < 0.3f;
        Vector3 fwd = yawQ * Vector3.forward;
        bool clear = !Physics.Raycast(pos + Vector3.up * 0.3f, fwd, 2f, RamKickAuthority.StaticMask, QueryTriggerInteraction.Ignore)
                  && !Physics.Raycast(pos + Vector3.up * 1.2f, fwd, 2f, RamKickAuthority.StaticMask, QueryTriggerInteraction.Ignore)
                  && Mathf.Abs(s) < 0.35f;
        if (Time.time >= nextJumpAt && onFloor && clear)
        {
            input.PressJump();
            nextJumpAt = Time.time + 0.7f + (float)rng.NextDouble() * 1.1f;
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
        if (Sense(out var hp, out _, 60f)) threatPos = hp;
        Vector3 d = threatPos - hider.transform.position; d.y = 0f;
        if (d.sqrMagnitude > 0.01f) input.yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        input.move = Vector2.up;
    }

    // Точка укрытия: вне видимости охотника, подальше, естественная, с путём в обход охотника.
    bool PickHidePoint(out Vector3 best)
    {
        best = default;
        Vector3 pos = hider.transform.position;
        // Стартовая точка на навмеше (если бот на полке — ближайшая точка внизу; Follow сперва спрыгнет).
        if (!SampleFloor(pos, 4f, out var fromPos)) return false;
        var from = new NavMeshHit { position = fromPos };
        float bestScore = float.MinValue;
        bool found = false;
        var path = new NavMeshPath();
        for (int i = 0; i < 14; i++)
        {
            float a = (float)rng.NextDouble() * Mathf.PI * 2f, r = 3f + (float)rng.NextDouble() * 10f;
            Vector3 want = from.position + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r;
            if (!NavMesh.SamplePosition(want, out var nh, 1.5f, NavMesh.AllAreas) || nh.position.y > 0.3f) continue;
            if (!NavMesh.CalculatePath(from.position, nh.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete) continue;
            float len = PathLength(path.corners);
            float s = -0.08f * len;
            if (haveThreat)
            {
                bool hidden = Physics.Linecast(threatPos + Vector3.up * 1.6f, nh.position + Vector3.up * 0.3f, RamKickAuthority.StaticMask, QueryTriggerInteraction.Ignore);
                s += hidden ? 3f : 0f;
                s += Mathf.Min(Vector3.Distance(threatPos, nh.position), 20f) * 0.15f;
                if (MinDistToPath(path.corners, threatPos) < 2.5f) s -= 3f;
            }
            s += Mathf.Min(CountNear(nh.position, 2.5f), 4) * 0.2f;
            if (EnclosedSides(nh.position + Vector3.up * 0.1f) >= 2) s += 0.5f;
            if (s > bestScore) { bestScore = s; best = nh.position; found = true; }
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

    // ---------- Видимость охотника ----------

    // Охотник в прямой видимости в пределах range (луч от глаз бота к груди охотника, без слоя тел). Позиция и дистанция — для решений.
    bool Sense(out Vector3 pos, out float dist, float range)
    {
        pos = default; dist = float.MaxValue;
        Vector3 eye = hider.EyePosition;
        int mask = RamKickAuthority.StaticMask;
        foreach (var h in HunterPlayer.All)
        {
            Vector3 chest = h.transform.position + Vector3.up * 1.2f;
            float d = Vector3.Distance(eye, chest);
            if (d > range || d >= dist) continue;
            if (Physics.Linecast(eye, chest, mask, QueryTriggerInteraction.Ignore)) continue;
            pos = h.transform.position; dist = d;
        }
        return dist < float.MaxValue;
    }

    // ---------- Движение по NavMesh ----------

    bool PlanPath(Vector3 target)
    {
        var path = new NavMeshPath();
        Vector3 pos = hider.transform.position;
        if (!SampleFloor(pos, 4f, out var from)) { lastPlanFail = $"нет навмеша у бота {pos}"; return false; }
        if (!NavMesh.CalculatePath(from, target, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
        { lastPlanFail = $"путь {from} -> {target}: {path.status}"; return false; }
        corners = path.corners; ci = 0; stuckT = 0f; lastPos = pos;
        stuckFailed = false;
        // Бот выше пола (на полке): сперва спрыгнуть к ближайшей точке навмеша по прямой, потом идти по пути.
        dropping = pos.y - from.y > 0.3f;
        dropTarget = from; dropT = 0f;
        return true;
    }

    // Ближайшая точка навмеша на уровне пола (не крыша и не верх стеллажа), поиск расширяется до maxR.
    static bool SampleFloor(Vector3 pos, float maxR, out Vector3 floor)
    {
        foreach (float r in new[] { 0.3f, 0.8f, 1.5f, maxR })
            if (r <= maxR && NavMesh.SamplePosition(new Vector3(pos.x, 0.05f, pos.z), out var nh, r, NavMesh.AllAreas) && nh.position.y < 0.3f) { floor = nh.position; return true; }
        floor = default;
        return false;
    }

    // Идём по углам пути; если бот на полке (выше пола) — сперва к ближайшей точке навмеша по прямой (спрыгнуть).
    void Follow()
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
                if (dd.sqrMagnitude > 0.0004f) input.yaw = Mathf.Atan2(dd.x, dd.z) * Mathf.Rad2Deg;
                input.move = Vector2.up;
                return;
            }
        }
        Vector3 to = corners[ci] - pos; to.y = 0f;
        while (to.magnitude < arriveDist && ci < corners.Length - 1) { ci++; to = corners[ci] - pos; to.y = 0f; }
        // на последней точке допуск жёстче: вселение зависит от луча из глаз на конкретный предмет, 30 см погрешности закрывают его полкой
        if (to.magnitude < (ci == corners.Length - 1 ? finalArriveDist : arriveDist)) { ci = corners.Length; input.move = Vector2.zero; return; }
        input.yaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
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

    void AimAtProp(Prop p)
    {
        Vector3 d = PropCenter(p) - hider.EyePosition;
        input.yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        input.pitch = -Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;
    }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    static float PathLength(Vector3[] c)
    {
        float l = 0f;
        for (int i = 1; i < c.Length; i++) l += Vector3.Distance(c[i - 1], c[i]);
        return l;
    }

    static float MinDistToPath(Vector3[] c, Vector3 p)
    {
        float best = float.MaxValue;
        for (int i = 1; i < c.Length; i++)
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
}
