using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Охотник (стикмен 1,8 м) с дробовиком. Ввод и камера клиентские; выстрел решает CombatAuthority.
[RequireComponent(typeof(CharacterController))]
public class HunterPlayer : MonoBehaviour, IOwnBodyViewer
{
    public static readonly List<HunterPlayer> All = new List<HunterPlayer>();

    public Camera cam;
    public bool controlled;
    public float walkSpeed = 4f;          // скорость охотника в документах не задана, временно как у прячущегося
    public float gravity = -20f;
    // Прыжок: та же формула, что у прячущегося (HiderPlayer.JumpHeightFor, movement-and-camera.md), но числа вдвое меньше
    // (решение владельца 2026-10-10: 1,75 × 1,8 = 3,15 м было слишком высоко): 0,875 × 1,8 = 1,575 м.
    public float hunterJumpMultiplier = 0.875f;
    public float hunterMinJumpHeight = 1.025f;
    public float jumpWindup = 0.1f;
    public float JumpHeight => HiderPlayer.JumpHeightFor(height, hunterJumpMultiplier, hunterMinJumpHeight);
    public float mouseSensitivity = 0.1f;
    public float height = 1.8f;
    public float stumbleDuration = 0.7f;

    public float NextShotTime;
    public float NextKickTime;            // пинок: отдельный кулдаун от дробовика
    public float KnockImmuneUntil;        // окно неуязвимости к повторному тарану после подъёма (привязано к жертве)
    public bool Knocked => ragdoll != null;
    public Vector3 EyePosition => transform.position + Vector3.up * height * 0.94f;
    public bool IsBlocked => RoundState.Instance != null && RoundState.Instance.Phase == RoundPhase.Prep;
    public string LastShot { get; private set; } = "";

    CharacterController cc;
    [SerializeField] Transform pivot, gun;
    [SerializeField] GameObject viewModel;
    float yaw, pitch, vy, stumbleT = -1f, lastFire = -10f, kickT = -1f;
    float windup = -1f, squash = 1f, camKick, camKickVel;
    bool wasAirborne;
    AudioClip landClip;
    HunterRagdoll ragdoll;
    Material bodyMaterial;
    const float KickAnimTime = 0.3f;
    AudioSource sfx;
    Material tracerMat;

    static int OwnBodyLayer => LayerMask.NameToLayer("OwnBody");

    void OnEnable() { All.Add(this); OwnBodyCulling.Register(this); }
    void OnDisable() { All.Remove(this); OwnBodyCulling.Unregister(this); }

    // Своё тело (капсула и дробовик на нём) скрывается только от камеры владельца; во время ragdoll камера на голове, тело-капсула неактивна.
    public Camera ViewCamera => controlled ? cam : null;
    public bool HideOwnBody => controlled && !Knocked && pivot != null;
    public void CollectOwnRenderers(List<Renderer> buffer) { pivot.GetComponentsInChildren<Renderer>(false, buffer); }

    // Исходный масштаб дробовика на теле (из Create): при возврате из руки ragdoll берётся он, а не производная от масштаба руки.
    public Vector3 GunHomeScale { get; private set; }

    // Рантайм-сборка: капсула-стикмен, CharacterController, дробовик на теле и «вьюмодель» для первого лица.
    public static HunterPlayer Create(Vector3 pos, float yawDeg, Camera cam, bool controlled, Material bodyMat, Material gunMat)
    {
        var go = new GameObject("Hunter");
        go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0, yawDeg, 0));
        var cc = go.AddComponent<CharacterController>();
        cc.skinWidth = 0.01f; cc.radius = 0.25f; cc.height = 1.8f; cc.center = new Vector3(0, 0.9f + cc.skinWidth, 0);
        var h = go.AddComponent<HunterPlayer>();
        h.cam = cam; h.yaw = yawDeg;

        var pv = new GameObject("BodyPivot").transform;   // шарнир у ног: вокруг него «спотыкается» тело
        pv.SetParent(go.transform, false);
        var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        body.name = "Body";
        Kill(body.GetComponent<Collider>());
        body.transform.SetParent(pv, false);
        body.transform.localPosition = new Vector3(0, 0.9f, 0);
        body.transform.localScale = new Vector3(0.5f, 0.9f, 0.5f);
        if (bodyMat != null) body.GetComponent<Renderer>().sharedMaterial = bodyMat;
        h.bodyMaterial = bodyMat;
        var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
        g.name = "Shotgun";
        Kill(g.GetComponent<Collider>());
        g.transform.SetParent(pv, false);
        g.transform.localPosition = new Vector3(0.28f, 1.15f, 0.4f);
        g.transform.localScale = new Vector3(0.08f, 0.1f, 0.8f);
        if (gunMat != null) g.GetComponent<Renderer>().sharedMaterial = gunMat;

        var vm = GameObject.CreatePrimitive(PrimitiveType.Cube);
        vm.name = "ShotgunViewModel";
        Kill(vm.GetComponent<Collider>());
        vm.transform.localScale = new Vector3(0.08f, 0.1f, 0.7f);
        if (gunMat != null) vm.GetComponent<Renderer>().sharedMaterial = gunMat;

        h.pivot = pv; h.gun = g.transform; h.viewModel = vm;
        h.GunHomeScale = g.transform.localScale;
        h.SetControlled(controlled);
        return h;
    }

    static void Kill(Object o) { if (Application.isPlaying) Destroy(o); else DestroyImmediate(o); }   // Create вызывается и из редактора

    void OnDestroy() { if (viewModel != null) Destroy(viewModel); }

    void Awake() { cc = GetComponent<CharacterController>(); SetupPushImmunity(); }

    void Start()
    {
        sfx = gameObject.AddComponent<AudioSource>();
        sfx.spatialBlend = 0f;
        landClip = HiderPlayer.MakeLandClip();
        tracerMat = new Material(Shader.Find("Sprites/Default"));
        yaw = transform.eulerAngles.y;
        if (GunHomeScale == Vector3.zero && gun != null) GunHomeScale = gun.localScale;   // охотник из сцены, не из Create
        ApplyControlled();
    }

    // Прицеливание в точку (боты, тесты): без управления камерой.
    public void AimAt(Vector3 point)
    {
        Vector3 d = (point - EyePosition).normalized;
        var e = Quaternion.LookRotation(d).eulerAngles;
        yaw = e.y; pitch = e.x > 180f ? e.x - 360f : e.x;
        transform.rotation = Quaternion.Euler(0, yaw, 0);
    }

    public void SetControlled(bool on)
    {
        controlled = on;
        ApplyControlled();
        if (on) { Cursor.lockState = CursorLockMode.Locked; yaw = transform.eulerAngles.y; pitch = 0f; }
    }

    // Вьюмодель только у управляемого (скрытие своего тела от своей камеры: OwnBodyCulling).
    void ApplyControlled()
    {
        if (pivot == null) return;
        viewModel.SetActive(controlled && !Knocked);
    }

    void Update()
    {
        if (Knocked) return;   // под ragdoll: ввода нет, тело ведёт физика
        var kb = controlled ? Keyboard.current : null;
        var mouse = controlled ? Mouse.current : null;
        if (mouse != null && Cursor.lockState == CursorLockMode.Locked)
        {
            Vector2 d = mouse.delta.ReadValue() * mouseSensitivity;
            yaw += d.x;
            pitch = Mathf.Clamp(pitch - d.y, -80f, 80f);
        }
        if (controlled) transform.rotation = Quaternion.Euler(0, yaw, 0);
        Move(kb);
        if (mouse != null && mouse.leftButton.wasPressedThisFrame) Fire();
        if (mouse != null && mouse.rightButton.wasPressedThisFrame) Kick();
        UpdateStumble();
        UpdateSquash();
    }

    // Коллайдер предмета на теле прячущегося (слой OwnBody; бокс/меш с собственной ориентацией) не должен выталкивать охотника:
    // CharacterController охотника при Move выдавливается из любого пересекающего его коллайдера (Physics.IgnoreCollision на
    // это не влияет, замер: охотника сдвигало до 6 м). Корень охотника на слое Hunter, пара Hunter-OwnBody не сталкивается.
    // Лучи выстрела и прицела слои не фильтруют, по предмету попадание работает как раньше. Капсулы CC друг друга не толкают
    // (замер), прячущегося охотник по-прежнему не пропускает. Толкать охотника будет только таран (ram-kick.md), отдельным кодом.
    static int HunterLayer => LayerMask.NameToLayer("Hunter");
    void SetupPushImmunity()
    {
        int hl = HunterLayer, own = OwnBodyLayer;
        if (hl < 0 || own < 0) { Debug.LogWarning("Слой Hunter/OwnBody не создан: охотника могут толкать (PropHunt/Build Supermarket Map создаёт слои)"); return; }
        gameObject.layer = hl;
        Physics.IgnoreLayerCollision(hl, own, true);
    }

    void Move(Keyboard kb)
    {
        Vector3 input = Vector3.zero;
        if (kb != null && !IsBlocked)
        {
            if (kb.wKey.isPressed) input.z += 1;
            if (kb.sKey.isPressed) input.z -= 1;
            if (kb.dKey.isPressed) input.x += 1;
            if (kb.aKey.isPressed) input.x -= 1;
        }
        Vector3 move = transform.rotation * input.normalized * walkSpeed;
        bool grounded = cc.isGrounded;
        if (grounded && wasAirborne) Land(-vy);
        bool jumpPressed = kb != null && !IsBlocked && kb.spaceKey.wasPressedThisFrame;
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
        }
        if (grounded) vy = -1f; else vy += gravity * Time.deltaTime;
        move.y = vy;
        var flags = cc.Move(move * Time.deltaTime);
        if ((flags & CollisionFlags.Above) != 0 && vy > 0f) vy = 0f;   // голова в потолок/полку: подъём гасится
        wasAirborne = !cc.isGrounded;
    }

    // Приземление: звук (громче от скорости падения) и толчок камеры; приседание модели и камеры идёт через squash.
    void Land(float fallSpeed)
    {
        float k = Mathf.Clamp01(fallSpeed / 8f);
        if (k < 0.05f) return;
        if (sfx != null && landClip != null) sfx.PlayOneShot(landClip, 0.25f + 0.75f * k);
        squash = 1f - 0.18f * k;
        camKickVel = -2.2f * k;
    }

    // Приседание перед отрывом и растяжение в воздухе. Свою модель владелец не видит (OwnBodyCulling), её видят остальные;
    // у владельца то же самое выражается опусканием камеры в LateUpdate.
    void UpdateSquash()
    {
        float target = 1f;
        if (windup >= 0f) target = 0.8f;
        else if (!cc.isGrounded)
        {
            float v0 = Mathf.Sqrt(2f * -gravity * Mathf.Max(0.05f, JumpHeight));
            target = 1f + 0.15f * Mathf.Clamp01(Mathf.Abs(vy) / v0);
        }
        squash = Mathf.Lerp(squash, target, 1f - Mathf.Exp(-(windup >= 0f ? 30f : 18f) * Time.deltaTime));
        float sxz = 1f / Mathf.Sqrt(squash);   // сохраняем объём
        pivot.localScale = new Vector3(sxz, squash, sxz);
    }

    // Выстрел: решает CombatAuthority. Звук/вспышка/след — у всех; на промахе «телл» (звук + спотыкание).
    public void Fire()
    {
        var auth = CombatAuthority.Instance;
        if (auth == null || IsBlocked) return;
        Vector3 origin = cam != null && controlled ? cam.transform.position : EyePosition;
        Vector3 fwd = cam != null && controlled ? cam.transform.forward : Quaternion.Euler(pitch, yaw, 0) * Vector3.forward;
        var res = auth.TryFire(this, origin, fwd);
        if (!res.fired) return;
        lastFire = Time.time;
        CombatAudio.PlayAt(CombatAudio.Shot, origin, 1f, controlled);
        StartCoroutine(Tracers(origin, res.pelletEnds));
        if (res.hit) LastShot = $"попадание: {res.damage} ур. с {res.distance:0.0} м{(res.caught ? ", ПОЙМАН" : "")}";
        else
        {
            LastShot = res.nearMisses > 0 ? $"промах (рядом свистнуло: {res.nearMisses})" : "промах";
            Stumble();
        }
#if UNITY_EDITOR
        Debug.Log($"[hunter shot] {LastShot}");   // только консоль редактора: охотник не должен видеть near-miss (hunter-combat.md)
#endif
    }

    // Пинок (ПКМ): решает RamKickAuthority. Промах даёт тот же телл, что промах дробовика (звук + спотыкание).
    public void Kick()
    {
        var auth = RamKickAuthority.Instance;
        Vector3 origin = cam != null && controlled ? cam.transform.position : EyePosition;
        Vector3 fwd = cam != null && controlled ? cam.transform.forward : Quaternion.Euler(pitch, yaw, 0) * Vector3.forward;
        var res = auth.TryKick(this);
        if (!res.fired) return;
        kickT = 0f;
        if (res.hit) CombatAudio.PlayAt(CombatAudio.Hit, origin + fwd, 1f, controlled);
        else Stumble();
#if UNITY_EDITOR
        Debug.Log($"[hunter kick] {(res.hit ? "попал" : "промах")}");
#endif
    }

    // Таран: ragdoll на упрощённом риге. Импульс задаётся явно, телу и камере; CharacterController выключается.
    public void Knock(Vector3 launch, Vector3 spin, RamKickAuthority auth)
    {
        if (Knocked) return;
        stumbleT = -1f; kickT = -1f;
        windup = -1f; squash = 1f; camKick = camKickVel = 0f;
        pivot.localScale = Vector3.one;
        UpdateStumble();
        Pose camPose = cam != null && controlled ? new Pose(cam.transform.position, cam.transform.rotation)
            : new Pose(EyePosition, Quaternion.Euler(pitch, yaw, 0));
        cc.enabled = false;
        viewModel.SetActive(false);
        var bodyMat = bodyMaterial != null ? bodyMaterial : pivot.GetComponentInChildren<Renderer>().sharedMaterial;
        ragdoll = HunterRagdoll.Begin(this, launch, spin, auth, bodyMat, gun, camPose);
        pivot.gameObject.SetActive(false);   // капсула прячется, вместо неё 7 тел ragdoll (дробовик ушёл в руку)
    }

    // Подъём закончен: охотник стоит на проверенной точке, управление возвращается.
    public void EndKnock(Vector3 foot, float yawDeg, float immuneUntil)
    {
        ragdoll = null;
        transform.position = foot;
        yaw = yawDeg; pitch = 0f; vy = 0f; wasAirborne = false;
        transform.rotation = Quaternion.Euler(0, yaw, 0);
        pivot.gameObject.SetActive(true);
        cc.enabled = true;
        KnockImmuneUntil = immuneUntil;
        ApplyControlled();
    }

    // Телл промаха: звук + спотыкание (видно прячущимся поблизости). Без штрафа к перезарядке.
    void Stumble()
    {
        stumbleT = 0f;
        CombatAudio.PlayAt(CombatAudio.Miss, transform.position + Vector3.up, 1f, controlled);
    }

    float StumbleK => stumbleT < 0f ? 0f : Mathf.Sin(Mathf.PI * Mathf.Clamp01(stumbleT / stumbleDuration));

    void UpdateStumble()
    {
        if (stumbleT >= 0f)
        {
            stumbleT += Time.deltaTime;
            if (stumbleT >= stumbleDuration) stumbleT = -1f;
        }
        float k = StumbleK;
        float wob = Mathf.Sin(stumbleT * 22f) * 6f * k;
        pivot.localRotation = Quaternion.Euler(28f * k, 0f, wob);
        pivot.localPosition = new Vector3(0, 0, 0.12f * k);
        if (kickT >= 0f)
        {
            kickT += Time.deltaTime;
            float kk = Mathf.Sin(Mathf.PI * Mathf.Clamp01(kickT / KickAnimTime));   // выпад: шаг вперёд с наклоном назад
            pivot.localRotation *= Quaternion.Euler(-14f * kk, 0f, 0f);
            pivot.localPosition += new Vector3(0, 0, 0.3f * kk);
            if (kickT >= KickAnimTime) kickT = -1f;
        }
    }

    System.Collections.IEnumerator Tracers(Vector3 origin, Vector3[] ends)
    {
        var lines = new List<GameObject>();
        Vector3 start = origin + (cam != null && controlled ? cam.transform.rotation * new Vector3(0.18f, -0.15f, 0.5f) : Vector3.zero);
        foreach (var e in ends)
        {
            var go = new GameObject("Pellet");
            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = tracerMat;
            lr.positionCount = 2;
            lr.SetPosition(0, start); lr.SetPosition(1, e);
            lr.startWidth = lr.endWidth = 0.012f;
            lr.startColor = new Color(1f, 0.9f, 0.5f, 0.9f); lr.endColor = new Color(1f, 0.9f, 0.5f, 0.2f);
            lines.Add(go);
        }
        yield return new WaitForSeconds(0.06f);
        foreach (var l in lines) Destroy(l);
    }

    public float ragdollCamDistance = 3.2f, ragdollCamHeight = 1.0f, ragdollCamBlend = 0.35f;
    float tpBlend; bool tpValid; Vector3 tpPos; Quaternion tpRot;

    // Камера за телом не должна уходить за стены: луч от цели к желаемой позиции по статике.
    Vector3 ClampBehind(Vector3 target, Vector3 want)
    {
        Vector3 d = want - target; float len = d.magnitude;
        if (len < 0.01f) return want;
        if (Physics.SphereCast(target, 0.2f, d / len, out var hit, len, RamKickAuthority.StaticMask, QueryTriggerInteraction.Ignore))
            return target + d / len * Mathf.Max(0.3f, hit.distance - 0.05f);
        return want;
    }

    void LateUpdate()
    {
        if (!controlled || cam == null) { tpBlend = 0f; return; }
        // Третье лицо на время тарана (movement-and-camera.md / ram-kick.md): плавный вход и возврат в первое лицо после подъёма.
        tpBlend = Mathf.MoveTowards(tpBlend, Knocked ? 1f : 0f, Time.deltaTime / ragdollCamBlend);
        if (Knocked)
        {
            if (ragdoll.TryGetBodyCenter(out var center))
            {
                Vector3 target = center + Vector3.up * 0.3f;
                Vector3 back = Quaternion.Euler(0f, yaw, 0f) * Vector3.back;
                Vector3 want = target + back * ragdollCamDistance + Vector3.up * ragdollCamHeight;
                tpPos = ClampBehind(target, want);
                tpRot = Quaternion.LookRotation(target - tpPos, Vector3.up);
                tpValid = true;
            }
            else if (ragdoll.TryGetCameraPose(out var p, out var r)) { tpPos = p; tpRot = r; tpValid = true; }
            if (tpValid) cam.transform.SetPositionAndRotation(tpPos, tpRot);
            return;
        }
        float dip = StumbleK * -10f;   // камера кивает вниз при спотыкании
        float recoil = Mathf.Clamp01(1f - (Time.time - lastFire) / 0.15f) * -4f;
        Quaternion rot = Quaternion.Euler(pitch + dip + recoil, yaw, StumbleK * Mathf.Sin(stumbleT * 22f) * 3f);
        // Толчок камеры при приземлении (пружина, как у прячущегося) и приседание перед отрывом/после посадки.
        camKickVel += (-camKick * 180f - camKickVel * 18f) * Time.deltaTime;
        camKick += camKickVel * Time.deltaTime;
        float crouchDip = Mathf.Min(0f, squash - 1f) * height * 0.25f;
        Vector3 fpPos = EyePosition + Vector3.up * (-0.12f * StumbleK + camKick * 0.5f + crouchDip);
        if (tpBlend > 0f && tpValid)
        {
            float k = Mathf.SmoothStep(0f, 1f, tpBlend);
            fpPos = Vector3.Lerp(fpPos, tpPos, k);
            rot = Quaternion.Slerp(rot, tpRot, k);
        }
        cam.transform.SetPositionAndRotation(fpPos, rot);

        // Вьюмодель с «перезарядкой»: отдача -> помпа за fireInterval. Цифрового таймера нет.
        float interval = CombatAuthority.Instance != null ? CombatAuthority.Instance.fireInterval : 1.2f;
        float u = Mathf.Clamp01((Time.time - lastFire) / interval);
        float kick = Mathf.Clamp01(1f - u / 0.12f);
        float pump = u > 0.3f && u < 0.8f ? Mathf.Sin(Mathf.PI * (u - 0.3f) / 0.5f) : 0f;
        Vector3 local = new Vector3(0.22f, -0.2f, 0.55f - 0.08f * kick - 0.12f * pump);
        viewModel.transform.SetPositionAndRotation(cam.transform.TransformPoint(local),
            cam.transform.rotation * Quaternion.Euler(-12f * kick, 0, 0));
    }

    void OnGUI()
    {
        if (!controlled) return;
        var style = new GUIStyle(GUI.skin.label) { fontSize = 16, normal = { textColor = Color.white } };
        string phase = IsBlocked ? $"Подготовка: охотник заблокирован ({RoundState.Instance.PrepRemaining:0} с)" : "Охота";
        GUI.Label(new Rect(12, 8, 900, 28), $"ОХОТНИК   {phase}", style);
        GUI.color = Color.white;
        GUI.DrawTexture(new Rect(Screen.width / 2f - 2, Screen.height / 2f - 2, 4, 4), Texture2D.whiteTexture);
    }
}
