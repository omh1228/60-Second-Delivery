using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
#endif

// 택배기사: 60초 배송
// 씬에 아무것도 배치하지 않아도 Play를 누르면 이 스크립트가 맵, 자동차, UI, 결과 화면을 모두 만든다.
public class DeliveryGame : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        new GameObject("DeliveryGame").AddComponent<DeliveryGame>();
    }

    // ===== 규칙 값 (기획서 기준) =====
    const float GameTime = 60f;      // 한 판 60초
    const float StunTime = 1.5f;     // 충돌 시 기절 1.5초
    const float PlayerSpeed = 3.6f;
    const float DeliverRange = 1.4f; // 배송 버튼이 활성화되는 거리

    class Box { public Transform t; public SpriteRenderer sr; public Vector2 size; }
    class Car : Box { public float speed; }

    Camera cam;
    Sprite square;
    Material spriteMat;
    Font font;
    float halfW;

    Box player;
    readonly List<Box> houses = new List<Box>();
    readonly List<Car> cars = new List<Car>();
    readonly float[] lanes = { 2.7f, 0.15f, -0.95f };
    SpriteRenderer barBg, barFill, marker;

    // 상태
    bool playing;
    float timeLeft, deliverTimer, deliverLimit, stunTimer;
    int score, success, fail, combo, maxCombo, stage;
    int target = -1;
    bool selected;

    // UI
    Text scoreText, timeText, infoText, hintText, resultText, starText;
    Image deliverImg;
    GameObject resultPanel, startPanel;
    Text startBestText;
    VirtualStick stick;

    static readonly Color Blue = Hex("#3A7BD5");
    static readonly Color Road = Hex("#5B5F66");
    static readonly Color Grass = Hex("#8BC34A");
    static readonly Color Yellow = Hex("#FFD23F");
    static readonly Color Red = Hex("#E5484D");
    static readonly Color HouseCol = Hex("#EAD7B7");
    static readonly Color Orange = Hex("#FF9F1C");

    static Color Hex(string h) { Color c; ColorUtility.TryParseHtmlString(h, out c); return c; }

    void Start()
    {
        cam = Camera.main;
        if (cam == null)
        {
            var cgo = new GameObject("Main Camera");
            cgo.tag = "MainCamera";
            cam = cgo.AddComponent<Camera>();
        }
        cam.orthographic = true;
        cam.orthographicSize = 6f;
        cam.transform.position = new Vector3(0, 0, -10);
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Grass;
        halfW = Mathf.Min(cam.orthographicSize * cam.aspect, 4f);

        var tex = new Texture2D(4, 4);
        var px = new Color[16];
        for (int i = 0; i < 16; i++) px[i] = Color.white;
        tex.SetPixels(px); tex.Apply();
        tex.filterMode = FilterMode.Point;
        square = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);

        var unlit = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
        if (unlit != null) spriteMat = new Material(unlit);

        // 웹(WebGL) 빌드에서는 OS 글꼴을 쓸 수 없으므로 Resources/GameFont.ttf(한글 포함)를 먼저 사용
        font = Resources.Load<Font>("GameFont");
        if (font == null) font = Font.CreateDynamicFontFromOSFont(new[] { "Malgun Gothic", "Apple SD Gothic Neo", "Noto Sans CJK KR", "Arial" }, 32);
        if (spriteMat == null) EnsureGlobalLight();

        BuildWorld();
        BuildUI();
        ShowStart();
    }

    // URP 2D에서 Unlit 셰이더가 빌드에 빠졌을 때 스프라이트가 어둡게 보이지 않도록 전역 2D 조명을 하나 만든다.
    // (Built-in 파이프라인이면 Light2D 타입이 없으므로 아무것도 하지 않음)
    void EnsureGlobalLight()
    {
        var lightType = System.Type.GetType("UnityEngine.Rendering.Universal.Light2D, Unity.RenderPipelines.Universal.Runtime");
        if (lightType == null) return;
        if (Object.FindAnyObjectByType(lightType) != null) return;
        var go = new GameObject("Global Light 2D");
        var light = go.AddComponent(lightType);
        var prop = lightType.GetProperty("lightType");
        if (prop != null && prop.CanWrite)
            prop.SetValue(light, System.Enum.Parse(prop.PropertyType, "Global"));
    }

    // ================= 월드 =================
    Box MakeBox(string name, Vector2 pos, Vector2 size, Color c, int order)
    {
        var go = new GameObject(name);
        go.transform.position = pos;
        go.transform.localScale = new Vector3(size.x, size.y, 1);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = square;
        sr.color = c;
        sr.sortingOrder = order;
        if (spriteMat != null) sr.sharedMaterial = spriteMat;
        return new Box { t = go.transform, sr = sr, size = size };
    }

    void BuildWorld()
    {
        foreach (float y in lanes) MakeBox("Road", new Vector2(0, y), new Vector2(halfW * 2 + 4, 1f), Road, 0);
        MakeBox("Sidewalk", new Vector2(0, -2.1f), new Vector2(halfW * 2 + 4, 1.2f), Hex("#C9CCD1"), 0);

        float[] hx = { -halfW * 0.62f, 0f, halfW * 0.62f };
        foreach (float y in new[] { 4.0f, 1.42f })
            foreach (float x in hx)
                houses.Add(MakeBox("House", new Vector2(x, y), new Vector2(1.3f, 1.0f), HouseCol, 1));

        player = MakeBox("Player", new Vector2(0, -2.1f), new Vector2(0.6f, 0.6f), Blue, 5);
        barBg = MakeBox("BarBg", Vector2.zero, new Vector2(1.3f, 0.18f), new Color(0, 0, 0, 0.6f), 7).sr;
        barFill = MakeBox("BarFill", Vector2.zero, new Vector2(1.3f, 0.18f), Yellow, 8).sr;
        marker = MakeBox("Marker", Vector2.zero, new Vector2(0.3f, 0.3f), Yellow, 8).sr;
        marker.transform.rotation = Quaternion.Euler(0, 0, 45);
    }

    void SpawnCars(int type, int count)
    {
        for (int i = 0; i < count; i++)
        {
            Vector2 size; Color c; float speed;
            if (type == 0) { size = new Vector2(1.0f, 0.6f); c = Hex("#FF8A3D"); speed = Random.Range(1.6f, 2.3f); }       // 일반 자동차
            else if (type == 1) { size = new Vector2(0.6f, 0.35f); c = Hex("#222831"); speed = Random.Range(3.2f, 4.0f); } // 오토바이
            else { size = new Vector2(1.8f, 0.8f); c = Hex("#9C6ADE"); speed = Random.Range(1.1f, 1.4f); }                 // 트럭
            float y = lanes[(cars.Count + i) % lanes.Length];
            float dir = Random.value < 0.5f ? -1f : 1f;
            var b = MakeBox("Car", new Vector2(Random.Range(-halfW, halfW), y), size, c, 3);
            cars.Add(new Car { t = b.t, sr = b.sr, size = size, speed = speed * dir });
        }
    }

    // 남은 시간이 줄어들수록 장애물이 하나씩 더 등장
    void OnStage(int s)
    {
        if (s == 1) SpawnCars(0, 2);
        else if (s == 2) SpawnCars(1, 2);
        else SpawnCars(2, 2);
    }

    float LimitFor(int s) { return s <= 1 ? 7f : (s == 2 ? 5f : 4f); }

    // ================= 진행 =================
    void ShowStart()
    {
        playing = false;
        resultPanel.SetActive(false);
        startBestText.text = "최고 기록  " + PlayerPrefs.GetInt("BestScore", 0);
        startPanel.SetActive(true);
    }

    public void StartGame()
    {
        startPanel.SetActive(false);
        Restart();
    }

    void Restart()
    {
        foreach (var c in cars) Destroy(c.t.gameObject);
        cars.Clear();
        score = success = fail = combo = maxCombo = 0;
        stage = 0;
        timeLeft = GameTime;
        stunTimer = 0;
        player.t.position = new Vector2(0, -2.1f);
        target = -1;
        NewTarget();
        resultPanel.SetActive(false);
        playing = true;
    }

    void NewTarget()
    {
        int next;
        do { next = Random.Range(0, houses.Count); } while (next == target && houses.Count > 1);
        target = next;
        selected = false;
        deliverLimit = LimitFor(Mathf.Max(stage, 1));
        deliverTimer = deliverLimit;
    }

    bool CanDeliver()
    {
        if (!playing || stunTimer > 0 || target < 0 || !selected) return false;
        return Vector2.Distance(player.t.position, houses[target].t.position) <= DeliverRange;
    }

    public void TryDeliver()
    {
        if (!CanDeliver()) return;
        combo++;
        maxCombo = Mathf.Max(maxCombo, combo);
        success++;
        score += 100 + (combo - 1) * 20;
        NewTarget();
    }

    void Fail()
    {
        fail++;
        combo = 0;
        NewTarget();
    }

    void Update()
    {
        if (!playing)
        {
            if (RestartKey()) StartGame();
            return;
        }

        float dt = Time.deltaTime;
        timeLeft -= dt;
        float elapsed = GameTime - timeLeft;
        int s = elapsed < 20f ? 1 : (elapsed < 40f ? 2 : 3);
        if (s != stage) { stage = s; OnStage(s); }

        // 자동차 이동
        float edge = halfW + 1.5f;
        foreach (var c in cars)
        {
            var p = c.t.position;
            p.x += c.speed * dt;
            if (p.x > edge) p.x = -edge;
            if (p.x < -edge) p.x = edge;
            c.t.position = p;
        }

        // 플레이어 이동과 충돌
        if (stunTimer > 0)
        {
            stunTimer -= dt;
            player.sr.color = Mathf.Repeat(Time.time * 10f, 1f) < 0.5f ? Red : Blue;
        }
        else
        {
            player.sr.color = Blue;
            Vector2 p = player.t.position;
            p += ReadMove() * PlayerSpeed * dt;
            p.x = Mathf.Clamp(p.x, -halfW + 0.3f, halfW - 0.3f);
            p.y = Mathf.Clamp(p.y, -2.5f, 4.6f);
            player.t.position = p;

            foreach (var c in cars)
            {
                if (Overlap(player, c))
                {
                    stunTimer = StunTime; // 1.5초 기절
                    Fail();               // 들고 있던 택배를 잃어 배송 취소
                    break;
                }
            }
        }

        // 배송지 탭 선택
        Vector2 sp;
        if (PointerDown(out sp) && !PointerOverUI() && target >= 0)
        {
            Vector3 wp = cam.ScreenToWorldPoint(new Vector3(sp.x, sp.y, 10f));
            var h = houses[target];
            if (Mathf.Abs(wp.x - h.t.position.x) < h.size.x / 2 + 0.3f && Mathf.Abs(wp.y - h.t.position.y) < h.size.y / 2 + 0.3f)
                selected = true;
        }
        if (DeliverKey()) TryDeliver();

        // 배송 제한시간
        deliverTimer -= dt;
        if (deliverTimer <= 0) Fail();

        UpdateVisuals();

        if (timeLeft <= 0) { timeLeft = 0; EndGame(); }
    }

    bool Overlap(Box a, Box b)
    {
        Vector2 d = a.t.position - b.t.position;
        return Mathf.Abs(d.x) < (a.size.x + b.size.x) * 0.45f && Mathf.Abs(d.y) < (a.size.y + b.size.y) * 0.45f;
    }

    void UpdateVisuals()
    {
        for (int i = 0; i < houses.Count; i++)
        {
            Color c = HouseCol;
            if (i == target) c = selected ? Orange : Color.Lerp(HouseCol, Yellow, Mathf.PingPong(Time.time * 3f, 1f));
            houses[i].sr.color = c;
        }
        if (target >= 0)
        {
            Vector2 hp = houses[target].t.position;
            float ratio = Mathf.Clamp01(deliverTimer / deliverLimit);
            barBg.transform.position = hp + new Vector2(0, 0.7f);
            barFill.transform.localScale = new Vector3(1.3f * ratio, 0.18f, 1);
            barFill.transform.position = hp + new Vector2(-0.65f + 0.65f * ratio, 0.7f);
            barFill.color = ratio < 0.35f ? Red : Yellow;
            marker.transform.position = hp + new Vector2(0, 1.0f + Mathf.PingPong(Time.time, 0.2f));
        }

        scoreText.text = "점수 " + score;
        timeText.text = Mathf.CeilToInt(timeLeft).ToString();
        timeText.color = timeLeft <= 10f ? Red : Color.white;
        infoText.text = "배송 " + success + "\n콤보 " + combo;
        if (stunTimer > 0) hintText.text = "쿵! 택배를 잃어버렸다";
        else if (!selected) hintText.text = "깜빡이는 배송지를 탭하세요";
        else if (!CanDeliver()) hintText.text = "배송지까지 이동하세요";
        else hintText.text = "배송 버튼을 누르세요!";
        deliverImg.color = CanDeliver() ? Yellow : new Color(0.6f, 0.6f, 0.6f, 0.8f);
    }

    // ================= 결과 화면 =================
    void EndGame()
    {
        playing = false;
        int best = PlayerPrefs.GetInt("BestScore", 0);
        bool newBest = score > best;
        if (newBest) { best = score; PlayerPrefs.SetInt("BestScore", best); PlayerPrefs.Save(); }

        int stars = score >= 2000 ? 3 : (score >= 1000 ? 2 : 1);
        starText.text = new string('★', stars) + new string('☆', 3 - stars);
        resultText.text =
            "점수  " + score + (newBest ? "  (신기록!)" : "") + "\n" +
            "배송 성공  " + success + "건\n" +
            "배송 실패  " + fail + "건\n" +
            "최고 콤보  " + maxCombo + "\n" +
            "최고 기록  " + best;
        resultPanel.SetActive(true);
    }

    // ================= UI =================
    void BuildUI()
    {
        if (EventSystem.current == null)
        {
            var es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
            es.AddComponent<InputSystemUIInputModule>();
#else
            es.AddComponent<StandaloneInputModule>();
#endif
        }

        var cgo = new GameObject("Canvas");
        var canvas = cgo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;
        var scaler = cgo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(480, 854);
        scaler.matchWidthOrHeight = 0.5f;
        cgo.AddComponent<GraphicRaycaster>();
        var root = cgo.transform;

        // 상단 정보 영역
        var top = MakeImage("TopBar", root, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), Vector2.zero, new Vector2(0, 80), new Color(0, 0, 0, 0.45f));
        top.raycastTarget = false;
        scoreText = MakeText(top.transform, "점수 0", 24, TextAnchor.MiddleLeft, new Vector2(0, 0), new Vector2(0.4f, 1), new Vector2(16, 0));
        timeText = MakeText(top.transform, "60", 40, TextAnchor.MiddleCenter, new Vector2(0.35f, 0), new Vector2(0.65f, 1), Vector2.zero);
        infoText = MakeText(top.transform, "", 20, TextAnchor.MiddleRight, new Vector2(0.6f, 0), new Vector2(1, 1), new Vector2(-16, 0));
        hintText = MakeText(root, "", 22, TextAnchor.MiddleCenter, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -100));
        hintText.rectTransform.sizeDelta = new Vector2(0, 40);

        // 하단 조작 영역: 왼쪽 가상 스틱
        var stickBase = MakeImage("Stick", root, Vector2.zero, Vector2.zero, new Vector2(0.5f, 0.5f), new Vector2(110, 110), new Vector2(160, 160), new Color(1, 1, 1, 0.3f));
        var knob = MakeImage("Knob", stickBase.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(70, 70), new Color(1, 1, 1, 0.85f));
        knob.raycastTarget = false;
        stick = stickBase.gameObject.AddComponent<VirtualStick>();
        stick.knob = knob.rectTransform;

        // 하단 조작 영역: 오른쪽 배송 버튼
        deliverImg = MakeImage("DeliverButton", root, new Vector2(1, 0), new Vector2(1, 0), new Vector2(0.5f, 0.5f), new Vector2(-100, 110), new Vector2(150, 150), Yellow);
        var btn = deliverImg.gameObject.AddComponent<Button>();
        btn.onClick.AddListener(TryDeliver);
        MakeText(deliverImg.transform, "배송", 34, TextAnchor.MiddleCenter, Vector2.zero, Vector2.one, Vector2.zero).color = Color.black;

        // 결과 화면
        var dim = MakeImage("ResultPanel", root, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero, new Color(0, 0, 0, 0.75f));
        resultPanel = dim.gameObject;
        var card = MakeImage("Card", dim.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(380, 480), Hex("#FFF8EC"));
        var title = MakeText(card.transform, "배송 종료!", 40, TextAnchor.MiddleCenter, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -50));
        title.color = Hex("#3A2E22");
        starText = MakeText(card.transform, "", 48, TextAnchor.MiddleCenter, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -115));
        starText.color = Hex("#F5B700");
        resultText = MakeText(card.transform, "", 24, TextAnchor.UpperCenter, new Vector2(0, 0), new Vector2(1, 1), new Vector2(0, -160));
        resultText.color = Hex("#3A2E22");
        resultText.rectTransform.offsetMin = new Vector2(20, 110);
        resultText.rectTransform.offsetMax = new Vector2(-20, -160);
        var again = MakeImage("RestartButton", card.transform, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 30), new Vector2(220, 64), Blue);
        again.gameObject.AddComponent<Button>().onClick.AddListener(Restart);
        MakeText(again.transform, "다시 하기", 28, TextAnchor.MiddleCenter, Vector2.zero, Vector2.one, Vector2.zero);
        resultPanel.SetActive(false);

        // 시작 화면
        var sdim = MakeImage("StartPanel", root, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero, new Color(0, 0, 0, 0.75f));
        startPanel = sdim.gameObject;
        var st = MakeText(sdim.transform, "택배기사", 56, TextAnchor.MiddleCenter, new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(0, 230));
        st.color = Yellow;
        MakeText(sdim.transform, "60초 배송", 40, TextAnchor.MiddleCenter, new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(0, 165));
        MakeText(sdim.transform, "1. 깜빡이는 배송지를 탭\n2. 왼쪽 스틱으로 이동, 자동차 조심\n3. 도착하면 배송 버튼", 22, TextAnchor.MiddleCenter, new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(0, 40));
        startBestText = MakeText(sdim.transform, "", 26, TextAnchor.MiddleCenter, new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(0, -70));
        var startBtn = MakeImage("StartButton", sdim.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -170), new Vector2(240, 80), Yellow);
        startBtn.gameObject.AddComponent<Button>().onClick.AddListener(StartGame);
        MakeText(startBtn.transform, "START", 34, TextAnchor.MiddleCenter, Vector2.zero, Vector2.one, Vector2.zero).color = Color.black;
    }

    Image MakeImage(string name, Transform parent, Vector2 aMin, Vector2 aMax, Vector2 pivot, Vector2 pos, Vector2 size, Color c)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = aMin; rt.anchorMax = aMax; rt.pivot = pivot;
        rt.anchoredPosition = pos; rt.sizeDelta = size;
        var img = go.AddComponent<Image>();
        img.color = c;
        return img;
    }

    Text MakeText(Transform parent, string s, int size, TextAnchor align, Vector2 aMin, Vector2 aMax, Vector2 pos)
    {
        var go = new GameObject("Text", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = aMin; rt.anchorMax = aMax;
        rt.anchoredPosition = pos;
        rt.sizeDelta = (aMin == aMax) ? new Vector2(300, 60) : (aMin.y == aMax.y ? new Vector2(0, 60) : Vector2.zero);
        var tx = go.AddComponent<Text>();
        tx.font = font;
        tx.fontSize = size;
        tx.alignment = align;
        tx.color = Color.white;
        tx.text = s;
        tx.raycastTarget = false;
        tx.horizontalOverflow = HorizontalWrapMode.Overflow;
        tx.verticalOverflow = VerticalWrapMode.Overflow;
        return tx;
    }

    // ================= 입력 (Input System / 기존 Input 둘 다 지원) =================
    Vector2 ReadMove()
    {
        Vector2 v = stick != null ? stick.Value : Vector2.zero;
#if ENABLE_INPUT_SYSTEM
        var k = Keyboard.current;
        if (k != null)
        {
            if (k.aKey.isPressed || k.leftArrowKey.isPressed) v.x -= 1;
            if (k.dKey.isPressed || k.rightArrowKey.isPressed) v.x += 1;
            if (k.sKey.isPressed || k.downArrowKey.isPressed) v.y -= 1;
            if (k.wKey.isPressed || k.upArrowKey.isPressed) v.y += 1;
        }
#else
        v.x += Input.GetAxisRaw("Horizontal");
        v.y += Input.GetAxisRaw("Vertical");
#endif
        return Vector2.ClampMagnitude(v, 1f);
    }

    bool PointerDown(out Vector2 pos)
    {
#if ENABLE_INPUT_SYSTEM
        var p = Pointer.current;
        if (p != null && p.press.wasPressedThisFrame) { pos = p.position.ReadValue(); return true; }
#else
        if (Input.GetMouseButtonDown(0)) { pos = Input.mousePosition; return true; }
#endif
        pos = Vector2.zero;
        return false;
    }

    bool PointerOverUI()
    {
        return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
    }

    bool DeliverKey()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.Space);
#endif
    }

    bool RestartKey()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.R);
#endif
    }
}
