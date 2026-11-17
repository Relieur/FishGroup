using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// 玩家得分：杀死一只小怪 +10 分
// 把本组件挂到场景里任意常驻物体上；按左手手柄 Y 弹出 / 收起得分面板
public class PlayerScoreBoard : MonoBehaviour
{
	// 杀一只小怪得多少分
	public static int pointsPerKill = 10;
	// 升级消耗：第一次 10 分，之后每升一级多花 2 分
	public static int upgradeBaseCost = 10;
	public static int upgradeCostStep = 2;

	static int s_Kills;
	static int s_Spent;
	static int s_UpgradeLevel;

	public static int Score => s_Kills * pointsPerKill - s_Spent;

	// 已升级次数
	public static int UpgradeLevel => s_UpgradeLevel;

	// 下一次升级要花多少分
	public static int NextUpgradeCost => upgradeBaseCost + upgradeCostStep * s_UpgradeLevel;

	// 小怪死亡时调用
	public static void NotifyKill()
	{
		s_Kills++;
	}

	// 花积分升级；分数不够返回 false
	public static bool TryUpgrade()
	{
		int cost = NextUpgradeCost;
		if (Score < cost) return false;

		s_Spent += cost;
		s_UpgradeLevel++;
		return true;
	}

	// 关闭“域重载”时静态数据会跨播放保留，进游戏时清零
	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
	static void ResetStatics()
	{
		s_Kills = 0;
		s_Spent = 0;
		s_UpgradeLevel = 0;
		pointsPerKill = 10;
		upgradeBaseCost = 10;
		upgradeCostStep = 2;
	}

	[Header("面板布局")]
	[Tooltip("面板宽度（米）")]
	public float panelWidth = 0.5f;
	[Tooltip("面板高度（米）")]
	public float panelHeight = 0.42f;
	[Tooltip("面板离相机的距离（米）")]
	public float distanceFromCamera = 0.9f;
	[Tooltip("面板相对视线的高度偏移（米），负数往下")]
	public float verticalOffset = -0.1f;

	[Header("样式")]
	[Tooltip("背景色（默认纯半透明黑）")]
	public Color backgroundColor = new Color(0f, 0f, 0f, 0.6f);
	public Color labelColor = new Color(1f, 1f, 1f, 0.7f);
	public Color scoreColor = Color.white;
	public Color buttonColor = new Color(0.16f, 0.52f, 0.85f, 0.85f);
	public int labelFontSize = 34;
	public int scoreFontSize = 76;
	public int buttonFontSize = 30;

	// 画布基准宽度（画布单位），面板尺寸按米配置时用它换算
	const float k_BaseWidth = 400f;

	InputAction showAction;     // 左手 Y 键
	InputAction upgradeAction;  // 右手 B 键（编辑器里用 U 键）
	GameObject panel;
	Canvas canvas;
	Camera cachedCamera;
	Text scoreText;
	Text levelText;
	Text upgradeText;
	Button upgradeButton;
	bool visible;
	int lastScore = int.MinValue;
	int lastCost = int.MinValue;
	float hintUntil;

	static Font s_Font;

	void Awake()
	{
		// Y 键在 Quest 左手手柄上是 secondaryButton
		showAction = new InputAction("ShowScore", InputActionType.Button,
			"<XRController>{LeftHand}/secondaryButton");
		// 编辑器里没接头显时用键盘 Y 测试
		showAction.AddBinding("<Keyboard>/y");
		showAction.Enable();

		// 升级按钮：右手 B 键。注意别用 A —— XRI 默认把右手 A 绑成跳跃，按一下人会跳起来
		upgradeAction = new InputAction("ScoreUpgrade", InputActionType.Button,
			"<XRController>{RightHand}/secondaryButton");
		upgradeAction.AddBinding("<Keyboard>/u");
		upgradeAction.Enable();

		// 图片/文字用的画布基准宽度，实际尺寸按米换算
		BuildPanel();
		AttachToCamera();
		panel.SetActive(false);
	}

	void Update()
	{
		if (showAction != null && showAction.WasPressedThisFrame())
			SetVisible(!visible);

		if (visible && upgradeAction != null && upgradeAction.WasPressedThisFrame())
			OnUpgradePressed();

		// “分数不足”提示到点后恢复按钮文字
		if (hintUntil > 0f && Time.time >= hintUntil)
		{
			hintUntil = 0f;
			lastCost = int.MinValue;
			RefreshUpgradeText();
		}
	}

	// 面板始终跟在视线前方，并且正对玩家
	void LateUpdate()
	{
		if (!visible || panel == null) return;

		// 相机可能被换掉（重新进场景），保证面板始终挂在当前相机下
		if (cachedCamera == null || panel.transform.parent != cachedCamera.transform)
			AttachToCamera();

		// 分数变了才刷新文字，避免每帧产生字符串垃圾
		if (Score != lastScore) RefreshText();
	}

	void SetVisible(bool value)
	{
		visible = value;
		if (panel == null) return;

		panel.SetActive(value);
		if (value)
		{
			RefreshText();
			RefreshUpgradeText();
			AttachToCamera();
		}
	}

	// 面板直接挂在相机下面，不再每帧写世界坐标：
	// XR 相机的最终位姿在 LateUpdate 之后才更新，用世界坐标追会让面板比视线慢半拍，
	// 转头 / 点按钮时面板看着就在跳。挂成相机的子物体就完全跟着视线走。
	void AttachToCamera()
	{
		if (cachedCamera == null)
		{
			cachedCamera = Camera.main;
			if (cachedCamera == null) cachedCamera = FindFirstObjectByType<Camera>();
			if (cachedCamera == null) return;
		}

		if (panel.transform.parent != cachedCamera.transform)
			panel.transform.SetParent(cachedCamera.transform, false);

		// 相机本地坐标：正前方 distanceFromCamera，上下偏移 verticalOffset
		panel.transform.localPosition = new Vector3(0f, verticalOffset, distanceFromCamera);
		panel.transform.localRotation = Quaternion.identity;

		float scale = Mathf.Max(0.0001f, panelWidth) / k_BaseWidth;
		panel.transform.localScale = new Vector3(scale, scale, scale);

		// 世界空间画布必须指定事件相机，鼠标点击才能落到按钮上（编辑器里可直接点）
		if (canvas != null) canvas.worldCamera = cachedCamera;
	}

	void RefreshText()
	{
		lastScore = Score;
		if (scoreText != null) scoreText.text = Score.ToString();
		RefreshUpgradeText();
	}

	void RefreshUpgradeText()
	{
		int cost = NextUpgradeCost;
		bool affordable = Score >= cost;

		if (levelText != null) levelText.text = "等级 Lv." + UpgradeLevel;
		if (upgradeButton != null) upgradeButton.interactable = affordable;

		if (upgradeText != null && lastCost != cost)
		{
			lastCost = cost;
			upgradeText.text = "升级 " + cost + " 分";
		}
	}

	void OnUpgradePressed()
	{
		if (!TryUpgrade())
		{
			// 分数不够：按钮本身就是灰的，这里再给个文字提示
			if (upgradeText != null) upgradeText.text = "分数不足";
			hintUntil = Time.time + 1f;
			return;
		}

		RefreshText();
		RefreshUpgradeText();
	}

	void BuildPanel()
	{
		panel = new GameObject("ScorePanel", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
		canvas = panel.GetComponent<Canvas>();
		canvas.renderMode = RenderMode.WorldSpace;
		canvas.sortingOrder = 20;

		RectTransform root = panel.GetComponent<RectTransform>();
		// 尺寸按米配置，换算成画布单位（画布基准宽度 k_BaseWidth）
		float scale = Mathf.Max(0.0001f, panelWidth) / k_BaseWidth;
		panel.transform.localScale = new Vector3(scale, scale, scale);

		float rectHeight = Mathf.Max(0.001f, panelHeight) / scale;
		root.anchorMin = root.anchorMax = new Vector2(0.5f, 0.5f);
		root.pivot = new Vector2(0.5f, 0.5f);
		root.anchoredPosition = Vector2.zero;
		root.sizeDelta = new Vector2(k_BaseWidth, rectHeight);

		// 只用一块纯色半透明背景
		CreateImage("Background", backgroundColor, new Vector2(k_BaseWidth, rectHeight), Vector2.zero);

		Text label = CreateText("Label", labelFontSize, labelColor,
			new Vector2(0f, rectHeight * 0.36f), rectHeight * 0.18f);
		label.text = "得分";

		scoreText = CreateText("Score", scoreFontSize, scoreColor,
			new Vector2(0f, rectHeight * 0.145f), rectHeight * 0.3f);
		scoreText.text = "0";

		levelText = CreateText("Level", 28, labelColor,
			new Vector2(0f, -rectHeight * 0.095f), rectHeight * 0.16f);
		levelText.text = "等级 Lv.0";

		CreateUpgradeButton(rectHeight);
	}

	void CreateUpgradeButton(float rectHeight)
	{
		GameObject go = new GameObject("UpgradeButton",
			typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
		go.transform.SetParent(panel.transform, false);

		RectTransform rect = go.GetComponent<RectTransform>();
		rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
		rect.pivot = new Vector2(0.5f, 0.5f);
		rect.anchoredPosition = new Vector2(0f, -rectHeight * 0.31f);
		rect.sizeDelta = new Vector2(300f, rectHeight * 0.18f);

		Image image = go.GetComponent<Image>();
		image.color = buttonColor;
		image.raycastTarget = true;

		upgradeButton = go.GetComponent<Button>();
		upgradeButton.targetGraphic = image;
		// 分数不够时按钮自动变灰（interactable = false）
		upgradeButton.onClick.AddListener(OnUpgradePressed);

		upgradeText = CreateText("UpgradeLabel", buttonFontSize, Color.white,
			Vector2.zero, rectHeight * 0.18f, go.transform);
		upgradeText.rectTransform.sizeDelta = new Vector2(300f, rectHeight * 0.18f);
		upgradeText.text = "升级 " + NextUpgradeCost + " 分";
	}

	void CreateImage(string name, Color color, Vector2 size, Vector2 anchoredPos)
	{
		GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
		go.transform.SetParent(panel.transform, false);

		RectTransform rect = go.GetComponent<RectTransform>();
		rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
		rect.pivot = new Vector2(0.5f, 0.5f);
		rect.anchoredPosition = anchoredPos;
		rect.sizeDelta = size;

		Image image = go.GetComponent<Image>();
		image.color = color;
		image.raycastTarget = false;
	}

	Text CreateText(string name, int fontSize, Color color, Vector2 anchoredPos, float height, Transform parent = null)
	{
		GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
		go.transform.SetParent(parent != null ? parent : panel.transform, false);

		RectTransform rect = go.GetComponent<RectTransform>();
		rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
		rect.pivot = new Vector2(0.5f, 0.5f);
		rect.anchoredPosition = anchoredPos;
		rect.sizeDelta = new Vector2(360f, height);

		Text text = go.GetComponent<Text>();
		text.text = "";
		text.font = GetFont();
		text.fontSize = fontSize;
		text.color = color;
		text.alignment = TextAnchor.MiddleCenter;
		text.raycastTarget = false;
		return text;
	}

	static Font GetFont()
	{
		if (s_Font != null) return s_Font;

		s_Font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
		return s_Font;
	}

	void OnDestroy()
	{
		if (showAction != null)
		{
			showAction.Disable();
			showAction.Dispose();
			showAction = null;
		}

		if (upgradeAction != null)
		{
			upgradeAction.Disable();
			upgradeAction.Dispose();
			upgradeAction = null;
		}

		if (panel != null) Destroy(panel);
	}
}
