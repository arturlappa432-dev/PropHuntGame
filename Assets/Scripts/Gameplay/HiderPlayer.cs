using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Локальный прячущийся: стикмен до вселения, потом предмет. Ввод и камера клиентские,
// решение «можно ли вселиться» принимает PossessionAuthority.
[RequireComponent(typeof(CharacterController))]
public class HiderPlayer : MonoBehaviour, IOwnBodyViewer
{
    public static readonly List<HiderPlayer> All = new List<HiderPlayer>();

    public Camera cam;
    public bool controlled = true;     // false: прячущийся без локального ввода (манекен для теста боя)
    public Transform stickman;
    public Material outlineMaterial;
    public Material puffMaterial;
    public float walkSpeed = 4f;      // скорость движения в документах открыта, значение временное
    public float gravity = -20f;
    public float suckDuration = 0.35f;
    public float mouseSensitivity = 0.1f;
    public float propTurnSpeed = 100f;   // °/с, предложено 90-120, калибровать
    public float hunterHeight = 1.8f;           // рост охотника (его ещё нет), от него считается рост стикмена
    public float stickmanRatio = 3.5f;          // стикмен в 3-4 раза меньше охотника (possession.md / movement-and-camera.md)
    public float jumpHeightMultiplier = 1.75f;  // высота прыжка = множитель × собственная высота модели, предложено 1,5-2
    public float minHidingJumpHeight = 2.05f;   // абсолютный минимум высоты прыжка: верхняя полка 1,82 м (измерено в сцене) + ~12%
    public float jumpWindup = 0.1f;             // приседание перед отрывом, сек

    public float boostSpeedMult = 1.5f;   // boost.md: ×1,5 (тестировать)
    public float boostDuration = 3f;      // сек ускорения = полная шкала
    public float boostRecharge = 15f;     // сек восстановления шкалы после конца ускорения

    public enum BoostPhase { Ready, Active, Recharging }
    public BoostPhase Boost { get; private set; } = BoostPhase.Ready;
    public float BoostFill { get; private set; } = 1f;   // 1 = полная шкала
    public bool Sliding => Boost == BoostPhase.Active;   // слайд: процедурное «прыг-скок» (movement-and-camera.md) на это время должно быть выключено

    // Бот (HiderBot) подаёт тот же ввод, что и клавиатура: движение, прыжок, Z, E. Прицел для E — луч из глаз по yaw/pitch бота.
    public BotInput botInput;
    public enum DetectKind { Hit, NearMiss, Kick }
    public event System.Action<DetectKind> Detected;   // реальное обнаружение: попадание, near-miss (свист), пинок; приближение охотника сюда не входит
    public bool Busy => busy;

    public bool Caught { get; private set; }
    public bool IsAlive => !Caught;
    public Vector3 BodyCenter => Stun != StunPhase.None && CurrentProp != null ? CurrentProp.transform.position : transform.position + Vector3.up * BodyHeight * 0.5f;
    public Vector3 HorizontalVelocity { get { var v = cc.velocity; v.y = 0f; return v; } }
    public enum StunPhase { None, Flight, Out, Realign }   // пинок: кувырок -> «в отключке» (звёзды) -> самовыравнивание
    public StunPhase Stun { get; private set; } = StunPhase.None;
    public Prop CurrentProp { get; private set; }
    public Prop Target { get; private set; }
    public int Hp { get; private set; }
    public int MaxHp { get; private set; }
    public float NextRepossessTime { get; private set; }
    public string Toast { get; private set; } = "";

    public Vector3 EyePosition => Stun != StunPhase.None && CurrentProp != null ? CurrentProp.transform.position + Vector3.up * CurrentProp.height * 0.4f : transform.position + Vector3.up * EyeHeight;
    float EyeHeight => CurrentProp == null ? StickmanHeight * 0.9f : Mathf.Max(0.25f, CurrentProp.height * 0.9f);
    public float StickmanHeight => hunterHeight / stickmanRatio;
    public float BodyHeight => CurrentProp == null ? StickmanHeight : CurrentProp.height;
    public float JumpHeight => Mathf.Max(BodyHeight * jumpHeightMultiplier, minHidingJumpHeight);

    static int OwnBodyLayer => LayerMask.NameToLayer("OwnBody");

    CharacterController cc;
    float yaw, pitch, vy, camDist, toastUntil, propYaw;
    bool thirdPerson, busy, eConsumed;
    float windup = -1f, squash = 1f, camKick, camKickVel;
    bool wasAirborne;
    float hitFlashUntil;
    Vector3 propBaseScale = Vector3.one, stickBaseScale = Vector3.one;
    AudioSource sfx;
    AudioClip landClip;

    void OnEnable() { All.Add(this); OwnBodyCulling.Register(this); }
    void OnDisable() { All.Remove(this); OwnBodyCulling.Unregister(this); }

    // Своё тело от первого лица скрывается только от камеры владельца (OwnBodyCulling), слой для этого не используется.
    public Camera ViewCamera => controlled ? cam : null;
    public bool ThirdPerson => thirdPerson;
    public bool HideOwnBody => controlled && !thirdPerson && !Caught && CurrentProp != null;
    public void CollectOwnRenderers(List<Renderer> buffer) { CurrentProp.GetComponentsInChildren<Renderer>(false, buffer); }

    void Awake() { cc = GetComponent<CharacterController>(); cc.skinWidth = 0.01f; }   // дефолт 0.08 даёт видимый зазор

    void Start()
    {
        if (controlled) Cursor.lockState = CursorLockMode.Locked;
        if (cam != null) cam.transform.SetParent(null, true);   // камера общая: её пересаживают на охотника и обратно
        yaw = transform.eulerAngles.y;
        ApplyStickmanShape();
        landClip = MakeLandClip();
        sfx = gameObject.AddComponent<AudioSource>();
        sfx.spatialBlend = 0f;
    }

    // Стикмен до вселения: рост охотника / stickmanRatio (капсула-примитив высотой 2 по Y).
    void ApplyStickmanShape()
    {
        float h = StickmanHeight, r = Mathf.Clamp(h * 0.17f, cc.skinWidth * 2f, h * 0.5f);
        cc.enabled = false;
        cc.radius = r; cc.height = h; cc.center = new Vector3(0, h * 0.5f + cc.skinWidth, 0);   // CC опирается низом капсулы на опору + skinWidth: поднимаем капсулу на skinWidth, чтобы корень (низ меша) стоял вплотную
        cc.stepOffset = Mathf.Min(0.3f, h * 0.5f);
        cc.enabled = true;
        stickBaseScale = new Vector3(r * 2f, h * 0.5f, r * 2f);
        stickman.localScale = stickBaseScale;
        stickman.localPosition = new Vector3(0, h * 0.5f, 0);
    }

    void Update()
    {
        if (Caught) return;
        if (Toast.Length > 0 && Time.time > toastUntil) Toast = "";
        var kb = controlled ? Keyboard.current : null;
        var mouse = controlled ? Mouse.current : null;
        if (mouse != null && Cursor.lockState == CursorLockMode.Locked)
        {
            Vector2 d = mouse.delta.ReadValue() * mouseSensitivity;
            yaw += d.x;
            pitch = Mathf.Clamp(pitch - d.y, -80f, 80f);
        }
        if (botInput != null) { yaw = botInput.yaw; pitch = Mathf.Clamp(botInput.pitch, -80f, 80f); }
        if (kb != null && kb.vKey.wasPressedThisFrame) thirdPerson = !thirdPerson;

        UpdateBoost();
        if ((kb != null && kb.zKey.wasPressedThisFrame) || (botInput != null && botInput.TakeBoost())) PressBoost();

        if (!busy)
        {
            Move(kb);
            if (Sliding && CurrentProp != null) RamKickAuthority.Instance.TryRam(this);   // таран только во время ускорения
            if (controlled || botInput != null) UpdateTarget();
            if (botInput != null && botInput.TakePossess() && (CurrentProp == null || MorphReady)) PressPossess();
            if (kb != null && kb.eKey.wasPressedThisFrame && MorphReady) { PressPossess(); eConsumed = true; }
            else if (kb != null && kb.eKey.wasPressedThisFrame && CurrentProp == null) PressPossess();
            if (kb != null && !kb.eKey.isPressed) eConsumed = false;
            RotateProp(kb);
        }
        UpdateSquash();
    }

    // E превращает только когда есть подсвеченная цель и кулдаун истёк; иначе E поворачивает предмет вправо.
    bool MorphReady => CurrentProp != null && Target != null && Time.time >= NextRepossessTime;

    // Q влево, E вправо (E только без подсвеченной цели: у превращения приоритет). Только yaw, отдельно от камеры.
    void RotateProp(Keyboard kb)
    {
        if (CurrentProp == null) return;
        float dir = 0f;
        if (kb != null)
        {
            if (kb.qKey.isPressed) dir -= 1f;
            if (kb.eKey.isPressed && !eConsumed && !MorphReady) dir += 1f;
        }
        propYaw += dir * propTurnSpeed * Time.deltaTime;
        CurrentProp.transform.localRotation = Quaternion.Euler(0f, propYaw, 0f);
    }

    void Move(Keyboard kb)
    {
        // Тело-предмет не крутится за камерой: у него своя ориентация (propYaw), поворачивается только стикмен.
        Quaternion look = Quaternion.Euler(0, yaw, 0);
        if (CurrentProp == null) transform.rotation = look;
        Vector3 input = Vector3.zero;
        if (kb != null)
        {
            if (kb.wKey.isPressed) input.z += 1;
            if (kb.sKey.isPressed) input.z -= 1;
            if (kb.dKey.isPressed) input.x += 1;
            if (kb.aKey.isPressed) input.x -= 1;
        }
        if (botInput != null) { input.x = botInput.move.x; input.z = botInput.move.y; }
        Vector3 move = look * input.normalized * walkSpeed * (Sliding ? boostSpeedMult : 1f);
        slideMoving = Sliding && input.sqrMagnitude > 0.01f;
        bool grounded = cc.isGrounded;
        if (grounded && wasAirborne) Land(-vy);
        bool jumpPressed = (kb != null && kb.spaceKey.wasPressedThisFrame) || (botInput != null && botInput.TakeJump());
        if (grounded && windup < 0f && jumpPressed) windup = jumpWindup;
        if (windup >= 0f)
        {
            windup -= Time.deltaTime;
            if (windup < 0f)
            {
                if (grounded) vy = Mathf.Sqrt(2f * -gravity * JumpHeight);   // отрыв без звука
                windup = -1f;
                grounded = false;
            }
            else windup = Mathf.Max(windup, 0f);
        }
        if (grounded) vy = -1f; else vy += gravity * Time.deltaTime;
        move.y = vy;
        cc.Move(move * Time.deltaTime);
        wasAirborne = !cc.isGrounded;
    }

    // --- Ускорение Z (boost.md): одно нажатие, только при полной шкале, без звука активации ---

    bool slideMoving;
    ParticleSystem dust;

    // Нажатие Z. Включается только в облике предмета и только при полной шкале.
    public void PressBoost()
    {
        if (Caught || busy || CurrentProp == null || Boost != BoostPhase.Ready) return;
        Boost = BoostPhase.Active;
        BoostFill = 1f;
    }

    void UpdateBoost()
    {
        switch (Boost)
        {
            case BoostPhase.Active:
                BoostFill = Mathf.Max(0f, BoostFill - Time.deltaTime / boostDuration);
                if (BoostFill <= 0f) Boost = BoostPhase.Recharging;
                break;
            case BoostPhase.Recharging:
                BoostFill = Mathf.Min(1f, BoostFill + Time.deltaTime / boostRecharge);
                if (BoostFill >= 1f) Boost = BoostPhase.Ready;
                break;
        }
        UpdateDust();
    }

    // Шлейф пыли: 2D-спрайты (billboard) у основания предмета, только пока слайд и предмет реально едет.
    // Частицы живут в мировых координатах, поэтому остаются позади и видны всем, не только владельцу.
    void UpdateDust()
    {
        if (dust == null)
        {
            if (!Sliding || puffMaterial == null) return;
            var go = new GameObject("BoostDust");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.up * 0.03f;
            dust = go.AddComponent<ParticleSystem>();
            dust.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = dust.main;
            main.loop = true; main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = 0.5f;
            main.startSpeed = 0.15f;
            main.startColor = new Color(0.85f, 0.82f, 0.75f, 0.7f);
            main.gravityModifier = -0.05f;
            var sh = dust.shape; sh.shapeType = ParticleSystemShapeType.Box; sh.scale = new Vector3(0.2f, 0.02f, 0.2f);
            var col = dust.colorOverLifetime; col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                      new[] { new GradientAlphaKey(0.7f, 0), new GradientAlphaKey(0f, 1) });
            col.color = g;
            var sz = dust.sizeOverLifetime; sz.enabled = true;
            sz.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0, 0.5f, 1, 1.5f));
            go.GetComponent<ParticleSystemRenderer>().sharedMaterial = puffMaterial;
            go.layer = gameObject.layer;
        }
        float size = Mathf.Max(BodyHeight, CurrentProp != null ? CurrentProp.footRadius * 2f : 0.2f);
        var m = dust.main;
        m.startSize = new ParticleSystem.MinMaxCurve(0.35f * size + 0.05f, 0.7f * size + 0.1f);
        var s = dust.shape; s.scale = new Vector3(Mathf.Max(0.1f, size * 0.5f), 0.02f, Mathf.Max(0.1f, size * 0.5f));
        var em = dust.emission; em.rateOverTime = slideMoving ? 30f : 0f;
        if (slideMoving && !dust.isPlaying) dust.Play();
    }

    // Кольцевая шкала: сегментные дуги, повёрнутые вокруг центра иконки.
    void DrawBoostHud()
    {
        const float r = 34f, seg = 3f, thick = 7f;
        const int n = 72;
        var c = new Vector2(Screen.width - 70f, Screen.height - 70f);
        Color fillCol = Boost == BoostPhase.Ready ? new Color(0.35f, 0.9f, 0.4f) : Boost == BoostPhase.Active ? new Color(1f, 0.7f, 0.2f) : new Color(0.45f, 0.65f, 1f);
        var oldMatrix = GUI.matrix;
        for (int i = 0; i < n; i++)
        {
            float a = i * 360f / n;
            GUI.matrix = oldMatrix;
            GUIUtility.RotateAroundPivot(a, c);
            GUI.color = (i + 0.5f) / n <= BoostFill ? fillCol : new Color(1f, 1f, 1f, 0.18f);
            GUI.DrawTexture(new Rect(c.x - seg * 0.5f, c.y - r - thick * 0.5f, seg, thick), Texture2D.whiteTexture);
        }
        GUI.matrix = oldMatrix;
        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        float ir = 24f;
        GUI.DrawTexture(new Rect(c.x - ir, c.y - ir, ir * 2f, ir * 2f), CircleTex);   // круглая иконка
        GUI.color = Color.white;
        var st = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
        GUI.Label(new Rect(c.x - ir, c.y - ir, ir * 2f, ir * 2f), "Z", st);
    }

    static Texture2D circleTex;
    static Texture2D CircleTex
    {
        get
        {
            if (circleTex != null) return circleTex;
            const int s = 64;
            circleTex = new Texture2D(s, s, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(s / 2f, s / 2f));
                    circleTex.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(s / 2f - d)));
                }
            circleTex.Apply();
            return circleTex;
        }
    }

    // Приземление: звук (громче от скорости падения), приседание и лёгкий толчок камеры.
    void Land(float fallSpeed)
    {
        float k = Mathf.Clamp01(fallSpeed / 8f);
        if (k < 0.05f) return;
        if (sfx != null && landClip != null) sfx.PlayOneShot(landClip, 0.25f + 0.75f * k);
        squash = 1f - 0.18f * k;
        camKickVel = -2.2f * k;
    }

    // Визуальный squash-and-stretch по высоте тела: приседание перед отрывом, растяжение в воздухе, возврат плавно.
    void UpdateSquash()
    {
        float target = 1f;
        if (windup >= 0f) target = 0.8f;
        else if (!cc.isGrounded && !busy)
        {
            float v0 = Mathf.Sqrt(2f * -gravity * Mathf.Max(0.05f, JumpHeight));
            target = 1f + 0.15f * Mathf.Clamp01(Mathf.Abs(vy) / v0);
        }
        squash = Mathf.Lerp(squash, target, 1f - Mathf.Exp(-(windup >= 0f ? 30f : 18f) * Time.deltaTime));
        if (busy) return;
        float sy = squash, sxz = 1f / Mathf.Sqrt(sy);   // сохраняем объём
        if (CurrentProp == null)
        {
            stickman.localScale = new Vector3(stickBaseScale.x * sxz, stickBaseScale.y * sy, stickBaseScale.z * sxz);
            stickman.localPosition = new Vector3(0, StickmanHeight * 0.5f * sy, 0);
        }
        else
        {
            var t = CurrentProp.transform;
            t.localScale = new Vector3(propBaseScale.x * sxz, propBaseScale.y * sy, propBaseScale.z * sxz);
            t.localPosition = new Vector3(0, CurrentProp.height * 0.5f * sy, 0);
        }
    }

    // Процедурный глухой «тум» приземления, без внешних ассетов.
    static AudioClip MakeLandClip()
    {
        const int rate = 22050;
        int n = (int)(rate * 0.18f);
        var data = new float[n];
        var rng = new System.Random(7);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate, env = Mathf.Exp(-t * 28f);
            float tone = Mathf.Sin(2f * Mathf.PI * (90f - 200f * t) * t);
            float noise = (float)(rng.NextDouble() * 2 - 1) * Mathf.Exp(-t * 60f) * 0.5f;
            data[i] = (tone * 0.8f + noise) * env;
        }
        var clip = AudioClip.Create("land", n, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    // Прицел по центру экрана: первое непустое попадание луча; подходящий свободный предмет в пределах дистанции подсвечивается.
    void UpdateTarget()
    {
        Prop found = null;
        var auth = PossessionAuthority.Instance;
        // Человек целится камерой; бот без камеры — лучом из глаз по своему yaw/pitch (те же правила, тот же луч).
        bool fromCam = controlled && cam != null;
        Vector3 rayO = fromCam ? cam.transform.position : EyePosition;
        Vector3 rayD = fromCam ? cam.transform.forward : Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward;
        var hits = Physics.RaycastAll(rayO, rayD, 30f, ~0, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (var h in hits)
        {
            if (h.collider.GetComponentInParent<HiderPlayer>() == this) continue;
            var p = h.collider.GetComponentInParent<Prop>();
            if (p != null && (CurrentProp != null || p.IsFree) && auth != null &&
                Vector3.Distance(EyePosition, h.point) <= auth.pickDistance) found = p;
            break;   // первое чужое попадание заслоняет всё, что за ним
        }
        if (found != Target)
        {
            bool show = botInput == null;   // подсветка рисуется только человеку: у бота обводка выдавала бы его цель игроку
            if (Target != null && show) Target.SetHighlight(false, outlineMaterial);
            Target = found;
            if (Target != null && show) Target.SetHighlight(true, outlineMaterial);
        }
    }

    // То, что делает клавиша E.
    public void PressPossess()
    {
        if (busy) return;
        if (Target == null) return;   // обратной связи об отсутствии цели нет: её даёт сама подсветка
        var prop = Target;
        if (CurrentProp != null)
        {
            // Смена облика: остаёмся на месте и копируем вид образца, образец не резервируем и не трогаем.
            if (!PossessionAuthority.Instance.CheckMorph(this, prop, out var why)) { ShowToast(why); return; }
            prop.SetHighlight(false, outlineMaterial);
            Target = null;
            Morph(prop);
            return;
        }
        if (!PossessionAuthority.Instance.TryRequest(this, prop, out var mode, out var reason))
        {
            ShowToast(reason);
            return;
        }
        prop.SetHighlight(false, outlineMaterial);
        Target = null;
        if (mode == PossessMode.Suck) StartCoroutine(SuckRoutine(prop));
        else Complete(prop, PossessMode.Puff);
    }

    void Morph(Prop sample)
    {
        int oldMax = MaxHp, oldHp = Hp;
        PropTier oldTier = CurrentProp.tier;
        bool sameModel = CurrentProp.ModelId == sample.ModelId;   // идентичная модель: HP не пересчитываем (possession.md, «Исключение»)
        CurrentProp.CopyAppearanceFrom(sample);
        propBaseScale = CurrentProp.transform.localScale;
        ApplyBodyShape(CurrentProp);
        MaxHp = CurrentProp.MaxHp;
        Hp = sameModel ? oldHp : HpConversion.Convert(oldHp, oldMax, MaxHp, oldTier, CurrentProp.tier);
        NextRepossessTime = Time.time + PossessionAuthority.Instance.repossessCooldown;
        float size = Mathf.Max(CurrentProp.height, CurrentProp.footRadius * 2f);
        PuffEffect.Spawn(transform.position + Vector3.up * (CurrentProp.height * 0.5f), size, puffMaterial);
        camDist = float.NaN;
    }

    void ApplyBodyShape(Prop prop)
    {
        cc.enabled = false;
        // Радиус не больше половины высоты и не меньше 2·skinWidth: капсула не выше видимой модели (без фиксированного минимума 0,1).
        float radius = Mathf.Clamp(Mathf.Min(prop.footRadius, prop.height * 0.5f), cc.skinWidth * 2f, 0.5f);
        cc.radius = radius;
        cc.height = Mathf.Max(prop.height, radius * 2f);
        cc.center = new Vector3(0, cc.height * 0.5f + cc.skinWidth, 0);   // компенсация skinWidth, см. ApplyStickmanShape
        cc.stepOffset = Mathf.Min(0.3f, cc.height * 0.5f);   // иначе Unity ругается и отключает контроллер у мелких предметов
        cc.enabled = true;
        foreach (var c in prop.Colliders) Physics.IgnoreCollision(cc, c, true);
    }

    // Автопревращение опоздавшего: ближайший свободный предмет, мгновенно, «пуфф», камера без плавности.
    public void AutoPossess()
    {
        var prop = PossessionAuthority.Instance.ReserveNearestFree(this);
        if (prop != null) Complete(prop, PossessMode.Puff);
    }

    IEnumerator SuckRoutine(Prop prop)
    {
        busy = true;
        cc.enabled = false;
        Vector3 from = transform.position, to = FootPos(prop);
        Vector3 scale = stickBaseScale;
        for (float t = 0; t < suckDuration; t += Time.deltaTime)
        {
            float k = t / suckDuration;
            transform.position = Vector3.Lerp(from, to, k * k);
            stickman.localScale = scale * (1f - k);
            yield return null;
        }
        stickman.localScale = scale;
        Complete(prop, PossessMode.Suck);
        busy = false;
    }

    static Vector3 FootPos(Prop p) => p.transform.position - Vector3.up * (p.height * 0.5f);

    // Первое вселение (старт подготовки или автовселение): игрок переходит к зарезервированному экземпляру.
    void Complete(Prop prop, PossessMode mode)
    {
        Vector3 foot = FootPos(prop);
        cc.enabled = false;
        transform.position = foot;
        transform.rotation = Quaternion.identity;
        propYaw = 0f;
        prop.AttachTo(transform);
        propBaseScale = prop.transform.localScale;
        prop.SetLayerRecursive(OwnBodyLayer);
        ApplyBodyShape(prop);
        stickman.gameObject.SetActive(false);
        vy = 0f;

        MaxHp = prop.MaxHp;
        Hp = MaxHp;
        CurrentProp = prop;
        NextRepossessTime = Time.time + PossessionAuthority.Instance.repossessCooldown;   // кулдаун первой смены на охоте отсчитывается отсюда

        if (mode == PossessMode.Puff)
        {
            float size = Mathf.Max(prop.height, prop.footRadius * 2f);
            PuffEffect.Spawn(prop.transform.position, size, puffMaterial);
            camDist = float.NaN;   // мгновенный переход камеры, без сглаживания
        }
    }

    void LateUpdate()
    {
        if (!controlled || cam == null || Caught) return;
        Vector3 eye = EyePosition;
        Quaternion rot = Quaternion.Euler(pitch, yaw, 0);
        float want = thirdPerson ? 1f + (CurrentProp == null ? StickmanHeight : CurrentProp.height * 1.5f) : 0f;
        camDist = float.IsNaN(camDist) ? want : Mathf.Lerp(camDist, want, 1f - Mathf.Exp(-14f * Time.deltaTime));
        // пружинный толчок камеры при приземлении
        camKickVel += (-camKick * 180f - camKickVel * 18f) * Time.deltaTime;
        camKick += camKickVel * Time.deltaTime;
        cam.transform.SetPositionAndRotation(eye - rot * Vector3.forward * ClampCamDistance(eye, rot, camDist) + Vector3.up * camKick * BodyHeight, rot);
    }

    public void SetControlled(bool on)
    {
        controlled = on;
        if (!on && Target != null) { Target.SetHighlight(false, outlineMaterial); Target = null; }
        if (on) Cursor.lockState = CursorLockMode.Locked;
    }

    // --- Пинок (RamKickAuthority): настоящий Rigidbody предмета, потом «в отключке» и самовыравнивание ---

    Coroutine stunCo;
    HpBar hpBar;
    Rigidbody propRb;
    StunStars stars;
    Vector3 preKickFoot;

    public void BeginKicked(Vector3 launch, Vector3 spin, RamKickAuthority a)
    {
        if (Caught || CurrentProp == null || Stun != StunPhase.None) return;
        Detected?.Invoke(DetectKind.Kick);
        stunCo = StartCoroutine(StunRoutine(launch, spin, a));
    }

    IEnumerator StunRoutine(Vector3 launch, Vector3 spin, RamKickAuthority a)
    {
        var prop = CurrentProp;
        busy = true; Stun = StunPhase.Flight; slideMoving = false;
        if (Target != null) { Target.SetHighlight(false, outlineMaterial); Target = null; }
        windup = -1f; squash = 1f; vy = 0f;
        preKickFoot = transform.position;
        cc.enabled = false;

        // Фаза 1: полёт/кувырок. Rigidbody на самом предмете (он ребёнок корня, корень стоит); управления нет, хаотичность даёт физика.
        propRb = prop.gameObject.AddComponent<Rigidbody>();
        propRb.mass = RamKickAuthority.MassOf(prop.tier);
        propRb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        propRb.angularDamping = 0.8f;   // иначе банка катится бесконечно (в PhysX нет сопротивления качению)
        propRb.linearDamping = 0.1f;
        propRb.maxAngularVelocity = 20f;
        var mat = new PhysicsMaterial { bounciness = 0.35f, dynamicFriction = 0.5f, staticFriction = 0.6f, bounceCombine = PhysicsMaterialCombine.Maximum };
        bool round = false;
        foreach (var c in prop.Colliders)
            if (c is CapsuleCollider || c is SphereCollider || prop.ModelId == "Can" || prop.ModelId == "Basket") round = true;
        if (round) { mat.dynamicFriction = a.roundFriction; mat.staticFriction = a.roundFriction; mat.frictionCombine = PhysicsMaterialCombine.Maximum; }
        foreach (var c in prop.Colliders) if (c != null) c.material = mat;
        propRb.AddForce(launch * propRb.mass, ForceMode.Impulse);
        propRb.AddTorque(spin, ForceMode.VelocityChange);
        var probe = prop.gameObject.AddComponent<KickContactProbe>();

        // Фаза 1 заканчивается, только когда предмет реально осел: низкие линейная и угловая скорости удерживаются
        // settleHold секунд при контакте с опорой под телом (либо Rigidbody уснул на опоре). flightMaxTime — лишь страховка.
        float t = 0f, calm = 0f;
        bool forced = false;
        while (true)
        {
            yield return null;
            t += Time.deltaTime;
            if (prop.transform.position.y < -2f || t >= a.flightMaxTime) { forced = true; break; }
            // PhysX не имеет сопротивления качению: после касания земли добавляем торможение, иначе банка/корзина катятся десятки метров
            if (t > 0.4f && probe.Supported()) { propRb.linearDamping = 1.5f; propRb.angularDamping = round ? a.roundAngularDamping : 3f; }
            bool slow = propRb.linearVelocity.sqrMagnitude < a.settleSpeed * a.settleSpeed && propRb.angularVelocity.sqrMagnitude < a.settleAngular * a.settleAngular;
            if (t > 0.2f && probe.Supported() && (slow || propRb.IsSleeping())) { calm += Time.deltaTime; if (calm >= a.settleHold) break; }
            else calm = 0f;
        }
        Destroy(probe);

        // Страховка сработала (физика застряла или улетел за карту): не замораживаем в воздухе, а ставим на ближайшую опору.
        if (forced)
        {
            float r0f = Mathf.Clamp(Mathf.Min(prop.footRadius, prop.height * 0.5f), cc.skinWidth * 2f, 0.5f);
            Vector3 stand = RamKickAuthority.FindStandPoint(prop.transform.position, r0f, prop.height, preKickFoot);
            prop.transform.position = stand + Vector3.up * (prop.height * 0.5f);
        }

        // Фаза 2: «в отключке» — лёгкое кинематическое состояние до конца таймера оглушения, звёзды над предметом.
        // (Выражение лица «оглушён» не делаем: лиц ещё нет, systems/faces.md не реализован.)
        propRb.linearVelocity = Vector3.zero; propRb.angularVelocity = Vector3.zero;
        propRb.isKinematic = true;
        Stun = StunPhase.Out;
        stars = StunStars.Create(prop);
        float outEnd = Mathf.Max(a.stunDuration, t + a.minOutDuration);
        while (t < outEnd) { yield return null; t += Time.deltaTime; }

        // Фаза 3: самовыравнивание Slerp к вертикали + коррекция позиции (ближайшая свободная точка, провал/далеко — прежнее место).
        Stun = StunPhase.Realign;
        if (stars != null) Destroy(stars.gameObject);
        stars = null;
        float radius = Mathf.Clamp(Mathf.Min(prop.footRadius, prop.height * 0.5f), cc.skinWidth * 2f, 0.5f);
        Vector3 c0 = prop.transform.position;
        Vector3 foot = RamKickAuthority.FindStandPoint(c0, radius, prop.height, preKickFoot);
        Vector3 center = foot + Vector3.up * (prop.height * 0.5f);
        Quaternion upright = transform.rotation * Quaternion.Euler(0f, propYaw, 0f);
        Quaternion r0 = prop.transform.rotation;
        for (float u = 0f; u < a.realignDuration; u += Time.deltaTime)
        {
            float k = Mathf.SmoothStep(0f, 1f, u / a.realignDuration);
            prop.transform.SetPositionAndRotation(Vector3.Lerp(c0, center, k), Quaternion.Slerp(r0, upright, k));
            yield return null;
        }
        FinishStun(foot);
    }

    // Возврат управления: корень встаёт на проверенную точку, предмет снова ребёнок корня в исходной ориентации.
    void FinishStun(Vector3 foot)
    {
        var prop = CurrentProp;
        if (propRb != null) Destroy(propRb);
        propRb = null;
        if (stars != null) Destroy(stars.gameObject);
        stars = null;
        transform.position = foot;
        prop.transform.localPosition = new Vector3(0f, prop.height * 0.5f, 0f);
        prop.transform.localRotation = Quaternion.Euler(0f, propYaw, 0f);
        cc.enabled = true;
        vy = 0f;
        Stun = StunPhase.None;
        busy = false;
        stunCo = null;
    }

    // Поймали во время оглушения: прерываем последовательность и убираем физику/звёзды.
    void AbortStun()
    {
        if (stunCo != null) StopCoroutine(stunCo);
        if (CurrentProp != null)
        {
            Vector3 center = CurrentProp.transform.position;
            var pr = CurrentProp.GetComponent<KickContactProbe>(); if (pr != null) Destroy(pr);
            if (propRb != null) Destroy(propRb);
            propRb = null;
            CurrentProp.transform.localRotation = Quaternion.identity;
            transform.position = center - Vector3.up * CurrentProp.height * 0.5f;
            CurrentProp.transform.localPosition = new Vector3(0f, CurrentProp.height * 0.5f, 0f);
        }
        if (stars != null) Destroy(stars.gameObject);
        stars = null;
        Stun = StunPhase.None;
        stunCo = null;
    }

    // --- Бой (вызывает CombatAuthority; на хосте при переходе на сеть) ---

    // Урон от выстрела; true, если HP дошло до 0.
    public bool ApplyHit(int damage)
    {
        if (Caught) return false;
        Hp = Mathf.Max(0, Hp - damage);
        hitFlashUntil = Time.time + 0.25f;
        if (hpBar == null) hpBar = HpBar.Attach(this);
        hpBar.Show(Hp, MaxHp);
        CombatAudio.PlayAt(CombatAudio.Hit, BodyCenter, 1f, controlled);
        Detected?.Invoke(DetectKind.Hit);
        return Hp <= 0;
    }

    // near-miss: свист слышит только этот прячущийся (2D-звук); игровых последствий нет (буст только от Z, hunter-combat.md).
    public void OnNearMiss(float hunterDistance)
    {
        if (Caught) return;
        Detected?.Invoke(DetectKind.NearMiss);
        if (controlled && sfx != null) sfx.PlayOneShot(CombatAudio.Whistle, CombatAudio.WhistleVolume(hunterDistance));
    }

    // Поимка: предмет исчезает с «пуфф» -> камера пролетает к поймавшему -> возрождение в команде охотников.
    public void BeginCatch(HunterPlayer by)
    {
        if (Caught) return;
        Caught = true;
        if (hpBar != null) Destroy(hpBar.gameObject);
        if (Stun != StunPhase.None) AbortStun();
        StartCoroutine(CaughtRoutine(by));
    }

    IEnumerator CaughtRoutine(HunterPlayer by)
    {
        busy = true;
        if (Target != null) { Target.SetHighlight(false, outlineMaterial); Target = null; }
        Vector3 center = BodyCenter;
        float size = Mathf.Max(BodyHeight, CurrentProp != null ? CurrentProp.footRadius * 2f : 0.2f);
        PuffEffect.Spawn(center, size, puffMaterial);
        CombatAudio.PlayAt(CombatAudio.Puff, center, 1f, controlled);
        if (CurrentProp != null) CurrentProp.gameObject.SetActive(false);   // исчезает мгновенно, без физики разрушения
        stickman.gameObject.SetActive(false);
        cc.enabled = false;

        if (controlled && cam != null)
        {
            cam.transform.SetParent(null, true);
            Vector3 p0 = cam.transform.position; Quaternion r0 = cam.transform.rotation;
            const float fly = 1.0f, hold = 0.4f;
            for (float t = 0; t < fly + hold && by != null; t += Time.deltaTime)
            {
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / fly));
                Vector3 head = by.EyePosition;
                Vector3 p1 = head + by.transform.forward * 2.2f + Vector3.up * 0.1f;
                cam.transform.SetPositionAndRotation(Vector3.Lerp(p0, p1, k),
                    Quaternion.Slerp(r0, Quaternion.LookRotation(head - p1), k));
                yield return null;
            }
        }
        else yield return new WaitForSeconds(1.4f);

        // Бот-охотник отложен (bots.md): пойманный бот просто выбывает, охотника из него не создаём.
        if (botInput == null && CombatAuthority.Instance != null) CombatAuthority.Instance.SpawnHunter(controlled, cam);
        Destroy(gameObject);
    }

    // Камера от третьего лица не заходит в стены: SphereCast от точки обзора назад к желаемой позиции,
    // при препятствии (кроме собственного тела) камера подтягивается к игроку.
    const float CamRadius = 0.2f, CamMargin = 0.05f;
    float ClampCamDistance(Vector3 eye, Quaternion rot, float dist)
    {
        if (dist <= 0.01f) return dist;
        Vector3 back = rot * Vector3.back;

        // Вплотную к стене SphereCast не видит коллайдер, в котором сфера уже стартует (distance = 0).
        // Поэтому сначала ищем ближайшую поверхность вокруг точки обзора и сжимаем радиус каста под неё.
        float radius = CamRadius;
        foreach (var c in Physics.OverlapSphere(eye, CamRadius, ~0, QueryTriggerInteraction.Ignore))
        {
            if (c.transform.IsChildOf(transform)) continue;
            if (c.GetComponentInParent<HiderPlayer>() == this) continue;
            float d = Vector3.Distance(eye, c.ClosestPoint(eye));
            if (d <= 0.001f) return 0f;   // глаз внутри геометрии: камера остаётся на глазах
            radius = Mathf.Min(radius, d * 0.9f);
        }

        var hits = Physics.SphereCastAll(eye, radius, back, dist, ~0, QueryTriggerInteraction.Ignore);
        float best = dist;
        foreach (var h in hits)
        {
            if (h.distance <= 0f) continue;
            if (h.collider.GetComponentInParent<HiderPlayer>() == this) continue;
            best = Mathf.Min(best, h.distance - CamMargin);
        }
        // страховка: тонкий луч по оси, на случай если каст сферой стартует на стене
        foreach (var h in Physics.RaycastAll(eye, back, dist, ~0, QueryTriggerInteraction.Ignore))
        {
            if (h.collider.GetComponentInParent<HiderPlayer>() == this) continue;
            best = Mathf.Min(best, h.distance - CamMargin);
        }
        return Mathf.Max(0f, best);
    }

    void ShowToast(string s) { Toast = s; toastUntil = Time.time + 1.5f; }

    void OnGUI()
    {
        if (!controlled || Caught) return;
        if (Time.time < hitFlashUntil)
        {
            GUI.color = new Color(1f, 0.1f, 0.1f, 0.35f * (hitFlashUntil - Time.time) / 0.25f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
        }
        var style = new GUIStyle(GUI.skin.label) { fontSize = 16, normal = { textColor = Color.white } };
        var round = RoundState.Instance;
        string phase = round == null ? "" : round.Phase == RoundPhase.Prep ? $"Подготовка: {round.PrepRemaining:0} с" : "Охота";
        float cd = Mathf.Max(0, NextRepossessTime - Time.time);
        string cdText = CurrentProp == null ? "" : cd > 0 ? $"   Смена облика: {cd:0.0} с" : "   Смена облика: готово";
        GUI.Label(new Rect(12, 8, 900, 28), $"{phase}   HP: {Hp}/{MaxHp}{cdText}", style);
        if (Target != null) GUI.Label(new Rect(Screen.width / 2f - 60, Screen.height / 2f + 16, 200, 26), "[E] вселиться", style);
        if (Toast.Length > 0) GUI.Label(new Rect(Screen.width / 2f - 100, Screen.height / 2f + 44, 400, 26), Toast, style);
        GUI.color = Color.white;
        if (CurrentProp != null) DrawBoostHud();
        GUI.color = Color.white;
        GUI.DrawTexture(new Rect(Screen.width / 2f - 2, Screen.height / 2f - 2, 4, 4), Texture2D.whiteTexture);
    }
}
