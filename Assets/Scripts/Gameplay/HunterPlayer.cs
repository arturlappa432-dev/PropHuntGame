using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Охотник (стикмен 1,8 м) с дробовиком. Ввод и камера клиентские; выстрел решает CombatAuthority.
[RequireComponent(typeof(CharacterController))]
public class HunterPlayer : MonoBehaviour
{
    public static readonly List<HunterPlayer> All = new List<HunterPlayer>();

    public Camera cam;
    public bool controlled;
    public float walkSpeed = 4f;          // скорость охотника в документах не задана, временно как у прячущегося
    public float gravity = -20f;
    public float mouseSensitivity = 0.1f;
    public float height = 1.8f;
    public float stumbleDuration = 0.7f;

    public float NextShotTime;
    public Vector3 EyePosition => transform.position + Vector3.up * height * 0.94f;
    public bool IsBlocked => RoundState.Instance != null && RoundState.Instance.Phase == RoundPhase.Prep;
    public string LastShot { get; private set; } = "";

    CharacterController cc;
    [SerializeField] Transform pivot, gun;
    [SerializeField] GameObject viewModel;
    float yaw, pitch, vy, stumbleT = -1f, lastFire = -10f;
    AudioSource sfx;
    Material tracerMat;

    static int OwnBodyLayer => LayerMask.NameToLayer("OwnBody");

    void OnEnable() { All.Add(this); }
    void OnDisable() { All.Remove(this); }

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
        h.SetControlled(controlled);
        return h;
    }

    static void Kill(Object o) { if (Application.isPlaying) Destroy(o); else DestroyImmediate(o); }   // Create вызывается и из редактора

    void OnDestroy() { if (viewModel != null) Destroy(viewModel); }

    void Awake() { cc = GetComponent<CharacterController>(); }

    void Start()
    {
        sfx = gameObject.AddComponent<AudioSource>();
        sfx.spatialBlend = 0f;
        tracerMat = new Material(Shader.Find("Sprites/Default"));
        yaw = transform.eulerAngles.y;
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

    // Своё тело не рисуется собственной камерой (слой OwnBody), вьюмодель только у управляемого.
    void ApplyControlled()
    {
        if (pivot == null) return;
        int layer = controlled && OwnBodyLayer >= 0 ? OwnBodyLayer : 0;
        foreach (var t in pivot.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        viewModel.SetActive(controlled);
    }

    void Update()
    {
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
        UpdateStumble();
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
        if (cc.isGrounded) vy = -1f; else vy += gravity * Time.deltaTime;
        move.y = vy;
        cc.Move(move * Time.deltaTime);
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

    void LateUpdate()
    {
        if (!controlled || cam == null) return;
        float dip = StumbleK * -10f;   // камера кивает вниз при спотыкании
        float recoil = Mathf.Clamp01(1f - (Time.time - lastFire) / 0.15f) * -4f;
        Quaternion rot = Quaternion.Euler(pitch + dip + recoil, yaw, StumbleK * Mathf.Sin(stumbleT * 22f) * 3f);
        cam.transform.SetPositionAndRotation(EyePosition + Vector3.up * -0.12f * StumbleK, rot);
        int own = OwnBodyLayer;
        if (own >= 0) cam.cullingMask = ~(1 << own);

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
        if (LastShot.Length > 0) GUI.Label(new Rect(12, 34, 900, 28), $"[отладка] {LastShot}", style);
        GUI.color = Color.white;
        GUI.DrawTexture(new Rect(Screen.width / 2f - 2, Screen.height / 2f - 2, 4, 4), Texture2D.whiteTexture);
    }
}
