using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

// Автотест ботов-прячущихся без MCP: флаг Temp/bot_test.flag -> настройка сцены -> Play -> отчёт Temp/bot_test.txt.
// Охотник управляется кодом тем же путём, что и при F2 (HunterPlayer.AimAt/Fire -> CombatAuthority.TryFire).
[InitializeOnLoad]
public static class BotPlayTest
{
    const string Flag = "Temp/bot_test.flag", ChgFlag = "Temp/chg_test.flag", Report = "Temp/bot_test.txt";
    static IEnumerator run;
    static StringBuilder log = new StringBuilder();

    static BotPlayTest()
    {
        EditorApplication.playModeStateChanged += OnState;
        bool chg = File.Exists(ChgFlag);
        if ((chg || File.Exists(Flag)) && !EditorApplication.isPlayingOrWillChangePlaymode)
        {
            File.Delete(chg ? ChgFlag : Flag);
            SessionState.SetBool("bottest", true);
            SessionState.SetBool("chgtest", chg);
            EditorApplication.delayCall += () =>
            {
                CombatTestSetup.Ensure();
                EditorSceneManager.SaveOpenScenes();
                EditorApplication.EnterPlaymode();
            };
        }
    }

    static void OnState(PlayModeStateChange s)
    {
        if (s == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool("bottest", false))
        {
            SessionState.SetBool("bottest", false);
            log.Clear();
            run = SessionState.GetBool("chgtest", false) ? MainChg() : Main();
            EditorApplication.update += Tick;
        }
    }

    static void Tick()
    {
        try { if (run == null || !Step(run)) Finish(); }
        catch (System.Exception e) { L("EXCEPTION " + e); Finish(); }
    }

    static bool Step(IEnumerator it)
    {
        if (it.Current is IEnumerator inner) { if (Step(inner)) return true; }
        return it.MoveNext();
    }

    static void Finish()
    {
        EditorApplication.update -= Tick;
        File.WriteAllText(Report, log.ToString());
        run = null;
        EditorApplication.isPlaying = false;
    }

    static void L(string s) { log.AppendLine(s); }

    static IEnumerator Wait(float sec) { float t0 = Time.time; while (Time.time - t0 < sec) yield return null; }

    static HunterPlayer hunter;
    // Обычный агент (0): без фильтра запросы могли вернуть точку на навмеше полок HiderSmall (охотник ставился в стеллаж и падал сквозь пол)
    static NavMeshQueryFilter HumanoidFilter => new NavMeshQueryFilter { agentTypeID = 0, areaMask = NavMesh.AllAreas };

    static void Teleport(HiderPlayer h, Vector3 p)
    {
        var cc = h.GetComponent<CharacterController>();
        cc.enabled = false; h.transform.position = p; cc.enabled = true;
    }

    static void PlaceHunter(Vector3 foot, Vector3 lookAt)
    {
        var cc = hunter.GetComponent<CharacterController>();
        cc.enabled = false; hunter.transform.position = foot; cc.enabled = true;
        hunter.AimAt(lookAt);
    }

    // Прямая видимость центра бота из точки: статика блокирует, собственные коллайдеры бота (CharacterController на слое Default) — нет.
    static bool Visible(Vector3 from, HiderPlayer b)
    {
        Vector3 c = Center(b);
        Vector3 d = c - from;
        var hits = Physics.RaycastAll(from, d.normalized, d.magnitude, RamKickAuthority.StaticMask, QueryTriggerInteraction.Ignore);
        foreach (var h in hits)
            if (h.collider.GetComponentInParent<HiderPlayer>() != b) return false;
        return true;
    }

    static Vector3 Center(HiderPlayer b) => b.BodyCenter;
    static Vector3 HunterEye(Vector3 foot) => foot + Vector3.up * 1.7f;

    // Точка на навмеше на расстоянии min..max от бота, откуда охотник видит центр бота (по статике).
    static bool FindView(HiderPlayer b, float min, float max, out Vector3 foot, int salt = 0)
    {
        foot = default;
        Vector3 c = Center(b);
        for (float r = min; r <= max; r += 0.5f)
            for (int k = 0; k < 48; k++)
            {
                float a = ((k + salt * 5) % 48) * Mathf.PI * 2f / 48f;
                Vector3 want = new Vector3(b.transform.position.x + Mathf.Cos(a) * r, 0.1f, b.transform.position.z + Mathf.Sin(a) * r);
                if (!NavMesh.SamplePosition(want, out var nh, 0.8f, HumanoidFilter)) continue;
                if (!Visible(HunterEye(nh.position), b)) continue;
                foot = nh.position; return true;
            }
        return false;
    }

    // Точка обзора для охотника; если у бота на полке её нет (виден только в упор), бот переносится на пол в проход — это для теста, в игре бот сам этого не делает.
    static bool EnsureView(HiderPlayer b, float min, float max, out Vector3 foot)
    {
        if (FindView(b, min, max, out foot)) return true;
        if (FindView(b, 1.0f, 12f, out foot)) return true;
        for (int k = 0; k < 80; k++)
        {
            var rnd = new Vector3(Random.Range(-8f, 8f), 0.1f, Random.Range(-4.5f, 4.5f));
            if (!NavMesh.SamplePosition(rnd, out var nh, 1f, HumanoidFilter)) continue;
            Teleport(b, nh.position);
            if (FindView(b, min, max, out foot)) { L($"   (бот {b.name} перенесён на пол для теста)"); return true; }
        }
        return false;
    }

    static float DistToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float t = ab.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude);
        return Vector3.Distance(p, a + ab * t);
    }

    // Куда целиться, чтобы выстрел прошёл рядом с ботом и не попал: ищем смещение 1 м, где луч доходит до глубины бота (первое попадание дальше него).
    static bool NearMissAim(HiderPlayer b, Vector3 eye, out Vector3 aim)
    {
        aim = default;
        Vector3 c = Center(b);
        Vector3 to = (c - eye).normalized;
        Vector3 side = Vector3.Cross(Vector3.up, to).normalized;
        foreach (var off in new[] { side, -side, Vector3.up, -Vector3.up })
        {
            Vector3 a = c + off * 1.0f;
            Vector3 dir = (a - eye).normalized;
            var hits = Physics.RaycastAll(eye, dir, 14f, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance).ToArray();
            Vector3 end = eye + dir * 14f; bool bad = false;
            foreach (var h in hits)
            {
                if (h.collider.GetComponentInParent<HunterPlayer>() == hunter) continue;
                if (h.collider.GetComponentInParent<HiderPlayer>() != null) bad = true;
                end = h.point; break;
            }
            if (bad) continue;
            float d = DistToSegment(c, eye, end);
            if (d > 0.6f && d < 1.3f) { aim = a; return true; }
        }
        return false;
    }

    static void SetField(object o, string name, object v) { o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).SetValue(o, v); }
    static void Morph(HiderPlayer h, Prop sample) { typeof(HiderPlayer).GetMethod("Morph", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(h, new object[] { sample }); }

    class Timeline { public List<string> lines = new List<string>(); public float t0; public HiderBot.State last; public float maxMoved; public Vector3 start; public bool hunterKnocked; public float lastTrace; public int weaveFlips; public float maxJump; float lastSign; public void Flip(float x) { float sg = Mathf.Abs(x) < 0.05f ? lastSign : Mathf.Sign(x); if (sg != 0 && lastSign != 0 && sg != lastSign) weaveFlips++; lastSign = sg; } }

    // Следит за ботом: переходы состояний с временем от t0, максимум удаления от старта, был ли охотник сбит.
    static IEnumerator Track(HiderBot b, Timeline tl, float maxSec, float minAfterReact, string shot = null, float shotAt = 0.5f)
    {
        bool shotDone = shot == null;
        tl.t0 = Time.time; tl.last = b.Current; tl.start = b.Hider.transform.position;
        bool reacted = b.Current == HiderBot.State.Reacting; float reactT = Time.time;
        while (Time.time - tl.t0 < maxSec && b != null && b.Hider != null)
        {
            if (b.Current != tl.last)
            {
                tl.lines.Add($"   +{Time.time - tl.t0:F2} с: {tl.last} -> {b.Current}" + (tl.last == HiderBot.State.Flee ? $"   [конец побега: {Where(b.Hider)}, safeDistance={b.personality.safeDistance}]" : ""));
                if (tl.last == HiderBot.State.Flee) tl.lines.Add($"      (невидим подряд к концу побега: {b.FleeEndUnseen:F2} с, сбросов счётчика за прогон: {b.UnseenResets})");
                tl.last = b.Current;
                if (b.Current == HiderBot.State.Reacting && !reacted) { reacted = true; reactT = Time.time; }
            }
            if (b.Current == HiderBot.State.Flee) { tl.Flip(b.Hider.botInput.move.x); tl.maxJump = Mathf.Max(tl.maxJump, b.Hider.transform.position.y - tl.start.y); }
            tl.maxMoved = Mathf.Max(tl.maxMoved, Vector3.Distance(tl.start, b.Hider.transform.position));
            if (Time.time - tl.lastTrace >= 1f) { tl.lastTrace = Time.time; var q = b.Hider.transform.position; tl.lines.Add($"      [{Time.time - tl.t0:F1} с] ({q.x:F1};{q.y:F2};{q.z:F1}) {b.Current}"); }
            if (hunter.Knocked) tl.hunterKnocked = true;
            if (!shotDone && Time.time - tl.t0 >= shotAt) { shotDone = true; ScreenCapture.CaptureScreenshot($"Assets/Screenshots/{shot}.png"); }
            if (reacted && b.Current == HiderBot.State.Freeze && Time.time - reactT > minAfterReact) break;
            yield return null;
        }
    }

    static string Where(HiderPlayer b)
    {
        Vector3 c = Center(b);
        bool seen = Visible(HunterEye(hunter.transform.position), b);
        return $"pos=({b.transform.position.x:F1};{b.transform.position.y:F2};{b.transform.position.z:F1}) охотник виден={seen} дист={Vector3.Distance(hunter.transform.position, b.transform.position):F1} м";
    }

    static IEnumerator Main()
    {
        yield return Wait(1f);
        var rs = RoundState.Instance;
        hunter = HunterPlayer.All[0];
        hunter.SetControlled(false);
        var bots = HiderBot.All.OrderBy(b => b.name).ToList();
        L($"ботов: {bots.Count}; охотник={hunter != null}; навмеш: триангуляция вершин={NavMesh.CalculateTriangulation().vertices.Length}");
        foreach (var b in bots) L($"  {b.name}: личность={b.personality.name} реакция={b.personality.reactionDelay} с, безопасная дистанция={b.personality.safeDistance} м, шанс тарана={b.personality.ramChance}, перемещение каждые {b.personality.relocateEvery} с, видит охотника до {b.personality.noticeRange} м");

        // ---------- карта навмеша: '#' пол, связный со входом; 'o' пол, несвязный; '^' только выше пола; '.' нет ----------
        {
            NavMesh.SamplePosition(new Vector3(0f, 0.1f, -5f), out var entry, 2f, HumanoidFilter);
            var path = new NavMeshPath();
            var sbn = new StringBuilder();
            sbn.AppendLine($"навмеш: вход {entry.position}");
            for (float z = 7f; z >= -7.01f; z -= 0.5f)
            {
                sbn.Append($"{z,5:F1} ");
                for (float x = -10f; x <= 10.01f; x += 0.5f)
                {
                    char ch = '.';
                    if (NavMesh.SamplePosition(new Vector3(x, 0.05f, z), out var f, 0.2f, HumanoidFilter) && f.position.y < 0.3f)
                        ch = NavMesh.CalculatePath(entry.position, f.position, HumanoidFilter, path) && path.status == NavMeshPathStatus.PathComplete ? '#' : 'o';
                    else if (NavMesh.SamplePosition(new Vector3(x, 1.0f, z), out var u, 1.0f, HumanoidFilter) && u.position.y > 0.3f && Mathf.Abs(u.position.x - x) < 0.25f && Mathf.Abs(u.position.z - z) < 0.25f) ch = '^';
                    sbn.Append(ch);
                }
                sbn.AppendLine();
            }
            L(sbn.ToString());
        }

        // ---------- T1: выбор предмета в подготовке ----------
        float t0 = Time.time;
        float lastTr = -1f;
        while (Time.time - t0 < 25f && rs.Phase == RoundPhase.Prep && bots.Any(b => b.Hider.CurrentProp == null))
        {
            if (Time.time - lastTr >= 0.5f && bots[1].Hider.CurrentProp == null) { lastTr = Time.time; var q = bots[1].Hider.transform.position; L($"   seek-trace Bot_2 t={Time.time - t0:F1}: ({q.x:F2};{q.y:F2};{q.z:F2}) {bots[1].Current} dest={bots[1].Dest} corner={(bots[1].PathCorners.Length > 0 ? bots[1].PathCorners[bots[1].PathCorners.Length - 1].ToString() : "-")}"); }
            yield return null;
        }
        L($"T1 за {Time.time - t0:F1} с (фаза {rs.Phase}): без предмета осталось {bots.Count(b => b.Hider.CurrentProp == null)}");
        var allProps = Prop.All.ToList();
        float avgNat = allProps.Average(p => HiderBot.Naturalness(p)), maxNat = allProps.Max(p => HiderBot.Naturalness(p));
        L($"  естественность по всем предметам сцены: среднее {avgNat:F2}, максимум {maxNat:F2}");
        foreach (var b in bots)
        {
            var p = b.Hider.CurrentProp;
            if (p == null) { L($"  {b.name}: предмета нет, состояние {b.Current}"); continue; }
            L($"  {b.name}: {p.ModelId} ({p.tier}), естественность {HiderBot.Naturalness(p):F2}, y={b.Hider.transform.position.y:F2}, состояние {b.Current}");
        }
        if (rs.Phase == RoundPhase.Prep) rs.EndPrep();
        yield return Wait(1f);
        L($"  после конца подготовки: {string.Join(", ", bots.Select(b => $"{b.name}={b.Current}"))}");
        // боты на полке/на полу
        foreach (var b in bots) { L($"  {b.name}: первое замирание на {b.FirstFreezeAt - 1f:F1} с"); foreach (var h in b.History) L("      " + h); }

        DebugRoleSwitch.Swap();   // камера у охотника: скриншоты его глазами
        yield return Wait(0.3f);

        // ---------- T1b: выбор предмета меняется от раунда к раунду ----------
        {
            var picks = new List<string>();
            for (int r = 0; r < 8; r++)
            {
                HiderBot.NewRound();
                picks.Add(string.Join(" / ", bots.Select(bb => { var c = bb.ScoreCandidates(); return c.Count > 0 ? $"{c[0].p.ModelId}@({c[0].p.transform.position.x:F1};{c[0].p.transform.position.z:F1})" : "-"; })));
            }
            for (int r = 0; r < picks.Count; r++) L($"T1b раунд {r + 1}: топ-1 кандидат по ботам: {picks[r]}");
            L($"T1b уникальных наборов из {picks.Count}: {picks.Distinct().Count()}");
            var sep = new[] { " / " };
            for (int bi = 0; bi < bots.Count; bi++) { int bi2 = bi; int uniq = picks.Select(x => x.Split(sep, System.StringSplitOptions.None)[bi2]).Distinct().Count(); L($"T1b {bots[bi].name}: уникальных топ-1 предметов за 8 раундов: {uniq}"); }
        }

        // ---------- T2: простое приближение охотника НЕ вызывает побега ----------
        foreach (var b in bots.Take(2))
        {
            Vector3 p0 = b.Hider.transform.position;
            float detectedBefore = b.DetectedAt;
            if (!EnsureView(b.Hider, 6f, 12f, out var far) || !FindView(b.Hider, 1.5f, 3f, out var near)) { L($"T2 {b.name}: нет точки обзора"); continue; }
            int ticks0 = b.TickCount;
            for (float u = 0; u < 4f; u += Time.deltaTime)   // охотник идёт с 8-12 м вплотную за 4 с, смотрит на бота
            {
                Vector3 pos = Vector3.Lerp(far, near, u / 4f);
                PlaceHunter(pos, Center(b.Hider));
                yield return null;
            }
            PlaceHunter(near, Center(b.Hider));
            yield return Wait(5.5f);
            if (b == bots[0]) { ScreenCapture.CaptureScreenshot("Assets/Screenshots/bot_freeze_close.png"); yield return Wait(0.5f); } else yield return Wait(0.5f);
            float moved = Vector3.Distance(p0, b.Hider.transform.position);
            L($"T2 {b.name} ({b.personality.name}): охотник подошёл на {Vector3.Distance(near, p0):F1} м и стоял 6 с -> состояние {b.Current}, сдвиг бота {moved * 1000f:F0} мм, обнаружение не фиксировалось={b.DetectedAt == detectedBefore} (ожидаем Freeze, 0 мм, True)");
        }

        // ---------- T3: near-miss, осторожный бот (bots[0]) ----------
        yield return RunDetect("T3 near-miss", bots[0], false, 3f, 7f, "bot_flee_nearmiss");
        // Tiny (HP 20) гибнет с одного выстрела (урон 15..50), бежать некому; для проверки побега после попадания переводим ботов в Small (Box, HP 100)
        foreach (var bb in new[] { bots[1], bots[2 % bots.Count] })
        {
            var box = Prop.All.FirstOrDefault(p => p.name == "Box" && p.IsFree);
            if (box != null && bb != null && bb.Hider != null && !bb.Hider.Caught) { Morph(bb.Hider, box); L($"   ({bb.name} переведён в Box для теста попадания)"); }
        }
        // ---------- T4: попадание, наглый бот (bots[1]) ----------
        yield return RunDetect("T4 попадание", bots[1], true, 3f, 7f);
        // ---------- T4b: попадание, осторожный (для сравнения личностей; вне кулдауна охотника) ----------
        yield return RunDetect("T4b попадание", bots[2 % bots.Count], true, 3f, 7f);
        yield return RunDetect("T3b near-miss", bots[3 % bots.Count], false, 3f, 7f);

        // ---------- T5: таран ----------
        {
            var b = bots[1];
            var machine = Prop.All.FirstOrDefault(p => p.name == "Machine" && p.IsFree);
            if (machine == null) L("T5: нет свободного Machine");
            else
            {
                b.personality.ramChance = 1f;
                yield return Wait(Mathf.Max(0f, b.Hider.NextRepossessTime - Time.time) + 0.2f);
                { float tb = Time.time; while (b.Hider.Boost != HiderPlayer.BoostPhase.Ready && Time.time - tb < 25f) yield return null; L($"   (T5: Z у бота готов через {Time.time - tb:F1} с)"); }
                Morph(b.Hider, machine);
                // ставим бота и охотника на пол в проходе, охотник виден за 4 м
                Vector3 spot = default; bool ok = false;
                for (int k = 0; k < 60 && !ok; k++)
                {
                    var rnd = new Vector3(Random.Range(-7f, 7f), 0.1f, Random.Range(-4f, 4f));
                    if (NavMesh.SamplePosition(rnd, out var nh, 1f, HumanoidFilter)) { spot = nh.position; ok = true; }
                }
                Teleport(b.Hider, spot);
                yield return Wait(0.3f);
                bool placed = EnsureView(b.Hider, 3.5f, 5f, out var hp);
                if (!placed) L("T5: нет точки обзора");
                else
                {
                    Vector3 aim = default; bool haveAim = false;
                    for (int salt = 0; salt < 8 && !haveAim; salt++)
                    {
                        if (!FindView(b.Hider, 3.5f, 5f, out hp, salt)) continue;
                        PlaceHunter(hp, Center(b.Hider));
                        haveAim = NearMissAim(b.Hider, hunter.EyePosition, out aim);
                    }
                    if (!haveAim) { L("T5: нет прицела near-miss"); yield break; }
                    hunter.NextShotTime = 0f;
                    int rams0 = b.RamCount;
                    hunter.AimAt(aim);
                    yield return null;
                    float hpBefore = b.Hider.Hp;
                    hunter.Fire();
                    var tl = new Timeline();
                    yield return Track(b, tl, 12f, 1f);
                    L($"T5 таран: бот {b.Hider.CurrentProp.ModelId} ({b.Hider.CurrentProp.tier}), HP {hpBefore}->{b.Hider.Hp}, тарана совершено={b.RamCount - rams0}, охотник был сбит={tl.hunterKnocked}");
                    foreach (var line in tl.lines) L(line);
                }
                b.personality.ramChance = 0.7f;
            }
        }
        yield return Wait(4f);

        // ---------- T6: частота тиков: вдали ~0,3 с, вблизи каждый кадр ----------
        {
            var b = bots[2 % bots.Count];
            yield return Wait(1f);
            Vector3 farFoot = default; float bestD = 0f;
            for (float x = -9f; x <= 9f; x += 1.5f) for (float z = -6f; z <= 6f; z += 1.5f)
                    if (NavMesh.SamplePosition(new Vector3(x, 0.1f, z), out var nh, 0.5f, HumanoidFilter) && Vector3.Distance(nh.position, b.Hider.transform.position) > bestD)
                    { bestD = Vector3.Distance(nh.position, b.Hider.transform.position); farFoot = nh.position; }
            PlaceHunter(farFoot, Center(b.Hider));
            yield return Wait(0.5f);
            int tk0 = b.TickCount; float tt0 = Time.time;
            yield return Wait(3f);
            float farRate = (b.TickCount - tk0) / (Time.time - tt0);
            if (EnsureView(b.Hider, 4f, 6f, out var nearFoot)) PlaceHunter(nearFoot, Center(b.Hider));
            yield return Wait(0.5f);
            tk0 = b.TickCount; tt0 = Time.time; int fr0 = Time.frameCount;
            yield return Wait(3f);
            float nearRate = (b.TickCount - tk0) / (Time.time - tt0);
            float fps = (Time.frameCount - fr0) / (Time.time - tt0);
            L($"T6 {b.name}: охотник в {bestD:F1} м -> {farRate:F1} тиков/с (ожидаем ~3,3); охотник в {Vector3.Distance(nearFoot, b.Hider.transform.position):F1} м -> {nearRate:F1} тиков/с при {fps:F0} кадр/с; состояние {b.Current} (охотник вплотную в прямой видимости 3 с, побега нет)");
        }

        // ---------- T7: естественное перемещение ----------
        {
            // охотник далеко и без прямой видимости на первого бота
            Vector3 hid = new Vector3(9f, 0.1f, 5f); float bestHid = -1f;
            for (float x = -9f; x <= 9f; x += 1f) for (float z = -6f; z <= 6f; z += 1f)
                    if (NavMesh.SamplePosition(new Vector3(x, 0.1f, z), out var nh2, 0.5f, HumanoidFilter))
                        foreach (var bb in bots)
                            if (!Visible(HunterEye(nh2.position), bb.Hider) && Vector3.Distance(nh2.position, bb.Hider.transform.position) > bestHid) { bestHid = Vector3.Distance(nh2.position, bb.Hider.transform.position); hid = nh2.position; }
            PlaceHunter(hid, hid + Vector3.forward);
            foreach (var b in bots)
            {
                yield return Wait(0.2f);
                if (b == null || b.Hider == null) continue;
                var cur = b.Hider.CurrentProp;
                string before = cur != null ? cur.ModelId : "—";
                Vector3 p0 = b.Hider.transform.position;
                yield return Wait(Mathf.Max(0f, b.Hider.NextRepossessTime - Time.time) + 0.3f);
                bool started = b.Current == HiderBot.State.Freeze && b.DebugRelocateNow();
                if (!started) { L($"T7 {b.name}: перемещение не запущено (состояние {b.Current}, y={b.Hider.transform.position.y:F2})"); continue; }
                float t1 = Time.time; float maxMoved = 0f;
                while (Time.time - t1 < 25f && b.Current != HiderBot.State.Freeze) { maxMoved = Mathf.Max(maxMoved, Vector3.Distance(p0, b.Hider.transform.position)); yield return null; }
                yield return Wait(4f);
                L($"T7 {b.name}: перемещение {Time.time - t1 - 4f:F1} с, уход {maxMoved:F1} м, итог {Vector3.Distance(p0, b.Hider.transform.position):F1} м от старта; облик {before} -> {b.Hider.CurrentProp.ModelId}; состояние {b.Current}");
                break;
            }
            // автоматическое перемещение по таймеру личности
            foreach (var b in bots)
            {
                if (b.Hider.Caught) continue;
                if (b.Current != HiderBot.State.Freeze) continue;
                SetField(b, "nextRelocateAt", Time.time + 0.5f);
                Vector3 p0 = b.Hider.transform.position;
                float maxMoved = 0f; bool onFloor = false;
                for (float u = 0; u < 20f; u += Time.deltaTime) { maxMoved = Mathf.Max(maxMoved, Vector3.Distance(p0, b.Hider.transform.position)); yield return null; if (b.Current == HiderBot.State.Relocate) onFloor = true; }
                L($"T7b {b.name}: по таймеру личности ушёл на {maxMoved:F1} м за 20 с (Relocate наблюдался={onFloor}; если бот на полке, не двигается: y={p0.y:F2})");
                break;
            }
        }

        // ---------- итог ----------
        yield return Wait(1f);
        L("итог: " + string.Join(" | ", bots.Select(b => $"{b.name}: {(b.Hider.Caught ? "пойман" : b.Current.ToString())}, HP {b.Hider.Hp}/{b.Hider.MaxHp}")));
    }

    // Тест правок: устойчивая невидимость, виляние, камера охотника при таране, F4 -> NewRound.
    static IEnumerator MainChg()
    {
        yield return Wait(1f);
        hunter = HunterPlayer.All[0];
        hunter.SetControlled(false);
        var bots = HiderBot.All.OrderBy(b => b.name).ToList();
        yield return Wait(4f);
        if (RoundState.Instance != null && RoundState.Instance.Phase == RoundPhase.Prep) RoundState.Instance.EndPrep();
        yield return Wait(1f);
        L($"веса виляния: amplitude={bots[0].weaveAmplitude}, period={bots[0].weavePeriod}, удержание невидимости={bots[0].fleeUnseenHold} с");

        // ---------- A/B: побег (near-miss), невидимость подряд и виляние ----------
        yield return RunDetect("A near-miss (осторожный)", bots[0], false, 3f, 7f);
        yield return RunDetect("B near-miss (наглый)", bots[1], false, 3f, 7f);

        // ---------- D: F4 настоящим событием клавиши ----------
        {
            var tops0 = string.Join(", ", bots.Select(b => { var c = b.ScoreCandidates(); return c.Count > 0 ? c.OrderByDescending(x => x.score).First().p.ModelId + "#" + c.OrderByDescending(x => x.score).First().p.GetInstanceID() : "-"; }));
            int salt0 = HiderBot.RoundSalt;
            UnityEngine.InputSystem.InputSystem.settings.backgroundBehavior = UnityEngine.InputSystem.InputSettings.BackgroundBehavior.IgnoreFocus;
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (Object.FindFirstObjectByType<DebugRoleSwitch>() == null) { L("D: нет DebugRoleSwitch в сцене"); yield break; }
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(kb, new UnityEngine.InputSystem.LowLevel.KeyboardState(UnityEngine.InputSystem.Key.F4));
            yield return null; yield return null;
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(kb, new UnityEngine.InputSystem.LowLevel.KeyboardState());
            yield return null;
            var tops1 = string.Join(", ", bots.Select(b => { var c = b.ScoreCandidates(); return c.Count > 0 ? c.OrderByDescending(x => x.score).First().p.ModelId + "#" + c.OrderByDescending(x => x.score).First().p.GetInstanceID() : "-"; }));
            L($"D F4: RoundSalt {salt0} -> {HiderBot.RoundSalt} (изменился={salt0 != HiderBot.RoundSalt}); топ-1 до: [{tops0}], после: [{tops1}]");
        }

        // ---------- C: камера охотника при таране ----------
        {
            var cam = hunter.cam;
            DebugRoleSwitch.Swap();   // управление охотнику, камера его
            Vector3 foot = hunter.transform.position;
            yield return Wait(0.3f);
            Vector3 eye = hunter.EyePosition;
            L($"C до тарана: камера от глаз {Vector3.Distance(cam.transform.position, eye):F2} м, Knocked={hunter.Knocked}");
            hunter.Knock(hunter.transform.forward * -6f + Vector3.up * 3f, Vector3.right * 4f, RamKickAuthority.Instance);
            float t0 = Time.time; bool shot = false; float maxD = 0f, minD = 99f; bool wasKnocked = true; float tEnd = -1f; float lastD = 0f; int framesTP = 0, framesAll = 0;
            while (Time.time - t0 < 12f)
            {
                if (hunter.Knocked)
                {
                    framesAll++;
                    var rg = hunter.GetComponentInChildren<HunterRagdoll>(true) ?? Object.FindFirstObjectByType<HunterRagdoll>();
                    if (rg != null && rg.TryGetBodyCenter(out var ctr))
                    {
                        float d = Vector3.Distance(cam.transform.position, ctr); maxD = Mathf.Max(maxD, d); minD = Mathf.Min(minD, d);
                        if (d > 0.8f) framesTP++;
                    }
                    if (!shot && Time.time - t0 > 1.2f) { shot = true; ScreenCapture.CaptureScreenshot("Assets/Screenshots/ram_hunter_thirdperson.png"); }
                }
                else if (wasKnocked) { wasKnocked = false; tEnd = Time.time; }
                if (!wasKnocked && Time.time - tEnd > 0.8f) break;
                yield return null;
            }
            lastD = Vector3.Distance(cam.transform.position, hunter.EyePosition);
            L($"C ragdoll: кадров с Knocked={framesAll}, из них камера дальше 0,8 м от тела={framesTP}, расстояние до центра тела {minD:F2}..{maxD:F2} м; подъём окончен через {(tEnd - t0):F1} с; 0,8 с после — камера от глаз {lastD:F3} м, Knocked={hunter.Knocked}");
            DebugRoleSwitch.Swap();
        }
    }

    // Запуск из MCP в уже идущем Play: BotPlayTest.StartScenario("chase").
    public static void StartScenario(string name)
    {
        log.Clear();
        run = name == "chase" ? MainChase() : name == "shelf" ? MainShelf() : name == "weave" ? MainWeave() : name == "all" ? MainAll() : name == "wc" ? MainWC() : name == "air" ? MainAir() : name == "group" ? MainGroup() : name == "ram" ? MainMorphRam() : name == "new" ? MainNew() : name == "rg" ? MainRG() : null;
        if (run != null) EditorApplication.update += Tick;
    }

    static IEnumerator MainAll()
    {
        yield return MainShelf();
        L("==========");
        yield return MainWeave();
        L("==========");
        yield return MainChase();
        L("==========");
        yield return MainAir();
    }

    static IEnumerator MainWC()
    {
        yield return MainWeave();
        L("==========");
        yield return MainChase();
    }

    // Виляние на земле и в воздухе: побег по длинному прямому коридору перед кассами (z=-3, x -8,5 -> 8,5), охотник видит бота сзади.
    // Покадрово: боковое смещение от оси коридора, земля/воздух; картинка следа сверху (красный — земля, синий — воздух).
    static IEnumerator MainAir()
    {
        yield return Ready();
        var bots = HiderBot.All.OrderBy(b => b.name).ToList();
        float speed0 = hunter.walkSpeed;
        int run = 0;
        foreach (var (model, b, fps) in new[] { ("Box", bots[0], -1), ("Box", bots[0], 60), ("Bin", bots[1], 60) })
        {
            run++;
            Application.targetFrameRate = fps; QualitySettings.vSyncCount = 0;
            var sample = FreeModel(model);
            if (sample != null) Morph(b.Hider, sample);
            NavMesh.SamplePosition(new Vector3(-8.5f, 0.05f, -3.0f), out var st, 1f, HumanoidFilter);
            Teleport(b.Hider, st.position);
            PlaceHunter(new Vector3(-9.6f, 0.05f, -3.0f), Center(b.Hider));
            hunter.walkSpeed = 3.2f;
            yield return Wait(0.6f);
            NavMesh.SamplePosition(new Vector3(8.5f, 0.05f, -3.0f), out var en, 1f, HumanoidFilter);
            float ram0 = b.personality.ramChance; b.personality.ramChance = 0f;
            int ej0 = b.EvadeJumps;
            bool ok = b.DebugFleeTo(en.position);
            var pts = new List<(Vector3 p, bool air, float off)>();
            float t0 = Time.time; int lastF = -1, airFrames = 0, frames = 0;
            float airOffMin = 99f, airOffMax = -99f;
            var lat = new StringBuilder();
            float lastLat = -1f;
            while (ok && Time.time - t0 < 9f && b.Current == HiderBot.State.Flee && b.Hider.transform.position.x < 8f)
            {
                if (Time.frameCount != lastF)
                {
                    lastF = Time.frameCount; frames++;
                    ChaseStep(b.Hider);
                    var q = b.Hider.transform.position;
                    bool air = !b.Hider.Grounded;
                    if (air) { airFrames++; airOffMin = Mathf.Min(airOffMin, b.WeaveOffset); airOffMax = Mathf.Max(airOffMax, b.WeaveOffset); }
                    pts.Add((q, air, b.WeaveOffset));
                    if (Time.time - lastLat >= 0.1f) { lastLat = Time.time; lat.Append($"{q.z + 3f:+0.00;-0.00}{(air ? "*" : "")} "); }
                }
                yield return null;
            }
            hunter.walkSpeed = speed0; b.personality.ramChance = ram0;
            // развороты бокового движения (по z) по точкам через 10 см пути
            var kept = new List<(Vector3 p, float t)>(); int idx = 0;
            foreach (var pt in pts) { if (kept.Count == 0 || Vector3.Distance(Flat2(pt.p), Flat2(kept[kept.Count - 1].p)) >= 0.1f) kept.Add((pt.p, idx)); idx++; }
            int rev = 0; float lastSign = 0f;
            for (int i = 1; i < kept.Count; i++) { float dz = kept[i].p.z - kept[i - 1].p.z; if (Mathf.Abs(dz) < 0.004f) continue; float sg = Mathf.Sign(dz); if (lastSign != 0f && sg != lastSign) rev++; lastSign = sg; }
            float dist = pts.Count > 1 ? pts[pts.Count - 1].p.x - pts[0].p.x : 0f;
            float zMin = pts.Count > 0 ? pts.Min(x => x.p.z) + 3f : 0f, zMax = pts.Count > 0 ? pts.Max(x => x.p.z) + 3f : 0f;
            L($"air #{run} [{model}, {(fps < 0 ? "без ограничения" : fps + " кадр/с")}] {b.name}: пробежал {dist:F1} м за {Time.time - t0:F1} с; кадров {frames}, в воздухе {airFrames}; прыжков уклонения {b.EvadeJumps - ej0}; " +
              $"боковое отклонение от оси {zMin:+0.00;-0.00}..{zMax:+0.00;-0.00} м; разворотов бокового движения {rev} (≈ {(dist > 0 ? rev / (dist / 4f) : 0):F2} на период при 4 м/с); смещение цели в воздухе {airOffMin:F2}..{airOffMax:F2}; итог {b.Current}");
            L($"   боковое смещение каждые 0,1 с (* = в воздухе): {lat}");
            if (run == 2 && pts.Count > 2) yield return TrailShot(pts, "bot_weave_trail");
            yield return Wait(0.5f);
        }
        Application.targetFrameRate = -1;
    }

    static Vector3 Flat2(Vector3 v) { v.y = 0f; return v; }

    // Картинка следа сверху: ортокамера под потолком, рендер в текстуру -> PNG (без окна Game).
    static IEnumerator TrailShot(List<(Vector3 p, bool air, float off)> pts, string file)
    {
        var root = new GameObject("TrailViz");
        var mat = new Material(Shader.Find("Sprites/Default"));
        int i = 0;
        while (i < pts.Count - 1)
        {
            bool air = pts[i].air; var seg = new List<Vector3>();
            while (i < pts.Count && pts[i].air == air) { seg.Add(pts[i].p + Vector3.up * 0.05f); i++; }
            if (i < pts.Count) seg.Add(pts[i].p + Vector3.up * 0.05f);
            var go = new GameObject("seg"); go.transform.SetParent(root.transform);
            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = mat; lr.widthMultiplier = 0.07f; lr.positionCount = seg.Count; lr.SetPositions(seg.ToArray());
            lr.startColor = lr.endColor = air ? new Color(0.2f, 0.45f, 1f) : new Color(1f, 0.15f, 0.1f);
            lr.alignment = LineAlignment.View;
        }
        var camGo = new GameObject("TrailCam");
        var cam = camGo.AddComponent<Camera>();
        cam.orthographic = true; cam.orthographicSize = 2.6f; cam.aspect = 4f;
        cam.transform.SetPositionAndRotation(new Vector3(0f, 3.3f, -3.0f), Quaternion.Euler(90f, 0f, 0f));
        cam.nearClipPlane = 0.05f; cam.farClipPlane = 5f;
        var rt = new RenderTexture(2000, 500, 24);
        cam.targetTexture = rt; cam.enabled = false;
        yield return null;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); tex.Apply();
        RenderTexture.active = null;
        File.WriteAllBytes($"Assets/Screenshots/{file}.png", tex.EncodeToPNG());
        cam.targetTexture = null;
        Object.Destroy(rt); Object.Destroy(camGo); Object.Destroy(root); Object.Destroy(tex);
        L($"   след сверху: Assets/Screenshots/{file}.png (красный — земля, синий — воздух)");
    }

    // Групповой побег мелких: все боты Snack, одновременное обнаружение, охотник идёт за первым; через 25 с — где кто затаился.
    // Ищем: боты друг на друге, затаился на виду на голом полу, неудачные переходы на полку.
    static IEnumerator MainGroup()
    {
        yield return Ready();
        var bots = HiderBot.All.OrderBy(b => b.name).ToList();
        for (int round = 1; round <= 2; round++)
        {
            int k = 0;
            foreach (var b in bots)
            {
                var sample = FreeModel("Snack");
                if (sample != null) Morph(b.Hider, sample);
                var p = new Vector3(-2.5f + k * 0.9f, 0.05f, -3.1f); k++;
                if (NavMesh.SamplePosition(p, out var nh, 1f, HumanoidFilter)) Teleport(b.Hider, nh.position);
                b.Diag = null;
            }
            PlaceHunter(new Vector3(-6f, 0.05f, -3.1f), bots[0].Hider.BodyCenter);
            yield return Wait(0.5f);
            var f0 = bots.Select(b => b.ShelfJumpsFail).ToList();
            var o0 = bots.Select(b => b.ShelfJumpsOk).ToList();
            foreach (var b in bots) { b.Trail.Clear(); b.Hider.OnNearMiss(4f); }
            float t0 = Time.time; int lastF = -1;
            while (Time.time - t0 < 25f)
            {
                if (Time.frameCount != lastF && Time.time - t0 < 8f) { lastF = Time.frameCount; ChaseStep(bots[0].Hider); }
                yield return null;
            }
            L($"group раунд {round}: через 25 с");
            for (int i = 0; i < bots.Count; i++)
            {
                var b = bots[i]; var h = b.Hider; var q = h.transform.position;
                if (h.Caught) { L($"   {b.name}: пойман"); continue; }
                var onSlot = ShelfSlot.All.FirstOrDefault(sl => sl.Contains(q, 0.08f));
                var under = bots.Where(o => o != b && !o.Hider.Caught && Vector3.Distance(Flat2(o.Hider.transform.position), Flat2(q)) < h.BodyRadius + o.Hider.BodyRadius + 0.05f && q.y - o.Hider.transform.position.y > 0.05f).Select(o => o.name).ToList();
                int same = Prop.All.Count(pp => pp.ModelId == h.CurrentProp.ModelId && pp != h.CurrentProp && Vector3.Distance(pp.transform.position, q) < 1.5f);
                bool seen = Visible(HunterEye(hunter.transform.position), h);
                L($"   {b.name}: {b.Current}, плохое место={b.BadSpot()}, облик {h.CurrentProp.ModelId}, pos ({q.x:F2};{q.y:F2};{q.z:F2}), {(onSlot != null ? $"на полке {onSlot.transform.parent.name}/{onSlot.name}" : q.y < 0.1f ? "на полу" : "НЕ на полу и не на полке")}, стоит на боте: {(under.Count > 0 ? string.Join(",", under) : "нет")}, таких же предметов в 1,5 м: {same}, охотник видит: {seen}; переходов ок/неудач +{b.ShelfJumpsOk - o0[i]}/+{b.ShelfJumpsFail - f0[i]}; {b.LastLinkLog}");
                if (b.BadSpot() || under.Count > 0) L("      журнал: " + string.Join(" | ", b.Trail.Skip(Mathf.Max(0, b.Trail.Count - 8))));
            }
        }
    }

    static IEnumerator MainRG()
    {
        yield return MainMorphRam();
        L("==========");
        yield return MainGroup();
    }

    static IEnumerator MainNew()
    {
        yield return MainMorphRam();
        L("==========");
        yield return MainGroup();
        L("==========");
        yield return MainAir();
        L("==========");
        yield return MainChase();
        L("==========");
        yield return MainShelf();
    }

    // Превратиться в крупный и таранить: мелкий бот (Snack) на полу в 1,5 м от среднего/крупного предмета, охотник в 4-5 м на виду,
    // near-miss -> побег. Ждём: Morphing -> Ram, облик стал средним/крупным, охотник сбит.
    static IEnumerator MainMorphRam()
    {
        yield return Ready();
        var bots = HiderBot.All.OrderBy(b => b.name).ToList();
        int n = 0;
        foreach (var big in Prop.All.Where(pp => pp.IsFree && pp.tier >= PropTier.Medium && pp.transform.position.y - pp.height * 0.5f < 0.1f).Take(3).ToList())
        {
            var b = bots[n % bots.Count]; n++;
            if (b.Hider.Caught) continue;
            var snack = FreeModel("Snack");
            if (snack != null) Morph(b.Hider, snack);
            SetField(b.Hider, "<NextRepossessTime>k__BackingField", 0f);   // кулдаун смены готов (в бою он отсчитался бы сам)
            // бот рядом с крупным предметом, со стороны зала
            Vector3 bp = big.transform.position; Vector3 dirIn = Flat2(-bp).normalized;
            if (!NavMesh.SamplePosition(bp + dirIn * (big.footRadius + 0.9f), out var nh, 1f, HumanoidFilter)) { L($"ram: нет места у {big.ModelId}"); continue; }
            Teleport(b.Hider, nh.position);
            yield return Wait(0.3f);
            float tb = Time.time; while (b.Hider.Boost != HiderPlayer.BoostPhase.Ready && Time.time - tb < 20f) yield return null;
            if (!FindView(b.Hider, 3.5f, 5f, out var hp)) { L($"ram: нет точки обзора у {big.ModelId}"); continue; }
            PlaceHunter(hp, Center(b.Hider));
            hunter.KnockImmuneUntil = 0f;
            float mr0 = b.personality.morphRamChance; b.personality.morphRamChance = 1f;
            int m0 = b.MorphRams, r0 = b.RamCount;
            yield return Wait(0.3f);
            b.Hider.OnNearMiss(4f);
            var seq = new List<string> { b.Current.ToString() };
            float t0 = Time.time; bool knocked = false; string modelAtRam = "";
            while (Time.time - t0 < 6f)
            {
                if (seq[seq.Count - 1] != b.Current.ToString()) { seq.Add(b.Current.ToString()); if (b.Current == HiderBot.State.Ram) modelAtRam = $"{b.Hider.CurrentProp.ModelId} ({b.Hider.CurrentProp.tier})"; }
                if (hunter.Knocked) knocked = true;
                yield return null;
            }
            b.personality.morphRamChance = mr0;
            L($"ram у {big.ModelId} ({big.tier}) {b.name}: превращений ради тарана +{b.MorphRams - m0}, таранов +{b.RamCount - r0}, облик при таране: {modelAtRam}, охотник сбит: {knocked}; состояния {string.Join("->", seq)}");
            float tw = Time.time; while (hunter.Knocked && Time.time - tw < 8f) yield return null;
            yield return Wait(1f);
        }
    }

    static IEnumerator Ready()
    {
        yield return Wait(1f);
        hunter = HunterPlayer.All[0];
        hunter.SetControlled(false);
        float t0 = Time.time;
        while (Time.time - t0 < 20f && HiderBot.All.Any(b => b.Hider.CurrentProp == null)) yield return null;
        if (RoundState.Instance != null && RoundState.Instance.Phase == RoundPhase.Prep) RoundState.Instance.EndPrep();
        yield return Wait(0.5f);
    }

    // Охотник бежит за ботом по навмешу каждый кадр (скорость охотника), целясь в него.
    static int chaseFrame = -1;
    static void ChaseStep(HiderPlayer b)
    {
        // корутина теста идёт по EditorApplication.update, он может тикать чаще кадров игры: шаг охотника — раз в кадр
        if (Time.frameCount == chaseFrame) return;
        chaseFrame = Time.frameCount;
        var path = new NavMeshPath();
        Vector3 from = hunter.transform.position;
        Vector3 target = b.transform.position;
        if (NavMesh.SamplePosition(target, out var tn, 3f, HumanoidFilter)) target = tn.position;
        Vector3 dir;
        if (NavMesh.CalculatePath(from, target, HumanoidFilter, path) && path.corners.Length > 1) dir = path.corners[1] - from;
        else dir = target - from;
        dir.y = 0f;
        Vector3 flat = b.transform.position - from; flat.y = 0f;
        if (flat.magnitude > 1.5f)   // по горизонтали: иначе охотник подбегал под бота в прыжке и тот стоял у него на голове
            hunter.GetComponent<CharacterController>().Move(dir.normalized * hunter.walkSpeed * Time.deltaTime);
        hunter.AimAt(Center(b));
    }

    static IEnumerator MainChase()
    {
        yield return Ready();
        var bots = HiderBot.All.OrderBy(b => b.name).ToList();
        int n = 0;
        foreach (var b in bots)
        {
            if (b == null || b.Hider == null || b.Hider.Caught) continue;
            n++;
            // бот на пол в проход, охотник за ним в 5-7 м с видимостью, выстрел мимо рядом -> побег; дальше охотник гонится
            if (!b.Hider.transform.position.y.Equals(0f) && b.Hider.transform.position.y > 0.3f)
            {
                for (int k = 0; k < 80; k++)
                {
                    var rnd = new Vector3(Random.Range(-8f, 8f), 0.1f, Random.Range(-4f, 3f));
                    if (NavMesh.SamplePosition(rnd, out var nh, 1f, HumanoidFilter) && nh.position.y < 0.3f) { Teleport(b.Hider, nh.position); break; }
                }
                yield return Wait(0.3f);
            }
            if (!EnsureView(b.Hider, 4f, 6f, out var hp)) { L($"chase {b.name}: нет точки обзора"); continue; }
            PlaceHunter(hp, Center(b.Hider));
            hunter.NextShotTime = 0f;
            if (!NearMissAim(b.Hider, hunter.EyePosition, out var aim)) aim = Center(b.Hider) + Vector3.up * 1.2f;
            hunter.AimAt(aim);
            yield return null;
            b.Diag = new StringBuilder();
            hunter.Fire();
            float t0 = Time.time; int falseSafe = 0, frozenSeen = 0; HiderBot.State last = b.Current;
            var trans = new List<string>();
            while (Time.time - t0 < 18f && b != null && !b.Hider.Caught)
            {
                if (b.Current == HiderBot.State.Flee || b.Current == HiderBot.State.Look || b.Current == HiderBot.State.Relocate || b.Current == HiderBot.State.Settle || b.Current == HiderBot.State.Freeze) ChaseStep(b.Hider);
                if (b.Current != last)
                {
                    bool vis = Visible(HunterEye(hunter.transform.position), b.Hider);
                    float d = Vector3.Distance(hunter.transform.position, b.Hider.transform.position);
                    trans.Add($"   +{Time.time - t0:F2} {last} -> {b.Current}: охотник виден (тест, статика без своих коллайдеров)={vis}, дист {d:F2}");
                    if (last == HiderBot.State.Flee && vis && b.Current != HiderBot.State.Ram) falseSafe++;
                    last = b.Current;
                }
                if ((b.Current == HiderBot.State.Freeze || b.Current == HiderBot.State.Look || b.Current == HiderBot.State.Settle)
                    && Vector3.Distance(hunter.transform.position, b.Hider.transform.position) < 6f && Visible(HunterEye(hunter.transform.position), b.Hider)) frozenSeen++;
                yield return null;
            }
            L($"chase #{n} {b.name} ({b.personality.name}, {b.Hider.CurrentProp?.ModelId}): ложных концов побега при видимом охотнике={falseSafe}; кадров «замер/осмотр на виду ближе 6 м»={frozenSeen}; время {Time.time - t0:F1} с; " +
              $"конец побега={b.FleeEndReason}, этапов={b.FleeLegs}, загнан={b.CorneredCount}, продолжений побега={b.ResumedFlees}, сбросов спокойствия={b.UnseenResets}, таранов={b.RamCount}, затаился (не виден, пути мимо охотника)={b.HoldCount}, прыжков на полку ок/неудач={b.ShelfJumpsOk}/{b.ShelfJumpsFail}");
            foreach (var s in trans) L(s);
            L("   --- диагностика каждый кадр ---");
            L(b.Diag.ToString());
            b.Diag = null;
            yield return Wait(1f);
            if (n >= 2) break;
        }
    }

    static Prop FreeModel(string model) => Prop.All.FirstOrDefault(p => p.ModelId == model && p.IsFree);

    static bool FreeShelfPoint(ShelfSlot s, HiderPlayer h, out Vector3 spot)
    {
        spot = default;
        int small = 0;
        for (int i = 0; i < NavMesh.GetSettingsCount(); i++) { int id = NavMesh.GetSettingsByIndex(i).agentTypeID; if (NavMesh.GetSettingsNameFromID(id) == "HiderSmall") small = id; }
        var f = new NavMeshQueryFilter { agentTypeID = small, areaMask = NavMesh.AllAreas };
        for (float k = 0f; k < 0.36f; k += 0.04f)
            foreach (float u in new[] { 0.5f + k, 0.5f - k })
            {
                Vector3 p = s.Point(u);
                float r = h.BodyRadius + 0.05f, hh = h.BodyHeight * 0.5f;
                bool busy = Physics.OverlapBox(p + Vector3.up * (hh + 0.01f), new Vector3(r, hh - 0.005f, r), Quaternion.identity, ~0, QueryTriggerInteraction.Ignore)
                    .Any(c => !c.transform.IsChildOf(h.transform) && (c.GetComponentInParent<Prop>() != null || c.GetComponentInParent<HiderPlayer>() != null));
                if (busy) continue;
                if (!NavMesh.SamplePosition(p, out var nh, 0.15f, f)) continue;
                spot = nh.position; return true;
            }
        return false;
    }

    // Прятки на полках: бот (Small/Tiny) сам идёт на каждый уровень (0 основание .. 4 верхняя полка) по навмешу HiderSmall
    // с переходом Jump и спускается обратно на пол. Без ограничения кадров и при 60 кадр/с.
    static IEnumerator MainShelf()
    {
        yield return Ready();
        var bots = HiderBot.All.OrderBy(b => b.name).ToList();
        Vector3 away = new Vector3(-8f, 0.05f, 5.5f);   // охотник в подсобке: бота не видит, перемещению не мешает
        PlaceHunter(away, away + Vector3.forward);
        var cfgs = new[] { ("Can", bots[1], -1), ("Can", bots[1], 60), ("Box", bots[0], 60) };
        foreach (var (model, b, fps) in cfgs)
        {
            Application.targetFrameRate = fps; QualitySettings.vSyncCount = 0;
            var sample = FreeModel(model);
            if (sample == null) { L($"shelf: нет свободного {model}"); continue; }
            Morph(b.Hider, sample);
            if (NavMesh.SamplePosition(new Vector3(-1f, 0.05f, -3.2f), out var st, 1f, HumanoidFilter)) Teleport(b.Hider, st.position);
            yield return Wait(0.5f);
            L($"shelf [{model}, {(fps < 0 ? "без ограничения" : fps + " кадр/с")}] {b.name}: тело r={b.Hider.BodyRadius:F2} h={b.Hider.BodyHeight:F2}, мелкое (навмеш HiderSmall)={b.SmallBody}");
            for (int lv = 0; lv <= 4; lv++)
            {
                var slot = ShelfSlot.All.Where(s => s.level == lv).OrderBy(s => Vector3.Distance(s.Point(0.5f), b.Hider.transform.position)).FirstOrDefault(s => FreeShelfPoint(s, b.Hider, out _));
                if (slot == null || !FreeShelfPoint(slot, b.Hider, out var spot)) { L($"   уровень {lv}: нет свободного места"); continue; }
                int ok0 = b.ShelfJumpsOk, fail0 = b.ShelfJumpsFail;
                float t0 = Time.time; int f0 = Time.frameCount;
                string pre = $"облик {b.Hider.CurrentProp.ModelId}, мелкое={b.SmallBody}, состояние {b.Current}";
                bool started = b.DebugGoTo(spot);
                string pathInfo = started ? $"углов {b.PathCorners.Length}, конец y={b.PathCorners[b.PathCorners.Length - 1].y:F2}" : "";
                var seq = new List<string> { b.Current.ToString() };
                while (started && Time.time - t0 < 15f && b.Current == HiderBot.State.Relocate) yield return null;
                seq.Add(b.Current.ToString());
                yield return Wait(0.6f);
                L($"      ({pre}; {pathInfo}; состояния {string.Join("->", seq)})");
                Vector3 foot = b.Hider.transform.position;
                bool on = slot.Contains(foot, 0.08f);
                float fpsReal = (Time.frameCount - f0) / Mathf.Max(0.01f, Time.time - t0);
                L($"   уровень {lv} ({slot.transform.parent.name}/{slot.name}, верх {slot.top:F2}, просвет {slot.clearance:F2}): путь={started}, за {Time.time - t0 - 0.6f:F1} с, бот y={foot.y:F2}, на полке={on}, переходов ок/неудач +{b.ShelfJumpsOk - ok0}/+{b.ShelfJumpsFail - fail0}, ~{fpsReal:F0} кадр/с; {b.LastLinkLog}");
                if (on && fps < 0)
                {
                    // кадр глазами охотника из прохода: бот среди предметов на полке
                    DebugRoleSwitch.Swap();
                    Vector3 eyeFoot = new Vector3(foot.x + 0.6f, 0.05f, foot.z + slot.normal.z * 2.2f);
                    if (NavMesh.SamplePosition(eyeFoot, out var ef, 1f, HumanoidFilter)) PlaceHunter(ef.position, b.Hider.BodyCenter);
                    yield return Wait(0.4f);
                    ScreenCapture.CaptureScreenshot($"Assets/Screenshots/bot_shelf_level{lv}.png");
                    yield return Wait(0.4f);
                    DebugRoleSwitch.Swap();
                    PlaceHunter(away, away + Vector3.forward);
                    yield return Wait(0.2f);
                }
                // обратно на пол прохода
                Vector3 down = foot + slot.normal * 0.9f; down.y = 0.05f;
                if (NavMesh.SamplePosition(down, out var dn, 1f, HumanoidFilter))
                {
                    t0 = Time.time; ok0 = b.ShelfJumpsOk;
                    bool s2 = b.DebugGoTo(dn.position);
                    while (s2 && Time.time - t0 < 10f && b.Current == HiderBot.State.Relocate) yield return null;
                    yield return Wait(0.3f);
                    L($"      спуск: путь={s2}, за {Time.time - t0 - 0.3f:F1} с, бот y={b.Hider.transform.position.y:F2}, переходов ок +{b.ShelfJumpsOk - ok0}; {b.LastLinkLog}");
                }
            }
            // убрать бота с прохода, чтобы не мешал следующему прогону
            if (NavMesh.SamplePosition(new Vector3(8.5f, 0.05f, -6f), out var park, 2f, HumanoidFilter)) Teleport(b.Hider, park.position);
        }
        // Край стеллажа: бот подходит сбоку от торца, место у самого торца (u 0,02/0,98) — раньше прыгал вдоль торцевой стенки
        {
            var b = bots[0];
            Application.targetFrameRate = 60;
            int okE = 0, allE = 0;
            foreach (int lv in new[] { 1, 2, 3, 4 })
                foreach (float u in new[] { 0.02f, 0.98f })
                {
                    var slot = ShelfSlot.All.FirstOrDefault(sl => sl.level == lv && sl.transform.parent.name == "ShelfRow_1" && sl.normal.z < 0);
                    if (slot == null) continue;
                    if (!FreeShelfPointNear(slot, b.Hider, u, out var spot)) { L($"edge уровень {lv} u={u}: место занято"); continue; }
                    Vector3 side = new Vector3(u < 0.5f ? -6.3f : 6.3f, 0.05f, -2.7f);
                    if (NavMesh.SamplePosition(side, out var sp, 1f, HumanoidFilter)) Teleport(b.Hider, sp.position);
                    yield return Wait(0.3f);
                    int ok0 = b.ShelfJumpsOk, f0 = b.ShelfJumpsFail; float t0 = Time.time;
                    bool st = b.DebugGoTo(spot);
                    while (st && Time.time - t0 < 12f && b.Current == HiderBot.State.Relocate) yield return null;
                    yield return Wait(0.5f);
                    bool on = slot.Contains(b.Hider.transform.position, 0.08f);
                    allE++; if (on) okE++;
                    L($"edge уровень {lv} u={u:F2} (x={spot.x:F2}): на полке={on}, y={b.Hider.transform.position.y:F2}, переходов ок/неудач +{b.ShelfJumpsOk - ok0}/+{b.ShelfJumpsFail - f0}, {Time.time - t0 - 0.5f:F1} с; {b.LastLinkLog}");
                }
            L($"edge итог: {okE}/{allE}");
        }
        Application.targetFrameRate = -1;
    }

    static bool FreeShelfPointNear(ShelfSlot s, HiderPlayer h, float u0, out Vector3 spot)
    {
        spot = default;
        int small = 0;
        for (int i = 0; i < NavMesh.GetSettingsCount(); i++) { int id = NavMesh.GetSettingsByIndex(i).agentTypeID; if (NavMesh.GetSettingsNameFromID(id) == "HiderSmall") small = id; }
        var f = new NavMeshQueryFilter { agentTypeID = small, areaMask = NavMesh.AllAreas };
        float dir = u0 < 0.5f ? 1f : -1f;
        for (float k = 0f; k < 0.15f; k += 0.01f)
        {
            Vector3 p = s.Point(u0 + dir * k);
            float r = h.BodyRadius + 0.05f, hh = h.BodyHeight * 0.5f;
            bool busy = Physics.OverlapBox(p + Vector3.up * (hh + 0.01f), new Vector3(r, hh - 0.005f, r), Quaternion.identity, ~0, QueryTriggerInteraction.Ignore)
                .Any(c => !c.transform.IsChildOf(h.transform) && (c.GetComponentInParent<Prop>() != null || c.GetComponentInParent<HiderPlayer>() != null));
            if (busy) continue;
            if (!NavMesh.SamplePosition(p, out var nh, 0.12f, f)) continue;
            spot = nh.position; return true;
        }
        return false;
    }

    // Виляние: побег с охотником, идущим следом медленнее (3 м/с), каждый кадр в Diag (позиция, смещение вбок, земля). Анализ — отдельно по логу.
    static IEnumerator MainWeave()
    {
        yield return Ready();
        var bots = HiderBot.All.OrderBy(b => b.name).ToList();
        float speed0 = hunter.walkSpeed;
        var cfgs = new[] { ("Box", bots[0], -1), ("Bin", bots[1], -1), ("Box", bots[2 % bots.Count], 60) };
        foreach (var (model, b, fps) in cfgs)
        {
            Application.targetFrameRate = fps; QualitySettings.vSyncCount = 0;
            var sample = FreeModel(model);
            if (sample != null) Morph(b.Hider, sample);
            if (NavMesh.SamplePosition(new Vector3(-6f, 0.05f, -3.3f), out var st, 1f, HumanoidFilter)) Teleport(b.Hider, st.position);
            yield return Wait(0.6f);
            if (!EnsureView(b.Hider, 4f, 6f, out var hp)) { L($"weave {b.name}: нет точки обзора"); continue; }
            PlaceHunter(hp, Center(b.Hider));
            hunter.NextShotTime = 0f;
            if (!NearMissAim(b.Hider, hunter.EyePosition, out var aim)) aim = Center(b.Hider) + Vector3.up * 1.2f;
            hunter.AimAt(aim);
            yield return null;
            b.Diag = new StringBuilder();
            int ej0 = b.EvadeJumps, jc0 = b.JumpChecks;
            float ram0 = b.personality.ramChance;
            b.personality.ramChance = 0f;   // проверяем бег, не таран
            hunter.walkSpeed = 3f;
            hunter.Fire();
            float t0 = Time.time; int air = 0, frames = 0, lastF = -1, strayLand = 0; bool wasG = true, wasLink = false; float airFrom = 0f;
            while (Time.time - t0 < 14f && b != null && !b.Hider.Caught)
            {
                if (b.Current == HiderBot.State.Flee && Time.frameCount != lastF)
                {
                    lastF = Time.frameCount; ChaseStep(b.Hider); frames++;
                    bool g = b.Hider.Grounded;
                    if (!g && !b.LinkActive) air++;
                    // приземление после прыжка уклонения не на пол (полка, чужая голова) — то, чего коридор прыжка должен избегать
                    if (!g && wasG) airFrom = Time.time;
                    if (g && !wasG && !b.LinkActive && !wasLink && Time.time - airFrom > 0.2f && b.Hider.transform.position.y > 0.15f) strayLand++;
                    wasG = g; wasLink = b.LinkActive;
                }
                if (b.Current != HiderBot.State.Flee && b.Current != HiderBot.State.Reacting && Time.time - t0 > 1f) break;
                yield return null;
            }
            hunter.walkSpeed = speed0;
            b.personality.ramChance = ram0;
            L($"weave [{model}, {(fps < 0 ? "без ограничения" : fps + " кадр/с")}] {b.name}: кадров побега {frames}, в воздухе {air}, итог {b.Current} ({b.FleeEndReason}), ампл={b.weaveAmplitude} м, период={b.weavePeriod} с; прыжков уклонения {b.EvadeJumps - ej0} из проверок {b.JumpChecks - jc0}, последняя помеха: {b.JumpBlockedBy}; приземлений не на пол вне перехода: {strayLand}");
            L("   --- диагностика каждый кадр ---");
            L(b.Diag.ToString());
            b.Diag = null;
            yield return Wait(1f);
        }
        Application.targetFrameRate = -1;
    }

    // Одно обнаружение: охотник встаёт на 5-7 м с видимостью, стреляет (мимо рядом / прямо в бота), следим за реакцией.
    static IEnumerator RunDetect(string title, HiderBot b, bool hit, float minD, float maxD, string shotName = null)
    {
        if (b == null || b.Hider == null || b.Hider.Caught) { L($"{title}: бота нет"); yield break; }
        yield return Wait(0.3f);
        // шкала Z и кулдаун смены не трогаем: тест видит, как они ограничивают бота
        if (!EnsureView(b.Hider, 5f, 7f, out var hp)) { L($"{title} {b.name}: нет точки обзора"); yield break; }
        PlaceHunter(hp, Center(b.Hider));
        hunter.NextShotTime = 0f;
        Vector3 aim = Center(b.Hider);
        if (!hit && !NearMissAim(b.Hider, hunter.EyePosition, out aim)) { L($"{title} {b.name}: не нашёл прицел для near-miss"); yield break; }
        hunter.AimAt(aim);
        yield return null;
        string botName = b.name;
        string modelBefore = b.Hider.CurrentProp.ModelId;
        int hp0 = b.Hider.Hp;
        float dist = Vector3.Distance(hunter.transform.position, b.Hider.transform.position);
        float tFire = Time.time;
        float det0 = b.DetectedAt;
        hunter.Fire();
        var tl = new Timeline();
        yield return Track(b, tl, 25f, 1.0f, shotName, 0.6f);
        yield return Wait(1.2f);
        if (b == null || b.Hider == null || b.Hider.Caught) { L($"{title}: {botName} ({modelBefore}) с {dist:F1} м: бот ПОЙМАН (HP {hp0} -> 0)"); foreach (var line in tl.lines) L(line); yield break; }
        L($"{title}: {b.name} ({b.personality.name}, {modelBefore} {b.Hider.CurrentProp.tier}) с {dist:F1} м; HP {hp0}->{b.Hider.Hp}; обнаружение зафиксировано={b.DetectedAt > det0} ({b.LastDetect}); реакция до бега: {(b.FleeStartedAt - b.DetectedAt):F2} с (ожидаем ~{b.personality.reactionDelay}); максимум удалился {tl.maxMoved:F1} м; Z использован={(b.Hider.Boost != HiderPlayer.BoostPhase.Ready)}; облик после: {b.Hider.CurrentProp.ModelId}; {Where(b.Hider)}; состояние {b.Current}");
        foreach (var line in tl.lines) L(line);
        L($"   уклонение: смен знака бокового ввода {tl.weaveFlips}, макс. подъём над стартом {tl.maxJump:F2} м");
        L($"   бег длился (Flee->Freeze): {(tl.lines.Count > 0 ? "см. выше" : "нет")}, личность safeDistance={b.personality.safeDistance} м");
        yield return Wait(Mathf.Max(0.1f, 1.3f - (Time.time - tFire)));
    }
}
