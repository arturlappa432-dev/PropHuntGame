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
    const string Flag = "Temp/bot_test.flag", Report = "Temp/bot_test.txt";
    static IEnumerator run;
    static StringBuilder log = new StringBuilder();

    static BotPlayTest()
    {
        EditorApplication.playModeStateChanged += OnState;
        if (File.Exists(Flag) && !EditorApplication.isPlayingOrWillChangePlaymode)
        {
            File.Delete(Flag);
            SessionState.SetBool("bottest", true);
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
            run = Main();
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
                if (!NavMesh.SamplePosition(want, out var nh, 0.8f, NavMesh.AllAreas)) continue;
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
            if (!NavMesh.SamplePosition(rnd, out var nh, 1f, NavMesh.AllAreas)) continue;
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

    class Timeline { public List<string> lines = new List<string>(); public float t0; public HiderBot.State last; public float maxMoved; public Vector3 start; public bool hunterKnocked; public float lastTrace; }

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
                tl.lines.Add($"   +{Time.time - tl.t0:F2} с: {tl.last} -> {b.Current}");
                tl.last = b.Current;
                if (b.Current == HiderBot.State.Reacting && !reacted) { reacted = true; reactT = Time.time; }
            }
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
        foreach (var b in bots) L($"  {b.name}: личность={b.personality.name} реакция={b.personality.reactionDelay} с, бег={b.personality.fleeDuration} с, шанс тарана={b.personality.ramChance}, перемещение каждые {b.personality.relocateEvery} с, видит охотника до {b.personality.noticeRange} м");

        // ---------- карта навмеша: '#' пол, связный со входом; 'o' пол, несвязный; '^' только выше пола; '.' нет ----------
        {
            NavMesh.SamplePosition(new Vector3(0f, 0.1f, -5f), out var entry, 2f, NavMesh.AllAreas);
            var path = new NavMeshPath();
            var sbn = new StringBuilder();
            sbn.AppendLine($"навмеш: вход {entry.position}");
            for (float z = 7f; z >= -7.01f; z -= 0.5f)
            {
                sbn.Append($"{z,5:F1} ");
                for (float x = -10f; x <= 10.01f; x += 0.5f)
                {
                    char ch = '.';
                    if (NavMesh.SamplePosition(new Vector3(x, 0.05f, z), out var f, 0.2f, NavMesh.AllAreas) && f.position.y < 0.3f)
                        ch = NavMesh.CalculatePath(entry.position, f.position, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete ? '#' : 'o';
                    else if (NavMesh.SamplePosition(new Vector3(x, 1.0f, z), out var u, 1.0f, NavMesh.AllAreas) && u.position.y > 0.3f && Mathf.Abs(u.position.x - x) < 0.25f && Mathf.Abs(u.position.z - z) < 0.25f) ch = '^';
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
                    if (NavMesh.SamplePosition(rnd, out var nh, 1f, NavMesh.AllAreas)) { spot = nh.position; ok = true; }
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
                    if (NavMesh.SamplePosition(new Vector3(x, 0.1f, z), out var nh, 0.5f, NavMesh.AllAreas) && Vector3.Distance(nh.position, b.Hider.transform.position) > bestD)
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
                    if (NavMesh.SamplePosition(new Vector3(x, 0.1f, z), out var nh2, 0.5f, NavMesh.AllAreas))
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
        L($"   бег длился (Flee->Freeze): {(tl.lines.Count > 0 ? "см. выше" : "нет")}, личность fleeDuration={b.personality.fleeDuration} с");
        yield return Wait(Mathf.Max(0.1f, 1.3f - (Time.time - tFire)));
    }
}
