using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
#if UNITY_EDITOR
using UnityEditor;
#endif

// 失败页面：玩家血量归零时弹在视野正前方，提供「重试」「退出」两个按钮
// 手放到按钮上按下抓握键即可点选
[AddComponentMenu("VR/Failure Screen")]
public class FailureScreen : MonoBehaviour
{
	[Header("页面布局")]
	[Tooltip("页面离相机的距离（米）")]
	public float distanceFromCamera = 0.75f;
	[Tooltip("页面宽度（米）")]
	public float panelWidth = 0.7f;
	[Tooltip("页面高度（米）")]
	public float panelHeight = 0.42f;
	[Tooltip("相对视线中心的垂直偏移（米）")]
	public float verticalOffset = 0.05f;

	[Header("样式")]
	public Color panelColor = new Color(0f, 0f, 0f, 0.85f);
	public Color titleColor = new Color(1f, 0.3f, 0.25f, 1f);
	public Color buttonColor = new Color(0.22f, 0.22f, 0.22f, 0.95f);
	public Color buttonTextColor = Color.white;

	// 画布基准宽度（画布单位），实际尺寸按 panelWidth / panelHeight 换算
	const float k_BaseWidth = 900f;

	Transform m_Camera;
	GameObject m_CanvasGo;
	bool m_Frozen;
	readonly System.Collections.Generic.List<XRGrabInteractable> m_PausedInteractables =
		new System.Collections.Generic.List<XRGrabInteractable>();

	static Font s_Font;

	void Awake()
	{
		PlayerHealthBar.HealthDepleted += Show;
		BuildUI();
		AttachToCamera();
		Hide();
	}

	void OnDestroy()
	{
		PlayerHealthBar.HealthDepleted -= Show;
	}

	void LateUpdate()
	{
		// 相机可能在 Awake 之后才就绪，或者被替换
		if (m_Camera == null) AttachToCamera();
	}

	// 血量归零：弹出失败页面，并暂停世界里的一切
	void Show()
	{
		if (m_CanvasGo == null) return;
		if (m_Camera == null) AttachToCamera();
		m_CanvasGo.SetActive(true);

		if (m_Frozen) return;
		m_Frozen = true;
		PauseGame();
	}

	void Hide()
	{
		if (m_CanvasGo != null) m_CanvasGo.SetActive(false);
	}

	// 时间冻结 + 断掉所有抓取：手里的鱼会停在原地，只有手柄还能操作页面按钮
	void PauseGame()
	{
		Time.timeScale = 0f;

		m_PausedInteractables.Clear();
		XRGrabInteractable[] grabs = FindObjectsOfType<XRGrabInteractable>(true);
		for (int i = 0; i < grabs.Length; i++)
		{
			if (grabs[i] == null || !grabs[i].enabled) continue;
			m_PausedInteractables.Add(grabs[i]);
			grabs[i].enabled = false;
		}
	}

	void ResumeGame()
	{
		Time.timeScale = 1f;

		for (int i = 0; i < m_PausedInteractables.Count; i++)
		{
			if (m_PausedInteractables[i] != null) m_PausedInteractables[i].enabled = true;
		}
		m_PausedInteractables.Clear();
		m_Frozen = false;
	}

	// 重试：回满血、积分等级清零、回到原点
	void Retry()
	{
		Hide();
		ResumeGame();

		if (PlayerHealthBar.Instance != null) PlayerHealthBar.Instance.ResetHealth();
		PlayerScoreBoard.ResetProgress();

		if (m_Camera == null) AttachToCamera();
		if (m_Camera != null)
		{
			Transform rig = m_Camera.transform.root;
			if (rig != null) rig.position = Vector3.zero;
		}
	}

	// 退出：编辑器里停止播放，打包后关闭游戏
	void Quit()
	{
#if UNITY_EDITOR
		EditorApplication.isPlaying = false;
#else
		Application.Quit();
#endif
	}

	// 挂到相机下，转头时页面跟着视线走
	void AttachToCamera()
	{
		Camera cam = Camera.main;
		if (cam == null) cam = FindFirstObjectByType<Camera>();
		if (cam == null) return;

		m_Camera = cam.transform;
		if (m_CanvasGo == null) return;

		m_CanvasGo.transform.SetParent(m_Camera, false);
		m_CanvasGo.transform.localPosition = new Vector3(0f, verticalOffset, Mathf.Max(0.2f, distanceFromCamera));
		m_CanvasGo.transform.localRotation = Quaternion.identity;
	}

	// 运行时搭一个 world-space 画布：半透明底板 + 标题 + 两个按钮 + 操作提示
	void BuildUI()
	{
		m_CanvasGo = new GameObject("FailureCanvas", typeof(RectTransform), typeof(Canvas));
		Canvas canvas = m_CanvasGo.GetComponent<Canvas>();
		canvas.renderMode = RenderMode.WorldSpace;
		canvas.sortingOrder = 30;

		float scale = Mathf.Max(0.0001f, panelWidth) / k_BaseWidth;
		m_CanvasGo.transform.localScale = new Vector3(scale, scale, scale);

		float rectHeight = Mathf.Max(1f, panelHeight / scale);

		RectTransform root = m_CanvasGo.GetComponent<RectTransform>();
		root.anchorMin = root.anchorMax = new Vector2(0.5f, 0.5f);
		root.pivot = new Vector2(0.5f, 0.5f);
		root.anchoredPosition = Vector2.zero;
		root.sizeDelta = new Vector2(k_BaseWidth, rectHeight);

		CreateImage(root, "Background", panelColor, new Vector2(k_BaseWidth, rectHeight), Vector2.zero);

		CreateText(root, "失败", 64, titleColor,
			new Vector2(0f, rectHeight * 0.5f - 80f), new Vector2(k_BaseWidth, 100f));

		CreateButton(root, "重试", new Vector2(-170f, -10f), Retry);
		CreateButton(root, "退出", new Vector2(170f, -10f), Quit);

		CreateText(root, "把手放到按钮上，按下抓握键", 26, new Color(0.8f, 0.8f, 0.8f, 1f),
			new Vector2(0f, -rectHeight * 0.5f + 50f), new Vector2(k_BaseWidth, 60f));
	}

	void CreateButton(RectTransform parent, string label, Vector2 pos, System.Action onClick)
	{
		GameObject go = new GameObject("Button_" + label, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
		go.transform.SetParent(parent, false);

		RectTransform rect = go.GetComponent<RectTransform>();
		rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
		rect.pivot = new Vector2(0.5f, 0.5f);
		rect.anchoredPosition = pos;
		rect.sizeDelta = new Vector2(300f, 110f);

		Image image = go.GetComponent<Image>();
		image.color = buttonColor;
		image.raycastTarget = false;

		CreateText(rect, label, 46, buttonTextColor, Vector2.zero, new Vector2(300f, 110f));

		// 手碰到按钮时按下抓握键即可触发，不需要 UI 射线
		BoxCollider box = go.AddComponent<BoxCollider>();
		box.center = Vector3.zero;
		box.size = new Vector3(300f, 110f, 120f);

		XRSimpleInteractable interactable = go.AddComponent<XRSimpleInteractable>();
		interactable.selectEntered.AddListener(_ => onClick());
	}

	Image CreateImage(Transform parent, string name, Color color, Vector2 size, Vector2 anchoredPos)
	{
		GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
		go.transform.SetParent(parent, false);

		RectTransform rect = go.GetComponent<RectTransform>();
		rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
		rect.pivot = new Vector2(0.5f, 0.5f);
		rect.anchoredPosition = anchoredPos;
		rect.sizeDelta = size;

		Image image = go.GetComponent<Image>();
		image.color = color;
		image.raycastTarget = false;
		return image;
	}

	Text CreateText(Transform parent, string content, int fontSize, Color color, Vector2 anchoredPos, Vector2 size)
	{
		GameObject go = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
		go.transform.SetParent(parent, false);

		RectTransform rect = go.GetComponent<RectTransform>();
		rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
		rect.pivot = new Vector2(0.5f, 0.5f);
		rect.anchoredPosition = anchoredPos;
		rect.sizeDelta = size;

		Text text = go.GetComponent<Text>();
		text.text = content;
		text.font = GetFont();
		text.fontSize = fontSize;
		text.color = color;
		text.alignment = TextAnchor.MiddleCenter;
		text.raycastTarget = false;
		text.horizontalOverflow = HorizontalWrapMode.Overflow;
		return text;
	}

	static Font GetFont()
	{
		if (s_Font != null) return s_Font;

		s_Font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
		return s_Font;
	}
}
