using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// 玩家得分：杀死一只小怪 +10 分
// 把本组件挂到场景里任意常驻物体上；按左手手柄 Y 弹出 / 收起得分面板
public class PlayerScoreBoard : MonoBehaviour
{
	// 杀一只小怪得多少分
	public static int pointsPerKill = 10;
	static int s_Kills;

	public static int Score => s_Kills * pointsPerKill;

	// 小怪死亡时调用
	public static void NotifyKill()
	{
		s_Kills++;
	}

	// 关闭“域重载”时静态数据会跨播放保留，进游戏时清零
	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
	static void ResetStatics()
	{
		s_Kills = 0;
		pointsPerKill = 10;
	}

	[Header("面板布局")]
	[Tooltip("面板宽度（米）")]
	public float panelWidth = 0.5f;
	[Tooltip("面板高度（米）")]
	public float panelHeight = 0.3f;
	[Tooltip("面板离相机的距离（米）")]
	public float distanceFromCamera = 0.9f;
	[Tooltip("面板相对视线的高度偏移（米），负数往下")]
	public float verticalOffset = -0.12f;

	[Header("样式")]
	[Tooltip("背景色（默认纯半透明黑）")]
	public Color backgroundColor = new Color(0f, 0f, 0f, 0.6f);
	public Color labelColor = new Color(1f, 1f, 1f, 0.7f);
	public Color scoreColor = Color.white;
	public int labelFontSize = 34;
	public int scoreFontSize = 90;

	InputAction showAction;   // 左手 Y 键
	GameObject panel;
	Text scoreText;
	Transform cameraTransform;
	bool visible;

	static Font s_Font;

	void Awake()
	{
		// Y 键在 Quest 左手手柄上是 secondaryButton
		showAction = new InputAction("ShowScore", InputActionType.Button,
			"<XRController>{LeftHand}/secondaryButton");
		// 编辑器里没接头显时用键盘 Y 测试
		showAction.AddBinding("<Keyboard>/y");
		showAction.Enable();

		BuildPanel();
		panel.SetActive(false);
	}

	void Update()
	{
		if (showAction != null && showAction.WasPressedThisFrame())
			SetVisible(!visible);
	}

	// 面板始终跟在视线前方，并且正对玩家
	void LateUpdate()
	{
		if (!visible || panel == null) return;
		FollowCamera();
	}

	void SetVisible(bool value)
	{
		visible = value;
		if (panel == null) return;

		panel.SetActive(value);
		if (value)
		{
			RefreshText();
			FollowCamera();
		}
	}

	void FollowCamera()
	{
		if (cameraTransform == null)
		{
			Camera cam = Camera.main;
			if (cam == null) cam = FindFirstObjectByType<Camera>();
			if (cam == null) return;
			cameraTransform = cam.transform;
		}

		Vector3 pos = cameraTransform.position
			+ cameraTransform.forward * distanceFromCamera
			+ cameraTransform.up * verticalOffset;

		panel.transform.position = pos;
		panel.transform.rotation = Quaternion.LookRotation(pos - cameraTransform.position, Vector3.up);
	}

	void RefreshText()
	{
		if (scoreText != null) scoreText.text = Score.ToString();
	}

	void BuildPanel()
	{
		panel = new GameObject("ScorePanel", typeof(RectTransform), typeof(Canvas));
		Canvas canvas = panel.GetComponent<Canvas>();
		canvas.renderMode = RenderMode.WorldSpace;
		canvas.sortingOrder = 20;

		RectTransform root = panel.GetComponent<RectTransform>();
		// 尺寸按米配置，换算成画布单位（画布基准宽度 400）
		const float k_BaseWidth = 400f;
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
			new Vector2(0f, rectHeight * 0.22f), rectHeight * 0.34f);
		label.text = "得分";

		scoreText = CreateText("Score", scoreFontSize, scoreColor,
			new Vector2(0f, -rectHeight * 0.12f), rectHeight * 0.62f);
		scoreText.text = "0";
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

	Text CreateText(string name, int fontSize, Color color, Vector2 anchoredPos, float height)
	{
		GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
		go.transform.SetParent(panel.transform, false);

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

		if (panel != null) Destroy(panel);
	}
}
