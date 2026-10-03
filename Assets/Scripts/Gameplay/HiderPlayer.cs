using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Локальный прячущийся: стикмен до вселения, потом предмет. Ввод и камера клиентские,
// решение «можно ли вселиться» принимает PossessionAuthority.
[RequireComponent(typeof(CharacterController))]
public class HiderPlayer : MonoBehaviour
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

    public bool Caught { get; private set; }
    public bool IsAlive => !Caught;
    public Vector3 BodyCenter => transform.position + Vector3.up * BodyHeight * 0.5f;
    public bool Boosted => Time.time < boostUntil;
    public Prop CurrentProp { get; private set; }
    public Prop Target { get; private set; }
    public int Hp { get; private set; }
    public int MaxHp { get; private set; }
    public float NextRepossessTime { get; private set; }
    public string Toast { get; private set; } = "";

    public Vector3 EyePosition => transform.position + Vector3.up * EyeHeight;
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
    float boostUntil, boostMult = 1f, hitFlashUntil;
    Vector3 propBaseScale = Vector3.one, stickBaseScale = Vector3.one;
    AudioSource sfx;
    AudioClip landClip;

    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }

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
        if (kb != null && kb.vKey.wasPressedThisFrame) thirdPerson = !thirdPerson;

        if (!busy)
        {
            Move(kb);
            if (controlled) UpdateTarget();
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
        Vector3 move = look * input.normalized * walkSpeed * (Boosted ? boostMult : 1f);
        bool grounded = cc.isGrounded;
        if (grounded && wasAirborne) Land(-vy);
        if (grounded && windup < 0f && kb != null && kb.spaceKey.wasPressedThisFrame) windup = jumpWindup;
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
        var hits = Physics.RaycastAll(cam.transform.position, cam.transform.forward, 30f, ~0, QueryTriggerInteraction.Ignore);
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
            if (Target != null) Target.SetHighlight(false, outlineMaterial);
            Target = found;
            if (Target != null) Target.SetHighlight(true, outlineMaterial);
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
        CurrentProp.CopyAppearanceFrom(sample);
        propBaseScale = CurrentProp.transform.localScale;
        ApplyBodyShape(CurrentProp);
        MaxHp = CurrentProp.MaxHp;
        Hp = HpConversion.Convert(oldHp, oldMax, MaxHp, oldTier, CurrentProp.tier);
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
        cam.transform.SetPositionAndRotation(eye - rot * Vector3.forward * camDist + Vector3.up * camKick * BodyHeight, rot);
        // от первого лица собственное тело не рисуется, от третьего (V) рисуется
        int own = OwnBodyLayer;
        if (own >= 0) cam.cullingMask = thirdPerson ? ~0 : ~(1 << own);
    }

    public void SetControlled(bool on)
    {
        controlled = on;
        if (!on && Target != null) { Target.SetHighlight(false, outlineMaterial); Target = null; }
        if (on) Cursor.lockState = CursorLockMode.Locked;
    }

    // --- Бой (вызывает CombatAuthority; на хосте при переходе на сеть) ---

    // Урон от выстрела; true, если HP дошло до 0.
    public bool ApplyHit(int damage)
    {
        if (Caught) return false;
        Hp = Mathf.Max(0, Hp - damage);
        hitFlashUntil = Time.time + 0.25f;
        CombatAudio.PlayAt(CombatAudio.Hit, BodyCenter, 1f);
        return Hp <= 0;
    }

    // near-miss: свист слышит только этот прячущийся (2D-звук), и он же получает буст скорости.
    public void OnNearMiss(float hunterDistance, float boostSeconds, float boostMultiplier)
    {
        if (Caught) return;
        boostUntil = Time.time + boostSeconds;
        boostMult = boostMultiplier;
        if (controlled && sfx != null) sfx.PlayOneShot(CombatAudio.Whistle, CombatAudio.WhistleVolume(hunterDistance));
    }

    // Поимка: предмет исчезает с «пуфф» -> камера пролетает к поймавшему -> возрождение в команде охотников.
    public void BeginCatch(HunterPlayer by)
    {
        if (Caught) return;
        Caught = true;
        StartCoroutine(CaughtRoutine(by));
    }

    IEnumerator CaughtRoutine(HunterPlayer by)
    {
        busy = true;
        if (Target != null) { Target.SetHighlight(false, outlineMaterial); Target = null; }
        Vector3 center = BodyCenter;
        float size = Mathf.Max(BodyHeight, CurrentProp != null ? CurrentProp.footRadius * 2f : 0.2f);
        PuffEffect.Spawn(center, size, puffMaterial);
        CombatAudio.PlayAt(CombatAudio.Puff, center, 1f);
        if (CurrentProp != null) CurrentProp.gameObject.SetActive(false);   // исчезает мгновенно, без физики разрушения
        stickman.gameObject.SetActive(false);
        cc.enabled = false;

        if (controlled && cam != null)
        {
            cam.cullingMask = ~0;
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

        if (CombatAuthority.Instance != null) CombatAuthority.Instance.SpawnHunter(controlled, cam);
        Destroy(gameObject);
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
        GUI.Label(new Rect(12, 8, 900, 28), $"{phase}   HP: {Hp}/{MaxHp}{cdText}{(Boosted ? "   БУСТ" : "")}", style);
        if (Target != null) GUI.Label(new Rect(Screen.width / 2f - 60, Screen.height / 2f + 16, 200, 26), "[E] вселиться", style);
        if (Toast.Length > 0) GUI.Label(new Rect(Screen.width / 2f - 100, Screen.height / 2f + 44, 400, 26), Toast, style);
        GUI.color = Color.white;
        GUI.DrawTexture(new Rect(Screen.width / 2f - 2, Screen.height / 2f - 2, 4, 4), Texture2D.whiteTexture);
    }
}
