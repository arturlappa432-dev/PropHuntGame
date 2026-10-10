using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

// Проверка прыжка охотника в Play (Space событием клавиатуры Input System). Запуск: Play, затем через MCP
// System.Type.GetType("HunterJumpPlayTest, Assembly-CSharp-Editor").GetMethod("Start").Invoke(null, null). Отчёт Temp/hunter_jump_test.txt.
public static class HunterJumpPlayTest
{
    const string Report = "Temp/hunter_jump_test.txt";
    static IEnumerator run;
    static StringBuilder log = new StringBuilder();
    static void L(string s) { log.AppendLine(s); }

    public static void Start()
    {
        log.Clear();
        run = Main();
        EditorApplication.update += Tick;
    }

    static void Tick()
    {
        try { if (run == null || !run.MoveNext()) Finish(); }
        catch (System.Exception e) { L("EXCEPTION " + e); Finish(); }
    }

    static void Finish()
    {
        EditorApplication.update -= Tick;
        File.WriteAllText(Report, log.ToString());
        run = null;
    }

    static void Key(Key k, bool down)
    {
        var kb = Keyboard.current;
        InputSystem.QueueStateEvent(kb, down ? new KeyboardState(k) : new KeyboardState());   // обработает цикл игрока, не редактор
    }

    // Таран в воздухе: Space, на подъёме выше 0,5 м вызывается Knock (как из RamKickAuthority.TryRam), затем наблюдение до подъёма.
    public static void StartKnockMidair()
    {
        log.Clear();
        run = KnockMain();
        EditorApplication.update += Tick;
    }

    static IEnumerator KnockMain()
    {
        InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
        var h = Object.FindFirstObjectByType<HunterPlayer>();
        if (RoundState.Instance.Phase == RoundPhase.Prep) RoundState.Instance.EndPrep();
        h.SetControlled(true);
        foreach (var hp in HiderPlayer.All) hp.SetControlled(false);
        for (float w0 = Time.time; Time.time - w0 < 0.5f;) yield return null;
        var cc = h.GetComponent<CharacterController>();
        var pivot = (Transform)typeof(HunterPlayer).GetField("pivot", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(h);
        float baseY = h.transform.position.y;
        Vector3 startPos = h.transform.position;
        Key(UnityEngine.InputSystem.Key.Space, true);
        float tStart = Time.time;
        while (h.transform.position.y < baseY + 0.7f && Time.time - tStart < 3f)
        {
            Key(UnityEngine.InputSystem.Key.Space, true);
            yield return null;
        }
        Key(UnityEngine.InputSystem.Key.Space, false);
        L($"Таран в воздухе на высоте {h.transform.position.y - baseY:F2} м, vy={Get<float>(h, "vy"):F2}");
        h.Knock(h.transform.forward * 7f + Vector3.up * 3f, Random.onUnitSphere * 6f, RamKickAuthority.Instance);
        L($"Knocked={h.Knocked}, cc.enabled={cc.enabled}, pivotActive={pivot.gameObject.activeSelf}");
        float t0 = Time.time; float maxFall = 0f, minY = 99f;
        while (h.Knocked && Time.time - t0 < 12f) { yield return null; }
        L($"Ragdoll закончился через {Time.time - t0:F2} с (жёсткий таймер 3 с + подъём 0,8 с), Knocked={h.Knocked}");
        for (float w0 = Time.time; Time.time - w0 < 1f;) yield return null;
        var p = h.transform.position;
        L($"После подъёма: pos={p}, смещение от старта {(new Vector2(p.x - startPos.x, p.z - startPos.z)).magnitude:F2} м, y={p.y:F3}, cc.enabled={cc.enabled}, pivotActive={pivot.gameObject.activeSelf}, scale={pivot.localScale}, vy={Get<float>(h, "vy"):F2}, камера-глаза-над-ногами={h.cam.transform.position.y - p.y:F2}");
        // Прыжок снова работает?
        float y1 = h.transform.position.y; float mx = y1;
        Key(UnityEngine.InputSystem.Key.Space, true);
        for (float w0 = Time.time; Time.time - w0 < 2f;) { Key(UnityEngine.InputSystem.Key.Space, Time.time - w0 < 0.1f); mx = Mathf.Max(mx, h.transform.position.y); yield return null; }
        Key(UnityEngine.InputSystem.Key.Space, false);
        L($"Прыжок после подъёма: апекс {mx - y1:F3} м");
        L("Готово");
    }

    static T Get<T>(object o, string f) => (T)o.GetType().GetField(f, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(o);

    static IEnumerator Main()
    {
        InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
        var h = Object.FindFirstObjectByType<HunterPlayer>();
        if (h == null) { L("нет охотника"); yield break; }
        if (RoundState.Instance.Phase == RoundPhase.Prep)
        {
            // подготовка: прыжок заблокирован как и движение, замер до EndPrep
            h.SetControlled(true);
            float y0 = h.transform.position.y;
            Key(UnityEngine.InputSystem.Key.Space, true);
            yield return null; Key(UnityEngine.InputSystem.Key.Space, false);
            float t0 = Time.time; float maxY0 = y0;
            while (Time.time - t0 < 1f) { maxY0 = Mathf.Max(maxY0, h.transform.position.y); yield return null; }
            L($"Prep: Space -> максимум y {maxY0 - y0:F3} м (ожидается 0)");
            RoundState.Instance.EndPrep();
        }
        h.SetControlled(true);
        foreach (var hp in HiderPlayer.All) hp.SetControlled(false);
        for (float w0 = Time.time; Time.time - w0 < 0.5f;) yield return null;
        var cam = h.cam;
        L($"JumpHeight (формула) = {h.JumpHeight:F3} м, controlled={h.controlled}, blocked={h.IsBlocked}, layer={LayerMask.LayerToName(h.gameObject.layer)}");

        float baseY = h.transform.position.y;
        float eyeBase = cam.transform.position.y - baseY;
        var pivot = (Transform)typeof(HunterPlayer).GetField("pivot", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(h);
        var landClipField = typeof(HunterPlayer).GetField("landClip", BindingFlags.NonPublic | BindingFlags.Instance);
        L($"Старт: y={baseY:F3}, высота глаз {eyeBase:F3}, landClip={(landClipField.GetValue(h) != null)}");

        // Клавиша удерживается несколькими событиями подряд: одиночное событие у редактора без фокуса сбрасывается.
        var wf = typeof(HunterPlayer).GetField("windup", BindingFlags.NonPublic | BindingFlags.Instance);
        for (int f0 = Time.frameCount; Time.frameCount - f0 < 6;)
        {
            Key(UnityEngine.InputSystem.Key.Space, true);
            L($"  кадр {Time.frameCount}: space={Keyboard.current.spaceKey.isPressed} windup={wf.GetValue(h)} y={h.transform.position.y:F3}");
            yield return null;
        }
        Key(UnityEngine.InputSystem.Key.Space, false);
        float maxY = baseY, minEye = eyeBase, minScaleY = 1f, maxScaleY = 1f, lastY = baseY;
        bool landed = false, left = false;
        float tStart = Time.time, tLand = 0f, minEyeAfterLand = 99f;
        float windupEnd = -1f;
        while (Time.time - tStart < 4f)
        {
            float y = h.transform.position.y;
            maxY = Mathf.Max(maxY, y);
            float eye = cam.transform.position.y - y;
            minEye = Mathf.Min(minEye, eye);
            minScaleY = Mathf.Min(minScaleY, pivot.localScale.y);
            maxScaleY = Mathf.Max(maxScaleY, pivot.localScale.y);
            if (!left && y > baseY + 0.05f) { left = true; windupEnd = Time.time - tStart; }
            if (left && !landed && y <= baseY + 0.02f && y < lastY) { landed = true; tLand = Time.time; }
            if (landed && Time.time - tLand < 0.5f) minEyeAfterLand = Mathf.Min(minEyeAfterLand, eye);
            if (left && !landed && Time.frameCount % 3 == 0) L($"  f{Time.frameCount} t={Time.time - tStart:F3} dt={Time.deltaTime:F4} y={y - baseY:F3} vy={Get<float>(h, "vy"):F2} grounded={h.GetComponent<CharacterController>().isGrounded}");
            lastY = y;
            yield return null;
        }
        L($"Прыжок: апекс {maxY - baseY:F3} м (ожидается ~{h.JumpHeight:F2}), отрыв через {windupEnd:F2} с, приземлился={landed}");
        L($"Камера: мин. высота глаз {minEye:F3} при базе {eyeBase:F3} (приседание/толчок {eyeBase - minEye:F3} м), после посадки мин. {minEyeAfterLand:F3}");
        L($"Масштаб тела Y: мин {minScaleY:F3}, макс {maxScaleY:F3}; финал {pivot.localScale.y:F3}");
        L($"Слой после прыжка: {LayerMask.LayerToName(h.gameObject.layer)}; HideOwnBody={h.HideOwnBody}");

        // Hunter-OwnBody: прыжок прямо над/на прячущегося-манекена не должен выталкивать охотника (слой). Проверка через матрицу.
        L($"Physics.GetIgnoreLayerCollision(Hunter, OwnBody) = {Physics.GetIgnoreLayerCollision(LayerMask.NameToLayer("Hunter"), LayerMask.NameToLayer("OwnBody"))}");
        L("Готово");
    }
}
