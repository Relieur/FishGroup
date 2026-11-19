using UnityEngine;
using UnityEngine.UI;

// 玩家血条：把本组件挂到场景里任意常驻物体上即可（和 EnemyRadarRing 一样）
// 运行时自动贴到相机视野左下角；小怪贴近玩家时持续扣血，血条实时刷新
[AddComponentMenu("VR/Player Health Bar")]
public class PlayerHealthBar : MonoBehaviour
{
	[Header("生命值")]
	[Tooltip("满血血量")]
	public float maxHealth = 100f;
	[Tooltip("小怪与玩家的水平距离小于该值就开始扣血（米）")]
	public float damageDistance = 1.8f;
	[Tooltip("每只贴脸小怪每秒造成的伤害")]
	public float damagePerSecond = 15f;
	[Tooltip("勾选后不掉血（新手教程期间用）")]
	public bool invulnerable;

	[Header("HUD 布局（相对相机视线中心）")]
	[Tooltip("血条离相机的距离（米）")]
	public float distanceFromCamera = 0.7f;
	[Tooltip("相对视线中心的水平偏移（米），负值往左")]
	public float horizontalOffset = -0.26f;
	[Tooltip("相对视线中心的垂直偏移（米），负值往下")]
	public float verticalOffset = -0.2f;
	[Tooltip("血条宽度（米）")]
	public float barWidth = 0.34f;
	[Tooltip("血条高度（米）")]
	public float barHeight = 0.05f;

	[Header("样式")]
	[Tooltip("血条底板颜色")]
	public Color backgroundColor = new Color(0f, 0f, 0f, 0.65f);
	[Tooltip("满血时的填充色")]
	public Color fullColor = new Color(0.25f, 0.85f, 0.35f, 1f);
	[Tooltip("空血时的填充色，中间按血量比例渐变")]
	public Color emptyColor = new Color(0.9f, 0.22f, 0.18f, 1f);
	public Color labelColor = Color.white;
	[Tooltip("在血条中间显示当前血量数字")]
	public bool showValueText = true;

	[Header("采样")]
	[Tooltip("扫描小怪位置的间隔（秒）")]
	public float scanInterval = 0.2f;

	[Header("受伤闪屏")]
	[Tooltip("受伤时铺满视野的红色，透明度越高越明显")]
	public Color screenFlashColor = new Color(0.85f, 0.05f, 0.05f, 0.45f);
	[Tooltip("闪屏持续时间（秒）")]
	public float screenFlashDuration = 0.4f;
	[Tooltip("闪屏层离相机的距离（米）")]
	public float screenFlashDistance = 0.45f;

	// 画布基准宽度（画布单位），实际尺寸按 barWidth / barHeight 换算
	const float k_BaseWidth = 500f;
	const float k_FlashDuration = 0.35f;

	Transform m_Follow;
	GameObject m_CanvasGo;
	Image m_Fill;
	RectTransform m_FillRect;
	Image m_Flash;
	Image m_ScreenFlash;
	Text m_ValueText;

	float m_Health;
	float m_InnerWidth;
	float m_InnerHeight;

	float m_NextScan;
	int m_NearbyEnemies;

	float m_FlashUntil;
	float m_ScreenFlashUntil;
	bool m_Empty;

	static Font s_Font;

	// 供 Boss 泡泡等外部伤害来源调用
	public static PlayerHealthBar Instance { get; private set; }

	// 血量归零时通知失败页面
	public static event System.Action HealthDepleted;

	void Awake()
	{
		Instance = this;
		m_Health = Mathf.Max(1f, maxHealth);

		ResolveFollow();
		AttachToFollow();
		BuildUI();
		BuildScreenFlash();
		RefreshBar();
	}

	void Update()
	{
		if (m_Follow == null)
		{
			ResolveFollow();
			if (m_Follow == null) return;
		}

		UpdateDamage();
	}

	void LateUpdate()
	{
		if (m_Follow == null) return;

		AttachToFollow();
		UpdateFlash();
		UpdateScreenFlash();
	}

	// 按扫描间隔统计贴身的小怪数量，再按数量持续扣血
	void UpdateDamage()
	{
		if (Time.time >= m_NextScan)
		{
			m_NextScan = Time.time + Mathf.Max(0.05f, scanInterval);
			m_NearbyEnemies = CountNearbyEnemies();
		}

		if (m_Empty || m_NearbyEnemies <= 0) return;

		float amount = damagePerSecond * m_NearbyEnemies * Time.deltaTime;
		if (amount <= 0f) return;

		TakeDamage(amount);
	}

	// 供教程 / 过场隐藏或恢复血条
	public void SetVisible(bool visible)
	{
		if (m_CanvasGo != null) m_CanvasGo.SetActive(visible);
		if (m_ScreenFlash != null) m_ScreenFlash.transform.parent.gameObject.SetActive(visible);
	}

	// 受到伤害：血量归零后进入「力竭」，过一会儿自动回满
	public void TakeDamage(float amount)
	{
		if (amount <= 0f || m_Empty || invulnerable) return;

		m_Health = Mathf.Max(0f, m_Health - amount);
		m_FlashUntil = Time.time + k_FlashDuration;
		m_ScreenFlashUntil = Time.unscaledTime + Mathf.Max(0.05f, screenFlashDuration);
		RefreshBar();

		if (m_Health <= 0f)
		{
			m_Empty = true;
			if (m_ValueText != null) m_ValueText.text = "力竭";
			HealthDepleted?.Invoke();
		}
	}

	// 失败页面点「重试」：血量回满
	public void ResetHealth()
	{
		m_Empty = false;
		m_Health = Mathf.Max(1f, maxHealth);
		RefreshBar();
	}

	void OnDestroy()
	{
		if (Instance == this) Instance = null;
	}

	int CountNearbyEnemies()
	{
		Vector3 player = m_Follow != null ? m_Follow.position : transform.position;
		float limit = Mathf.Max(0.01f, damageDistance);

		LuffyEnemy[] enemies = FindObjectsOfType<LuffyEnemy>();
		int count = 0;

		for (int i = 0; i < enemies.Length; i++)
		{
			LuffyEnemy enemy = enemies[i];
			if (enemy == null || !enemy.IsAlive || enemy.IsGrabbed) continue;

			Vector3 delta = enemy.transform.position - player;
			delta.y = 0f;   // 只算水平距离：小怪贴地时离相机的高度差不算进来
			if (delta.sqrMagnitude <= limit * limit) count++;
		}

		return count;
	}

	// 血条直接挂在相机下面：XR 相机最终位姿在 LateUpdate 之后才更新，
	// 用世界坐标追会让血条比视线慢半拍，转头时看着在飘
	void AttachToFollow()
	{
		if (m_Follow == null) return;

		if (transform.parent != m_Follow) transform.SetParent(m_Follow, false);

		transform.localPosition = new Vector3(
			horizontalOffset,
			verticalOffset,
			Mathf.Max(0.1f, distanceFromCamera));
		transform.localRotation = Quaternion.identity;
		transform.localScale = Vector3.one;
	}

	void ResolveFollow()
	{
		if (m_Follow != null) return;

		Camera cam = Camera.main;
		if (cam == null) cam = FindFirstObjectByType<Camera>();
		if (cam != null) m_Follow = cam.transform;
	}

	void RefreshBar()
	{
		float fraction = Mathf.Clamp01(m_Health / Mathf.Max(1f, maxHealth));

		if (m_FillRect != null)
			m_FillRect.sizeDelta = new Vector2(m_InnerWidth * fraction, m_InnerHeight);

		if (m_Fill != null)
			m_Fill.color = Color.Lerp(emptyColor, fullColor, fraction);

		if (m_ValueText != null && !m_Empty)
			m_ValueText.text = Mathf.CeilToInt(m_Health).ToString();
	}

	// 挨打时整条血条闪一下红，反馈更直观
	void UpdateFlash()
	{
		if (m_Flash == null) return;

		float alpha = Mathf.Clamp01((m_FlashUntil - Time.time) / k_FlashDuration) * 0.45f;
		m_Flash.enabled = alpha > 0.001f;

		if (m_Flash.enabled)
		{
			Color color = m_Flash.color;
			color.a = alpha;
			m_Flash.color = color;
		}
	}

	// 受伤时整个视野蒙一层红，随时间淡出
	void UpdateScreenFlash()
	{
		if (m_ScreenFlash == null) return;

		float duration = Mathf.Max(0.05f, screenFlashDuration);
		float remain = m_ScreenFlashUntil - Time.unscaledTime;
		float alpha = remain <= 0f ? 0f : screenFlashColor.a * Mathf.Clamp01(remain / duration);

		m_ScreenFlash.enabled = alpha > 0.001f;
		if (m_ScreenFlash.enabled)
		{
			Color color = screenFlashColor;
			color.a = alpha;
			m_ScreenFlash.color = color;
		}
	}

	// 在相机正前方铺一张比视野略大的红色面片
	void BuildScreenFlash()
	{
		if (m_Follow == null) return;

		GameObject canvasGo = new GameObject("DamageScreenCanvas", typeof(RectTransform), typeof(Canvas));
		canvasGo.transform.SetParent(m_Follow, false);

		Canvas canvas = canvasGo.GetComponent<Canvas>();
		canvas.renderMode = RenderMode.WorldSpace;
		canvas.sortingOrder = 50;

		float dist = Mathf.Max(0.1f, screenFlashDistance);

		// 按相机实际视野算覆盖尺寸，四周多留一圈避免转头时看到边缘
		Camera cam = m_Follow.GetComponent<Camera>();
		float vFov = cam != null ? cam.fieldOfView : 60f;
		float aspect = cam != null ? cam.aspect : 1f;
		float height = 2f * dist * Mathf.Tan(vFov * 0.5f * Mathf.Deg2Rad) * 1.3f;
		float width = height * Mathf.Max(0.5f, aspect) * 1.3f;

		RectTransform rect = canvasGo.GetComponent<RectTransform>();
		rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
		rect.pivot = new Vector2(0.5f, 0.5f);
		rect.anchoredPosition = Vector2.zero;
		rect.sizeDelta = new Vector2(k_BaseWidth, k_BaseWidth);

		canvasGo.transform.localPosition = new Vector3(0f, 0f, dist);
		canvasGo.transform.localRotation = Quaternion.identity;
		canvasGo.transform.localScale = new Vector3(width / k_BaseWidth, height / k_BaseWidth, 1f);

		GameObject imageGo = new GameObject("Red", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
		imageGo.transform.SetParent(canvasGo.transform, false);

		RectTransform imageRect = imageGo.GetComponent<RectTransform>();
		imageRect.anchorMin = Vector2.zero;
		imageRect.anchorMax = Vector2.one;
		imageRect.offsetMin = Vector2.zero;
		imageRect.offsetMax = Vector2.zero;

		m_ScreenFlash = imageGo.GetComponent<Image>();
		m_ScreenFlash.color = screenFlashColor;
		m_ScreenFlash.raycastTarget = false;
		m_ScreenFlash.enabled = false;
	}

	// 运行时搭一个 world-space 画布：黑底 + 血量条 + 当前血量数字
	void BuildUI()
	{
		GameObject canvasGo = new GameObject("PlayerHealthCanvas", typeof(RectTransform), typeof(Canvas));
		canvasGo.transform.SetParent(transform, false);
		m_CanvasGo = canvasGo;

		Canvas canvas = canvasGo.GetComponent<Canvas>();
		canvas.renderMode = RenderMode.WorldSpace;
		canvas.sortingOrder = 16;

		float scale = Mathf.Max(0.0001f, barWidth) / k_BaseWidth;
		canvasGo.transform.localScale = new Vector3(scale, scale, scale);

		// barHeight 是米，不能拿 1 米做下限，否则会被撑成方块
		float rectHeight = Mathf.Max(1f, barHeight / scale);

		RectTransform root = canvasGo.GetComponent<RectTransform>();
		root.anchorMin = root.anchorMax = new Vector2(0.5f, 0.5f);
		root.pivot = new Vector2(0.5f, 0.5f);
		root.anchoredPosition = Vector2.zero;
		root.sizeDelta = new Vector2(k_BaseWidth, rectHeight);

		float padding = Mathf.Clamp(rectHeight * 0.1f, 1f, 6f);
		m_InnerWidth = Mathf.Max(1f, k_BaseWidth - padding * 2f);
		m_InnerHeight = Mathf.Max(1f, rectHeight - padding * 2f);

		CreateImage(canvasGo.transform, "Background", backgroundColor,
			new Vector2(k_BaseWidth, rectHeight), Vector2.zero);

		m_Fill = CreateImage(canvasGo.transform, "Fill", fullColor,
			new Vector2(m_InnerWidth, m_InnerHeight),
			new Vector2(-k_BaseWidth * 0.5f + padding, 0f));
		m_FillRect = m_Fill.rectTransform;
		// 左端固定，血量减少时条从右往左收
		m_FillRect.pivot = new Vector2(0f, 0.5f);

		m_Flash = CreateImage(canvasGo.transform, "DamageFlash", new Color(1f, 0.15f, 0.1f, 0f),
			new Vector2(k_BaseWidth, rectHeight), Vector2.zero);
		m_Flash.enabled = false;

		if (showValueText)
			m_ValueText = CreateText(canvasGo.transform, rectHeight);
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

	Text CreateText(Transform parent, float rectHeight)
	{
		GameObject go = new GameObject("Value", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
		go.transform.SetParent(parent, false);

		RectTransform rect = go.GetComponent<RectTransform>();
		rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
		rect.pivot = new Vector2(0.5f, 0.5f);
		rect.anchoredPosition = Vector2.zero;
		rect.sizeDelta = new Vector2(k_BaseWidth, rectHeight);

		Text text = go.GetComponent<Text>();
		text.text = "";
		text.font = GetFont();
		text.fontSize = Mathf.RoundToInt(rectHeight * 0.55f);
		text.color = labelColor;
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
