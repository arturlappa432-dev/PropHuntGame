using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

// Автотест пинка / HP-бара / исключения HP при смене облика без MCP: флаг Temp/kick_test.flag -> Play -> отчёт Temp/kick_test.txt.
[InitializeOnLoad]
public static class KickPlayTest
{
    const string Flag = "Temp/kick_test.flag", Report = "Temp/kick_test.txt";
    static IEnumerator run;
    static StringBuilder log = new StringBuilder();

    static KickPlayTest()
    {
        EditorApplication.playModeStateChanged += OnState;
        if (File.Exists(Flag) && !EditorApplication.isPlayingOrWillChangePlaymode)
        {
            File.Delete(Flag);
            SessionState.SetBool("kicktest", true);
            EditorApplication.delayCall += () => EditorApplication.EnterPlaymode();
        }
    }

    static void OnState(PlayModeStateChange s)
    {
        if (s == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool("kicktest", false))
        {
            SessionState.SetBool("kicktest", false);
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

    static void Teleport(HiderPlayer h, Vector3 p)
    {
        var cc = h.GetComponent<CharacterController>();
        cc.enabled = false; h.transform.position = p; cc.enabled = true;
    }

    static Prop Sample(string name) => Prop.All.First(p => p.name == name && p.IsFree);

    static void Morph(HiderPlayer h, Prop sample)
    {
        typeof(HiderPlayer).GetMethod("Morph", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(h, new object[] { sample });
    }

    static void SetHp(HiderPlayer h, int hp) { typeof(HiderPlayer).GetProperty("Hp").SetValue(h, hp); }

    static IEnumerator Main()
    {
        yield return Wait(1f);
        var rs = RoundState.Instance;
        rs.EndPrep();
        yield return Wait(0.5f);
        var hunter = HunterPlayer.All[0];
        var dummy = HiderPlayer.All.Find(h => !h.controlled);
        L($"hunter={hunter != null} dummy={dummy != null} dummyProp={(dummy.CurrentProp != null ? dummy.CurrentProp.name : "null")}");
        var auth = RamKickAuthority.Instance;
        auth.kickCooldown = 0f;

        Vector3 hp0 = new Vector3(0f, 0.1f, -3.2f);
        hunter.transform.SetPositionAndRotation(hp0, Quaternion.identity);   // смотрит на +z
        L($"hunter feet y={hunter.transform.position.y:F3}");

        // --- T1: тиры, цель сзади охотника (прицел ни при чём) ---
        foreach (var name in new[] { "Can", "Snack", "Box", "Basket", "Bin", "Stool", "Table", "Machine" })
        {
            Prop s = null;
            try { s = Sample(name); } catch { L($"T1 {name}: нет свободного образца"); continue; }
            Morph(dummy, s);
            Teleport(dummy, hp0 + new Vector3(0f, 0f, -0.6f));
            yield return Wait(0.1f);
            hunter.NextKickTime = 0f;
            var r = auth.TryKick(hunter);
            L($"T1 {name} tier={s.tier} (сзади 0,6 м): fired={r.fired} hit={r.hit} stun={dummy.Stun}");
            yield return Wait(8f);
            L($"   после: stun={dummy.Stun}");
        }

        // --- T2: цель по прицелу, но далеко от ног ---
        Morph(dummy, Sample("Box"));
        Teleport(dummy, hp0 + new Vector3(0f, 0f, 3f));
        yield return Wait(0.1f);
        hunter.NextKickTime = 0f;
        var r2 = auth.TryKick(hunter);
        L($"T2 Box под прицелом на 3 м: fired={r2.fired} hit={r2.hit} (ожидаем hit=False)");
        Teleport(dummy, hp0 + new Vector3(0.7f, 0f, 0f));
        yield return Wait(0.1f);
        hunter.NextKickTime = 0f;
        var r3 = auth.TryKick(hunter);
        L($"T2 Box сбоку 0,7 м: hit={r3.hit} (ожидаем True)");
        yield return Wait(8f);

        // --- T3: осадка, серия пинков с замером ---
        var names = new[] { "Can", "Box", "Basket", "Snack" };
        for (int i = 0; i < 8; i++)
        {
            var n = names[i % names.Length];
            Morph(dummy, Sample(n));
            float ang = i * 45f * Mathf.Deg2Rad;
            Vector3 start = hp0 + new Vector3(Mathf.Sin(ang), 0f, Mathf.Cos(ang)) * 0.55f;
            Teleport(dummy, start);
            yield return Wait(0.1f);
            hunter.NextKickTime = 0f;
            var rr = auth.TryKick(hunter);
            if (!rr.hit) { L($"T3#{i} {n}: промах"); continue; }
            float t0 = Time.time;
            var prop = dummy.CurrentProp;
            float tOut = -1f, gapAtOut = -1f;
            Vector3 posAtOut = Vector3.zero, launchedFrom = prop.transform.position;
            while (dummy.Stun != HiderPlayer.StunPhase.None && Time.time - t0 < 15f)
            {
                if (dummy.Stun == HiderPlayer.StunPhase.Out && tOut < 0f)
                {
                    tOut = Time.time - t0;
                    posAtOut = prop.transform.position;
                    float bottom = prop.Colliders.Where(c => c != null).Min(c => c.bounds.min.y);
                    var hits = Physics.RaycastAll(new Vector3(posAtOut.x, bottom + 0.02f, posAtOut.z), Vector3.down, 3f, RamKickAuthority.StaticMask, QueryTriggerInteraction.Ignore);
                    gapAtOut = hits.Length == 0 ? 99f : hits.Min(h => h.distance) - 0.02f;
                }
                yield return null;
            }
            float flown = Vector3.Distance(new Vector3(posAtOut.x, 0, posAtOut.z), new Vector3(launchedFrom.x, 0, launchedFrom.z));
            L($"T3#{i} {n}: фаза1 {tOut:F2} с, зазор до опоры при входе в «отключку» {gapAtOut * 1000f:F0} мм, горизонтально {flown:F2} м, всего {Time.time - t0:F2} с, итог stun={dummy.Stun}");
            yield return Wait(0.3f);
        }

        // --- T4: HP при смене облика ---
        Morph(dummy, Sample("Box"));
        yield return Wait(0.1f);
        SetHp(dummy, 33);
        Morph(dummy, Sample("Box"));
        L($"T4 Box 33/100 -> тот же Box: HP={dummy.Hp}/{dummy.MaxHp} (ожидаем 33/100)");
        Morph(dummy, Sample("Basket"));
        L($"T4 Box 33/100 -> Basket (тот же тир, другая модель): HP={dummy.Hp}/{dummy.MaxHp} (формула: 1-0,67*1,15=0,2295 -> 23)");
        SetHp(dummy, 70);
        Morph(dummy, Sample("Basket"));
        L($"T4 Basket 70/100 -> Basket: HP={dummy.Hp} (ожидаем 70)");
        Morph(dummy, Sample("Bin"));
        L($"T4 Basket 70/100 -> Bin(Medium): HP={dummy.Hp}/{dummy.MaxHp}");
        Morph(dummy, Sample("Bin"));
        L($"T4 Bin -> Bin: HP={dummy.Hp}/{dummy.MaxHp} (без изменений)");

        // --- T5: HP-бар ---
        Morph(dummy, Sample("Box"));
        SetHp(dummy, 100);
        Teleport(dummy, hp0 + new Vector3(0f, 0f, 3f));
        yield return Wait(0.2f);
        dummy.ApplyHit(40);
        yield return Wait(0.2f);
        var bar = Object.FindObjectsByType<HpBar>(FindObjectsSortMode.None).FirstOrDefault();
        L($"T5 после попадания: бар={(bar != null)} Visible={(bar != null && bar.Visible)} Fraction={(bar != null ? bar.Fraction : -1):F2} (ожидаем 0,60)");
        yield return Wait(1.5f);
        dummy.ApplyHit(10);
        yield return Wait(0.2f);
        L($"T5 второе попадание продлило: Visible={bar.Visible} Fraction={bar.Fraction:F2} (0,50)");
        yield return Wait(1.5f);
        L($"T5 через 1,7 с после второго: Visible={bar.Visible} (ожидаем True: продлён)");
        yield return Wait(1.6f);
        L($"T5 через ~3,3 с после второго: Visible={bar.Visible} (ожидаем False)");
        var alphas = bar.GetComponentsInChildren<SpriteRenderer>().Select(s => s.color.a).ToArray();
        L($"T5 альфа спрайтов: {string.Join(",", alphas.Select(a => a.ToString("F2")))}");

        // --- T6: бар у самого прячущегося: виден от третьего лица, не виден от первого ---
        var me = HiderPlayer.All.Find(h => h.controlled);
        if (me != null && me.CurrentProp != null)
        {
            var tp = typeof(HiderPlayer).GetField("thirdPerson", BindingFlags.NonPublic | BindingFlags.Instance);
            SetHp(me, me.MaxHp);
            tp.SetValue(me, true);
            yield return Wait(0.5f);
            me.ApplyHit(me.MaxHp / 4);
            yield return Wait(0.3f);
            var myBar = Object.FindObjectsByType<HpBar>(FindObjectsSortMode.None).First(b => b.GetComponentInParent<Transform>() != null && typeof(HpBar).GetField("owner", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(b) == me);
            float aTp = myBar.GetComponentsInChildren<SpriteRenderer>().Max(s => s.color.a);
            L($"T6 свой бар, третье лицо: Visible={myBar.Visible} Fraction={myBar.Fraction:F2} альфа={aTp:F2} (ожидаем >0), HideOwnBody={me.HideOwnBody}");
            ScreenCapture.CaptureScreenshot("Assets/Screenshots/hpbar_own_thirdperson.png");
            yield return Wait(0.5f);
            tp.SetValue(me, false);
            me.ApplyHit(1);
            yield return Wait(0.3f);
            float aFp = myBar.GetComponentsInChildren<SpriteRenderer>().Max(s => s.color.a);
            L($"T6 свой бар, первое лицо: альфа={aFp:F2} (ожидаем 0)");
        }
        else L("T6 пропущен: нет управляемого прячущегося с предметом");

        // Скриншот бара глазами охотника
        DebugRoleSwitch.Swap();
        yield return Wait(0.3f);
        // самое свободное направление из точки охотника
        float bestFree = 0f; float bestYaw = 0f;
        for (int k = 0; k < 16; k++)
        {
            float yawK = k * 22.5f;
            Vector3 d = Quaternion.Euler(0, yawK, 0) * Vector3.forward;
            float free = Physics.Raycast(hp0 + Vector3.up * 0.5f, d, out var hk, 12f, ~0, QueryTriggerInteraction.Ignore) ? hk.distance : 12f;
            if (free > bestFree) { bestFree = free; bestYaw = yawK; }
        }
        float dist = Mathf.Min(4f, bestFree - 0.6f);
        Vector3 dirK = Quaternion.Euler(0, bestYaw, 0) * Vector3.forward;
        hunter.transform.SetPositionAndRotation(hp0, Quaternion.Euler(0, bestYaw, 0));
        typeof(HunterPlayer).GetField("yaw", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(hunter, bestYaw);
        typeof(HunterPlayer).GetField("pitch", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(hunter, 12f);
        Teleport(dummy, hp0 + dirK * dist);
        L($"T5 скриншот: свободно {bestFree:F1} м по yaw {bestYaw}, манекен на {dist:F1} м");
        dummy.ApplyHit(20);
        yield return Wait(0.4f);
        string shot = "Assets/Screenshots/hpbar_hunter_view.png";
        ScreenCapture.CaptureScreenshot(shot);
        yield return Wait(0.6f);
        L($"T5 скриншот: {shot}");
    }
}
