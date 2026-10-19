using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.Networking;
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
    static readonly int[] StarDeliveries = { 10, 20, 30 }; // 별 1, 2, 3개에 필요한 배송 건수
    // ===== 랭킹 (Firebase Realtime Database) =====
    // Firebase 콘솔에서 만든 데이터베이스 주소로 바꾸세요. 끝에 / 는 붙이지 않습니다.
    const string FirebaseUrl = "https://second-delivery-13526-default-rtdb.asia-southeast1.firebasedatabase.app";
    const int RankShow = 5;          // 결과 화면에 보여 줄 순위 수
    const float RankRefresh = 5f;    // 결과 화면에서 랭킹을 다시 불러오는 간격(초)
    const int NameMax = 10;          // 이름 최대 글자 수

    const string BestKey = "BestScoreV2"; // 점수 공식이 바뀌어서 예전 최고 기록과 따로 저장
    const int MaxOrders = 2;         // 동시에 뜨는 배송지 최대 개수
    const float MapHalfW = 12f;      // 맵 절반 너비. 화면 약 3장 폭 (넓히거나 좁히려면 이 값만 바꾸면 됨)

    class Box { public Transform t; public SpriteRenderer sr; public Vector2 size; }
    class Car : Box { public float speed; }
    class Order { public int house; public float timer, limit; } // 배송지 하나 (제한시간은 배송지마다 따로 흐름)

    Camera cam;
    Sprite square, triangle;
    Material spriteMat;
    Font font;
    float viewHalf;                  // 지금 카메라에 보이는 절반 너비

    Box player;
    readonly List<Box> houses = new List<Box>();
    readonly List<Car> cars = new List<Car>();
    readonly float[] lanes = { 2.7f, 0.15f, -0.95f };
    readonly List<Order> orders = new List<Order>();
    readonly SpriteRenderer[] barBgs = new SpriteRenderer[MaxOrders];
    readonly SpriteRenderer[] barFills = new SpriteRenderer[MaxOrders];
    readonly SpriteRenderer[] markers = new SpriteRenderer[MaxOrders];
    readonly SpriteRenderer[] arrows = new SpriteRenderer[MaxOrders];

    // 상태
    bool playing;
    float timeLeft, stunTimer;
    int score, success, fail, combo, maxCombo, stage;
    int lastHouse = -1;              // 방금 끝난 배송지 (바로 같은 집이 다시 나오지 않게)

    // UI
    Text scoreText, timeText, infoText, hintText, resultText, starText;
    Image deliverImg;
    GameObject resultPanel, startPanel;
    Text startBestText, rankText;
    InputField nameInput;
    CanvasScaler uiScaler;
    string playerName = "익명";
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

        var tex = new Texture2D(4, 4);
        var px = new Color[16];
        for (int i = 0; i < 16; i++) px[i] = Color.white;
        tex.SetPixels(px); tex.Apply();
        tex.filterMode = FilterMode.Point;
        square = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);

        // 화면 밖 목적지를 가리키는 오른쪽 방향 삼각형
        var ttex = new Texture2D(32, 32, TextureFormat.RGBA32, false);
        var tpx = new Color[32 * 32];
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
                tpx[y * 32 + x] = Mathf.Abs(y - 15.5f) <= (32 - x) * 0.5f ? Color.white : Color.clear;
        ttex.SetPixels(tpx); ttex.Apply();
        triangle = Sprite.Create(ttex, new Rect(0, 0, 32, 32), new Vector2(0.5f, 0.5f), 32f);

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
        foreach (float y in lanes) MakeBox("Road", new Vector2(0, y), new Vector2(MapHalfW * 2 + 4, 1f), Road, 0);
        MakeBox("Sidewalk", new Vector2(0, -2.1f), new Vector2(MapHalfW * 2 + 4, 1.2f), Hex("#C9CCD1"), 0);

        // 맵 전체 폭에 2.6 간격으로 집을 배치 (MapHalfW = 12이면 한 줄에 9채)
        foreach (float y in new[] { 4.0f, 1.42f })
            for (float x = -MapHalfW + 1.4f; x <= MapHalfW - 1.4f + 0.01f; x += 2.6f)
                houses.Add(MakeBox("House", new Vector2(x, y), new Vector2(1.3f, 1.0f), HouseCol, 1));

        player = MakeBox("Player", new Vector2(0, -2.1f), new Vector2(0.6f, 0.6f), Blue, 5);
        // 배송지마다 제한시간 막대, 마커, 화면 밖 화살표를 하나씩 준비
        for (int k = 0; k < MaxOrders; k++)
        {
            barBgs[k] = MakeBox("BarBg", Vector2.zero, new Vector2(1.3f, 0.18f), new Color(0, 0, 0, 0.6f), 7).sr;
            barFills[k] = MakeBox("BarFill", Vector2.zero, new Vector2(1.3f, 0.18f), Yellow, 8).sr;
            markers[k] = MakeBox("Marker", Vector2.zero, new Vector2(0.3f, 0.3f), Yellow, 8).sr;
            markers[k].transform.rotation = Quaternion.Euler(0, 0, 45);
            arrows[k] = MakeBox("Arrow", Vector2.zero, new Vector2(0.7f, 0.7f), Yellow, 9).sr;
            arrows[k].sprite = triangle;
            arrows[k].enabled = false;
        }
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
            var b = MakeBox("Car", new Vector2(Random.Range(-MapHalfW, MapHalfW), y), size, c, 3);
            cars.Add(new Car { t = b.t, sr = b.sr, size = size, speed = speed * dir });
        }
    }

    // 남은 시간이 줄어들수록 장애물이 하나씩 더 등장
    // 맵이 넓어진 만큼 차도 늘려서 화면당 차 밀도를 예전과 비슷하게 유지
    int CarDensity() { return Mathf.Max(1, Mathf.RoundToInt(MapHalfW / 4f)); }

    void OnStage(int s)
    {
        int n = 2 * CarDensity();
        if (s == 1) SpawnCars(0, n);
        else if (s == 2) SpawnCars(1, n);
        else SpawnCars(2, n);
    }

    float LimitFor(int s) { return s <= 1 ? 7f : (s == 2 ? 5f : 4f); }

    // ================= 진행 =================
    void ShowStart()
    {
        playing = false;
        resultPanel.SetActive(false);
        startBestText.text = "최고 기록  " + PlayerPrefs.GetInt(BestKey, 0);
        nameInput.text = PlayerPrefs.GetString("PlayerName", "");
        startPanel.SetActive(true);
    }

    public void StartGame()
    {
        playerName = CleanName(nameInput.text);
        PlayerPrefs.SetString("PlayerName", playerName);
        PlayerPrefs.Save();
        startPanel.SetActive(false);
        Restart();
    }

    // 이름 정리: 앞뒤 공백 제거, 따옴표·역슬래시·꺾쇠·제어 문자 제거, 최대 NameMax 글자, 비어 있으면 "익명"
    string CleanName(string raw)
    {
        var sb = new StringBuilder();
        foreach (char ch in (raw ?? "").Trim())
            if (!char.IsControl(ch) && ch != '"' && ch != '\\' && ch != '<' && ch != '>') sb.Append(ch);
        string n = sb.ToString().Trim();
        if (n.Length > NameMax) n = n.Substring(0, NameMax);
        return n.Length == 0 ? "익명" : n;
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
        orders.Clear();
        lastHouse = -1;
        AddOrder();
        resultPanel.SetActive(false);
        playing = true;
    }

    bool HasOrder(int house)
    {
        foreach (var o in orders) if (o.house == house) return true;
        return false;
    }

    // 새 배송지 추가. 맵이 넓어서, 제한시간 안에 갈 수 있는 거리의 집만 후보로 고른다
    void AddOrder()
    {
        if (orders.Count >= MaxOrders) return;
        float limit = LimitFor(Mathf.Max(stage, 1));
        float reach = limit * PlayerSpeed * 0.7f;
        Vector2 pp = player.t.position;
        var cand = new List<int>();
        int nearest = -1;
        float nearestDist = float.MaxValue;
        for (int i = 0; i < houses.Count; i++)
        {
            if (i == lastHouse || HasOrder(i)) continue;
            float d = Vector2.Distance(pp, houses[i].t.position);
            if (d <= reach) cand.Add(i);
            if (d < nearestDist) { nearestDist = d; nearest = i; }
        }
        if (cand.Count == 0)
        {
            if (nearest < 0) return;
            cand.Add(nearest);
        }
        orders.Add(new Order { house = cand[Random.Range(0, cand.Count)], timer = limit, limit = limit });
    }

    // 배송 가능한 거리 안에 있는 배송지 번호 (없으면 -1)
    int DeliverableOrder()
    {
        if (!playing || stunTimer > 0) return -1;
        int best = -1;
        float bestDist = DeliverRange;
        for (int i = 0; i < orders.Count; i++)
        {
            float d = Vector2.Distance(player.t.position, houses[orders[i].house].t.position);
            if (d <= bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    bool CanDeliver() { return DeliverableOrder() >= 0; }

    // 남은 시간이 가장 적은 배송지 번호
    int MostUrgentOrder()
    {
        int best = -1;
        for (int i = 0; i < orders.Count; i++)
            if (best < 0 || orders[i].timer < orders[best].timer) best = i;
        return best;
    }

    public void TryDeliver()
    {
        int i = DeliverableOrder();
        if (i < 0) return;
        combo++;
        maxCombo = Mathf.Max(maxCombo, combo);
        success++;
        score += Mathf.RoundToInt(100 * ComboMultiplier());
        lastHouse = orders[i].house;
        orders.RemoveAt(i);
        if (orders.Count == 0) AddOrder();
    }

    // 콤보 단계 배율: 1~4콤보 x1, 5~9콤보 x1.5, 10콤보 이상 x2
    float ComboMultiplier()
    {
        if (combo >= 10) return 2f;
        if (combo >= 5) return 1.5f;
        return 1f;
    }

    void FailOrder(int i)
    {
        if (i < 0 || i >= orders.Count) return;
        fail++;
        combo = 0;
        lastHouse = orders[i].house;
        orders.RemoveAt(i);
        if (orders.Count == 0) AddOrder();
    }

    void Update()
    {
        if (!playing)
        {
            // 이름 입력 중에 R을 쳐도 게임이 시작되지 않게
            if (RestartKey() && (nameInput == null || !nameInput.isFocused)) StartGame();
            return;
        }

        float dt = Time.deltaTime;
        timeLeft -= dt;
        float elapsed = GameTime - timeLeft;
        int s = elapsed < 20f ? 1 : (elapsed < 40f ? 2 : 3);
        if (s != stage) { stage = s; OnStage(s); }

        // 자동차 이동
        float edge = MapHalfW + 1.5f;
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
            p.x = Mathf.Clamp(p.x, -MapHalfW + 0.3f, MapHalfW - 0.3f);
            p.y = Mathf.Clamp(p.y, -2.5f, 4.6f);
            player.t.position = p;

            foreach (var c in cars)
            {
                if (Overlap(player, c))
                {
                    stunTimer = StunTime;          // 1.5초 기절
                    FailOrder(MostUrgentOrder());  // 가장 급한 택배 1개를 잃음
                    break;
                }
            }
        }

        // 배송지는 게임이 자동으로 정해 준다. 플레이어는 이동해서 배송 버튼만 누르면 된다.
        if (DeliverKey()) TryDeliver();

        // 배송 제한시간 (배송지마다 따로 흐름)
        for (int i = orders.Count - 1; i >= 0; i--)
        {
            orders[i].timer -= dt;
            if (orders[i].timer <= 0) FailOrder(i);
        }

        // 진행 중인 배송의 시간이 절반 남으면 다음 배송지 등장 (최대 MaxOrders곳)
        if (orders.Count < MaxOrders)
        {
            foreach (var o in orders)
            {
                if (o.timer <= o.limit * 0.5f) { AddOrder(); break; }
            }
        }

        UpdateVisuals();

        if (timeLeft <= 0) { timeLeft = 0; EndGame(); }
    }

    // 카메라는 플레이어를 좌우로만 따라가고, 맵 끝에서는 멈춘다
    void LateUpdate()
    {
        // 가로 화면(PC)에서는 높이 기준으로 UI 크기를 맞춰 결과 카드가 잘리지 않게 한다
        if (uiScaler != null) uiScaler.matchWidthOrHeight = Screen.width > Screen.height ? 1f : 0.5f;

        if (cam == null || player == null) return;
        viewHalf = cam.orthographicSize * cam.aspect;
        float maxX = Mathf.Max(0f, MapHalfW - viewHalf);
        float cx = Mathf.Clamp(player.t.position.x, -maxX, maxX);
        cam.transform.position = new Vector3(cx, 0f, -10f);
        UpdateArrow(cx);
    }

    // 목적지가 화면 밖이면 화면 가장자리에 방향 화살표를 띄운다 (배송지마다 하나)
    void UpdateArrow(float cx)
    {
        float edgeX = viewHalf - 0.6f;
        for (int k = 0; k < MaxOrders; k++)
        {
            var arrow = arrows[k];
            if (arrow == null) continue;
            if (!playing || k >= orders.Count) { arrow.enabled = false; continue; }
            var o = orders[k];
            Vector2 hp = houses[o.house].t.position;
            float dx = hp.x - cx;
            if (Mathf.Abs(dx) <= edgeX) { arrow.enabled = false; continue; }

            arrow.enabled = true;
            float side = Mathf.Sign(dx);
            float bob = Mathf.PingPong(Time.time * 2f, 0.25f);
            float inset = k * 0.9f; // 두 화살표가 겹치지 않게 두 번째는 조금 안쪽
            arrow.transform.position = new Vector3(cx + side * (edgeX - inset - bob), hp.y, 0f);
            arrow.transform.rotation = Quaternion.Euler(0, 0, side > 0 ? 0f : 180f);
            arrow.color = Mathf.Clamp01(o.timer / o.limit) < 0.35f ? Red : Yellow;
        }
    }

    bool Overlap(Box a, Box b)
    {
        Vector2 d = a.t.position - b.t.position;
        return Mathf.Abs(d.x) < (a.size.x + b.size.x) * 0.45f && Mathf.Abs(d.y) < (a.size.y + b.size.y) * 0.45f;
    }

    void UpdateVisuals()
    {
        int ready = DeliverableOrder();
        Color blink = Color.Lerp(HouseCol, Yellow, Mathf.PingPong(Time.time * 3f, 1f));
        foreach (var h in houses) h.sr.color = HouseCol;
        for (int k = 0; k < orders.Count; k++)
            houses[orders[k].house].sr.color = (k == ready) ? Orange : blink;

        for (int k = 0; k < MaxOrders; k++)
        {
            bool on = k < orders.Count;
            barBgs[k].enabled = on; barFills[k].enabled = on; markers[k].enabled = on;
            if (!on) continue;
            Vector2 hp = houses[orders[k].house].t.position;
            float ratio = Mathf.Clamp01(orders[k].timer / orders[k].limit);
            barBgs[k].transform.position = hp + new Vector2(0, 0.7f);
            barFills[k].transform.localScale = new Vector3(1.3f * ratio, 0.18f, 1);
            barFills[k].transform.position = hp + new Vector2(-0.65f + 0.65f * ratio, 0.7f);
            barFills[k].color = ratio < 0.35f ? Red : Yellow;
            markers[k].transform.position = hp + new Vector2(0, 1.0f + Mathf.PingPong(Time.time, 0.2f));
        }

        scoreText.text = "점수 " + score;
        timeText.text = Mathf.CeilToInt(timeLeft).ToString();
        timeText.color = timeLeft <= 10f ? Red : Color.white;
        float mult = ComboMultiplier();
        infoText.text = "배송 " + success + "\n콤보 " + combo + (mult > 1f ? "  x" + mult.ToString("0.#") : "");
        if (stunTimer > 0) hintText.text = "쿵! 급한 택배를 하나 잃어버렸다";
        else if (ready >= 0) hintText.text = "배송 버튼을 누르세요!";
        else if (orders.Count > 1) hintText.text = "배송지 2곳! 급한 곳부터 가세요";
        else hintText.text = "깜빡이는 배송지로 이동하세요";
        deliverImg.color = ready >= 0 ? Yellow : new Color(0.6f, 0.6f, 0.6f, 0.8f);
    }

    // ================= 결과 화면 =================
    void EndGame()
    {
        playing = false;
        int best = PlayerPrefs.GetInt(BestKey, 0);
        bool newBest = score > best;
        if (newBest) { best = score; PlayerPrefs.SetInt(BestKey, best); PlayerPrefs.Save(); }

        // 별은 배송 건수 기준
        int stars = 0;
        foreach (int need in StarDeliveries) if (success >= need) stars++;
        starText.text = new string('★', stars) + new string('☆', 3 - stars);
        string nextStar = stars < StarDeliveries.Length
            ? "다음 별까지 " + (StarDeliveries[stars] - success) + "건"
            : "별 3개 달성!";
        resultText.text =
            "점수  " + score + (newBest ? "  (신기록!)" : "") + "\n" +
            "배송 성공  " + success + "건\n" +
            "배송 실패  " + fail + "건\n" +
            "최고 콤보  " + maxCombo + "\n" +
            "최고 기록  " + best + "\n" +
            nextStar;
        resultPanel.SetActive(true);

        StopAllCoroutines();
        StartCoroutine(SubmitAndShowRanking(playerName, score, score > 0));
    }

    // ================= 랭킹 =================
    bool RankingReady() { return !FirebaseUrl.Contains("YOUR-PROJECT"); }

    // 점수를 저장하고, 결과 화면이 열려 있는 동안 RankRefresh초마다 랭킹을 새로 불러온다
    IEnumerator SubmitAndShowRanking(string name, int myScore, bool submit)
    {
        if (!RankingReady()) { rankText.text = "랭킹 서버 설정 전이에요"; yield break; }
        rankText.text = "랭킹 불러오는 중...";

        if (submit)
        {
            // time은 Firebase 서버 시간으로 기록된다
            string body = "{\"name\":\"" + name + "\",\"score\":" + myScore + ",\"time\":{\".sv\":\"timestamp\"}}";
            using (var req = new UnityWebRequest(FirebaseUrl + "/scores.json", "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                    Debug.LogWarning("점수 저장 실패: " + req.error + " " + req.downloadHandler.text);
            }
        }

        string url = FirebaseUrl + "/scores.json?orderBy=%22score%22&limitToLast=" + RankShow;
        while (resultPanel.activeSelf && !playing)
        {
            using (var req = UnityWebRequest.Get(url))
            {
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                    ShowRanking(ParseScores(req.downloadHandler.text), name, submit ? myScore : -1);
                else
                {
                    rankText.text = "랭킹을 불러오지 못했어요\n(인터넷 연결 확인)";
                    Debug.LogWarning("랭킹 불러오기 실패: " + req.error + " " + req.downloadHandler.text);
                }
            }
            yield return new WaitForSeconds(RankRefresh);
        }
    }

    // Firebase 응답 {"키":{"name":"..","score":123,"time":..}, ...} 에서 이름과 점수만 꺼내 높은 순으로 정렬
    List<KeyValuePair<string, int>> ParseScores(string json)
    {
        var list = new List<KeyValuePair<string, int>>();
        if (string.IsNullOrEmpty(json) || json == "null") return list;
        foreach (Match m in Regex.Matches(json, @"\{[^{}]*\}"))
        {
            var nm = Regex.Match(m.Value, @"""name""\s*:\s*""((?:\\.|[^""\\])*)""");
            var sc = Regex.Match(m.Value, @"""score""\s*:\s*(\d+)");
            if (!nm.Success || !sc.Success) continue;
            string n;
            try { n = Regex.Unescape(nm.Groups[1].Value); } catch { n = nm.Groups[1].Value; }
            list.Add(new KeyValuePair<string, int>(n, int.Parse(sc.Groups[1].Value)));
        }
        list.Sort((a, b) => b.Value.CompareTo(a.Value));
        return list;
    }

    void ShowRanking(List<KeyValuePair<string, int>> list, string me, int myScore)
    {
        var sb = new StringBuilder("실시간 랭킹 TOP " + RankShow + "\n");
        if (list.Count == 0) sb.Append("아직 기록이 없어요");
        bool marked = false;
        for (int i = 0; i < list.Count; i++)
        {
            bool mine = !marked && list[i].Key == me && list[i].Value == myScore;
            if (mine) marked = true;
            sb.Append(i + 1).Append("위  ").Append(list[i].Key).Append("  ").Append(list[i].Value);
            if (mine) sb.Append("  (나)");
            sb.Append('\n');
        }
        rankText.text = sb.ToString().TrimEnd();
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
        uiScaler = scaler;
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
        var card = MakeImage("Card", dim.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(380, 700), Hex("#FFF8EC"));
        var title = MakeText(card.transform, "배송 종료!", 40, TextAnchor.MiddleCenter, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -50));
        title.color = Hex("#3A2E22");
        starText = MakeText(card.transform, "", 48, TextAnchor.MiddleCenter, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -115));
        starText.color = Hex("#F5B700");
        resultText = MakeText(card.transform, "", 24, TextAnchor.UpperCenter, new Vector2(0, 0), new Vector2(1, 1), new Vector2(0, -160));
        resultText.color = Hex("#3A2E22");
        resultText.rectTransform.offsetMin = new Vector2(20, 340);
        resultText.rectTransform.offsetMax = new Vector2(-20, -160);
        // 결과 아래 실시간 랭킹
        rankText = MakeText(card.transform, "", 20, TextAnchor.UpperCenter, new Vector2(0, 1), new Vector2(1, 1), Vector2.zero);
        rankText.color = Hex("#3A2E22");
        rankText.supportRichText = false;
        rankText.rectTransform.pivot = new Vector2(0.5f, 1f);
        rankText.rectTransform.anchoredPosition = new Vector2(0, -370);
        rankText.rectTransform.sizeDelta = new Vector2(0, 230);
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
        MakeText(sdim.transform, "1. 노랗게 깜빡이는 집이 배송지 (최대 2곳)\n2. 왼쪽 스틱으로 이동, 자동차 조심\n3. 도착하면 배송 버튼", 22, TextAnchor.MiddleCenter, new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(0, 40));
        startBestText = MakeText(sdim.transform, "", 26, TextAnchor.MiddleCenter, new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(0, -30));
        nameInput = MakeInput(sdim.transform, new Vector2(0, -100), new Vector2(260, 56), "이름 입력 (최대 " + NameMax + "자)");
        var startBtn = MakeImage("StartButton", sdim.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -190), new Vector2(240, 80), Yellow);
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

    InputField MakeInput(Transform parent, Vector2 pos, Vector2 size, string placeholder)
    {
        var bg = MakeImage("NameInput", parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), pos, size, Color.white);
        var txt = MakeText(bg.transform, "", 26, TextAnchor.MiddleCenter, Vector2.zero, Vector2.one, Vector2.zero);
        txt.color = Color.black;
        txt.supportRichText = false;
        var ph = MakeText(bg.transform, placeholder, 22, TextAnchor.MiddleCenter, Vector2.zero, Vector2.one, Vector2.zero);
        ph.color = new Color(0, 0, 0, 0.4f);
        var input = bg.gameObject.AddComponent<InputField>();
        input.targetGraphic = bg;
        input.textComponent = txt;
        input.placeholder = ph;
        input.characterLimit = NameMax;
        input.lineType = InputField.LineType.SingleLine;
        return input;
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