using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using VRTest.Environment;
#if UNITY_EDITOR
using UnityEditor;
#endif

// 游戏菜单 UI：
// 1. Boss 被击败后弹出胜利弹窗，带「退出游戏」按钮
// 2. 按下右手摇杆弹出「是否退出游戏？」确认弹窗，按「确定」退出
[AddComponentMenu("VR/Game Menu UI")]
public class GameMenuUI : MonoBehaviour
{
	[Header("页面布局")]
	[Tooltip("页面离相机的距离（米）")]
	public float distanceFromCamera = 0.75f;
	[Tooltip("页面宽度（米）")]
	public float panelWidth = 0.7f;
	[Tooltip("页面高度（米）")]
	public float panelHeight = 0.34f;
	[Tooltip("相对视线中心的垂直偏移（米）")]
	public float verticalOffset = 0.05f;

	[Header("文案")]
	public string victoryTitle = "胜利";
	public string victorySubtitle = "Boss 已被击败";
	public string victoryButtonLabel = "退出游戏";
	public string quitConfirmTitle = "是否退出游戏？";
	public string confirmButtonLabel = "确定";
	public string restartButtonLabel = "重新开始";
	public string tutorialButtonLabel = "返回教程";

	[Header("样式")]
	public Color panelColor = new Color(0f, 0f, 0f, 0.85f);
	public Color victoryTitleColor = new Color(1f, 0.85f, 0.3f, 1f);
	public Color titleColor = Color.white;
	public Color buttonColor = new Color(0.22f, 0.22f, 0.22f, 0.95f);
	public Color buttonTextColor = Color.white;
	public Color hintColor = new Color(0.8f, 0.8f, 0.8f, 1f);

	// 画布基准宽度（画布单位），实际尺寸按 panelWidth / panelHeight 换算
	const float k_BaseWidth = 900f;

	Transform m_Camera;
	GameObject m_VictoryGo;
	GameObject m_QuitGo;
	InputAction m_QuitPromptAction;
	bool m_VictoryShown;

	static Font s_Font;

	void Awake()
	{
		m_VictoryGo = BuildPanel("VictoryCanvas", 31, BuildVictoryContent);
		m_QuitGo = BuildPanel("QuitPromptCanvas", 40, BuildQuitContent);

		AttachToCamera();
		HideAll();

		// 右手摇杆按下
		m_QuitPromptAction = new InputAction("QuitPrompt", InputActionType.Button,
			"<XRController>{RightHand}/primary2DAxisClick");
		m_QuitPromptAction.Enable();

		BossNest.BossDefeated += ShowVictory;
	}

	void OnDestroy()
	{
		BossNest.BossDefeated -= ShowVictory;

		if (m_QuitPromptAction != null)
		{
			m_QuitPromptAction.Disable();
			m_QuitPromptAction.Dispose();
			m_QuitPromptAction = null;
		}

		if (m_VictoryGo != null) Destroy(m_VictoryGo);
		if (m_QuitGo != null) Destroy(m_QuitGo);
	}

	void LateUpdate()
	{
		// 相机可能在 Awake 之后才就绪，或者被替换
		if (m_Camera == null) AttachToCamera();
	}

	void Update()
	{
		if (m_QuitPromptAction == null || !m_QuitPromptAction.WasPressedThisFrame()) return;
		// 失败页面等暂停状态下不再叠加弹窗
		if (Time.timeScale <= 0f) return;

		ShowQuitPrompt();
	}

	void ShowVictory()
	{
		if (m_VictoryShown) return;
		m_VictoryShown = true;

		if (m_Camera == null) AttachToCamera();
		if (m_VictoryGo != null) m_VictoryGo.SetActive(true);
	}

	void ShowQuitPrompt()
	{
		if (m_QuitGo != null) m_QuitGo.SetActive(true);
	}

	void HideAll()
	{
		if (m_VictoryGo != null) m_VictoryGo.SetActive(false);
		if (m_QuitGo != null) m_QuitGo.SetActive(false);
	}

	// 重新开始：重载场景，跳过新手引导，直接进入正式游戏
	void RestartGame()
	{
		ReloadScene(true);
	}

	// 返回教程：重载场景并从第一句重新播放新手引导
	void BackToTutorial()
	{
		ReloadScene(false);
	}

	static void ReloadScene(bool skipTutorial)
	{
		TutorialDirector.SkipTutorialOnce = skipTutorial;
		PlayerScoreBoard.ResetProgress();
		Time.timeScale = 1f;

#if UNITY_EDITOR
		// 场景可能还没加进 Build Settings，编辑器里用路径直接重载
		string path = SceneManager.GetActiveScene().path;
		if (!string.IsNullOrEmpty(path))
		{
			UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(
				path, new LoadSceneParameters(LoadSceneMode.Single));
			return;
		}
#endif
		Scene active = SceneManager.GetActiveScene();
		SceneManager.LoadScene(active.buildIndex >= 0 ? active.buildIndex : 0);
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

		AttachPanel(m_VictoryGo);
		AttachPanel(m_QuitGo);
	}

	void AttachPanel(GameObject panel)
	{
		if (panel == null || m_Camera == null) return;

		panel.transform.SetParent(m_Camera, false);
		panel.transform.localPosition = new Vector3(0f, verticalOffset, Mathf.Max(0.2f, distanceFromCamera));
		panel.transform.localRotation = Quaternion.identity;
	}

	// 搭一个 world-space 页面底板，内容交给 builder 填
	GameObject BuildPanel(string name, int sortingOrder, System.Action<RectTransform> builder)
	{
		GameObject canvasGo = new GameObject(name, typeof(RectTransform), typeof(Canvas));
		Canvas canvas = canvasGo.GetComponent<Canvas>();
		canvas.renderMode = RenderMode.WorldSpace;
		canvas.sortingOrder = sortingOrder;

		float scale = Mathf.Max(0.0001f, panelWidth) / k_BaseWidth;
		canvasGo.transform.localScale = new Vector3(scale, scale, scale);

		float rectHeight = Mathf.Max(1f, panelHeight / scale);

		RectTransform root = canvasGo.GetComponent<RectTransform>();
		root.anchorMin = root.anchorMax = new Vector2(0.5f, 0.5f);
		root.pivot = new Vector2(0.5f, 0.5f);
		root.anchoredPosition = Vector2.zero;
		root.sizeDelta = new Vector2(k_BaseWidth, rectHeight);

		CreateImage(root, "Background", panelColor, new Vector2(k_BaseWidth, rectHeight), Vector2.zero);
		builder(root);

		return canvasGo;
	}

	void BuildVictoryContent(RectTransform root)
	{
		float rectHeight = root.sizeDelta.y;

		CreateText(root, victoryTitle, 64, victoryTitleColor,
			new Vector2(0f, rectHeight * 0.5f - 80f), new Vector2(k_BaseWidth, 100f));

		CreateText(root, victorySubtitle, 30, titleColor,
			new Vector2(0f, rectHeight * 0.5f - 155f), new Vector2(k_BaseWidth, 60f));

		float buttonY = -rectHeight * 0.5f + 135f;
		CreateButton(root, restartButtonLabel, 0, buttonY, RestartGame);
		CreateButton(root, tutorialButtonLabel, 1, buttonY, BackToTutorial);
		CreateButton(root, victoryButtonLabel, 2, buttonY, Quit);

		CreateText(root, "把手放到按钮上，按下抓握键", 24, hintColor,
			new Vector2(0f, -rectHeight * 0.5f + 45f), new Vector2(k_BaseWidth, 50f));
	}

	void BuildQuitContent(RectTransform root)
	{
		float rectHeight = root.sizeDelta.y;

		CreateText(root, quitConfirmTitle, 48, titleColor,
			new Vector2(0f, rectHeight * 0.5f - 85f), new Vector2(k_BaseWidth, 90f));

		float buttonY = -rectHeight * 0.5f + 90f;
		CreateButton(root, restartButtonLabel, 0, buttonY, RestartGame);
		CreateButton(root, tutorialButtonLabel, 1, buttonY, BackToTutorial);
		CreateButton(root, confirmButtonLabel, 2, buttonY, Quit);
	}

	// 三个按钮并排：slot 0 左（重新开始）、1 中（返回教程）、2 右（退出/确定）
	void CreateButton(RectTransform parent, string label, int slot, float y, System.Action onClick)
	{
		float[] xs = { -270f, 0f, 270f };
		float x = xs[Mathf.Clamp(slot, 0, xs.Length - 1)];

		GameObject go = new GameObject("Button_" + label, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
		go.transform.SetParent(parent, false);

		RectTransform rect = go.GetComponent<RectTransform>();
		rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
		rect.pivot = new Vector2(0.5f, 0.5f);
		rect.anchoredPosition = new Vector2(x, y);
		rect.sizeDelta = new Vector2(250f, 110f);

		Image image = go.GetComponent<Image>();
		image.color = buttonColor;
		image.raycastTarget = false;

		CreateText(rect, label, 40, buttonTextColor, Vector2.zero, new Vector2(250f, 110f));

		// 手碰到按钮时按下抓握键即可触发，不需要 UI 射线
		BoxCollider box = go.AddComponent<BoxCollider>();
		box.center = Vector3.zero;
		box.size = new Vector3(250f, 110f, 120f);

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
