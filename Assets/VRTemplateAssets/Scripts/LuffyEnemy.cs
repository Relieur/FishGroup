using System.Collections;
using JSukoAnimals;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

// 挂在 YG_01 小怪上：随机出现在玩家视野内 + 头顶血条 + 被泡泡打掉血量 + 可抓取
// Boss 复用它：不追玩家，原地朝玩家发射泡泡
[AddComponentMenu("VR/Luffy Enemy")]
[DisallowMultipleComponent]
[DefaultExecutionOrder(-1000)]   // 碰撞体要在 XRGrabInteractable 之前建好
public class LuffyEnemy : MonoBehaviour
{
	[Header("血量")]
	[Tooltip("最大血量：小怪固定 20，Boss 由 BossNest 设为 100")]
	[Min(1f)] public float maxHealth = 20f;

	[Header("死亡特效")]
	[Tooltip("死亡时播放溶解特效；关闭则像以前一样立刻消失")]
	public bool deathDissolve = true;

	[Header("头顶血条")]
	[Tooltip("血条离模型最高点的距离（米）")]
	public float barHeadOffset = 0.3f;
	[Tooltip("血条宽度（米）")]
	public float barWidth = 0.9f;
	[Tooltip("血条高度（米）")]
	public float barHeight = 0.14f;

	[Header("面朝玩家并移动")]
	[Tooltip("勾选后小怪会慢慢朝玩家移动并转身面向玩家")]
	public bool chasePlayer = true;
	[Tooltip("朝玩家移动的速度（米/秒）")]
	public float moveSpeed = 0.6f;
	[Tooltip("离玩家这么近就停下，避免糊到脸上")]
	public float stopDistance = 1.5f;
	[Tooltip("转身平滑速度，越大转得越快")]
	public float turnSpeed = 2f;
	[Tooltip("模型原始朝向不是 +Z 时用它修正，常见值 90 / 180 / -90")]
	public float facingYawOffset = 180f;

	[Header("可抓取 / 摔伤")]
	[Tooltip("被扔出去落到地面时扣掉的最大血量比例，0.5 = 扣一半血")]
	[Range(0f, 1f)] public float throwDamageRatio = 0.5f;
	[Tooltip("拿在手里时，把被抓住的那一面对准玩家的转身速度（度/秒）")]
	public float grabTurnSpeed = 240f;

	[Header("发射泡泡（Boss）")]
	[Tooltip("勾选后不追玩家，原地转身朝玩家发射泡泡")]
	public bool shootBubbles = false;
	[Tooltip("泡泡模板；留空则运行时在场景里按名字 BossBubble 自动查找")]
	public GameObject bubbleTemplate;
	[Tooltip("发射间隔（秒）")]
	public float bubbleInterval = 2.5f;
	[Tooltip("泡泡飞行速度（米/秒）")]
	public float bubbleSpeed = 8f;
	[Tooltip("泡泡相对模板的缩放倍数")]
	public float bubbleScale = 3f;
	[Tooltip("泡泡命中玩家的伤害")]
	public float bubbleDamage = 10f;
	[Tooltip("射程（米），玩家超出这个距离就不再发射")]
	public float bubbleRange = 22f;

	const float k_BarRectWidth = 200f;   // 血条画布的参考宽度，实际米数由 barWidth 换算

	float m_Health;
	float m_NextFire;   // 下一次发射泡泡的时间
	bool m_Dead;

	Rigidbody m_Rigidbody;
	XRGrabInteractable m_Grab;
	bool m_Grabbed;      // 是否被玩家拿在手里
	bool m_Thrown;       // 松手后是否在等落地结算
	float m_ReleasedTime;

	Quaternion m_BaseRotation;   // 开局时的姿态，转身时只叠加偏航，保持模型原本的立正姿势
	float m_Yaw;

	float m_BuiltBarWidth;       // 上次生成血条时用的参数，运行中改了就在下一帧重建
	float m_BuiltBarHeight;

	Transform m_Camera;
	Transform m_PlayerRoot;
	Renderer[] m_Renderers;
	Collider[] m_Colliders;

	GameObject m_BarRoot;
	Image m_BarFill;
	float m_BarInnerWidth;
	float m_BarInnerHeight;
	Vector3 m_BarAnchorLocal;

	// 供方位指示器查询：小怪当前是否存活（死亡到重生之间为 false）
	public bool IsAlive => !m_Dead;

	// 是否正在锁定玩家：小怪在追人 / Boss 正在开火，雷达只提示这类敌人
	public bool IsLockedOn => chasePlayer || shootBubbles;

	// 供玩家血条查询：被拿在手里的小怪不算贴身攻击
	public bool IsGrabbed => m_Grabbed;

	// 供方位指示器查询：当前血量比例 1 = 满血，0 = 空
	public float HealthFraction => Mathf.Clamp01(m_Health / Mathf.Max(1f, maxHealth));

	// 碰撞体、刚体、抓取组件都在 Awake 里先准备好，顺序必须早于 XRGrabInteractable 缓存碰撞体
	void Awake()
	{
		m_Renderers = GetComponentsInChildren<Renderer>(true);
		m_Colliders = GetComponentsInChildren<Collider>(true);

		EnsureCollider();
		SetupGrab();
	}

	void Start()
	{
		m_Camera = FindCamera();
		m_PlayerRoot = m_Camera != null ? m_Camera.root : null;

		// 鱼群只是装饰，不能推挤怪：否则怪会被游动的鱼带到天上
		SardineUserController.IgnoreCollisionsWithFish(m_Colliders);

		// 血量在 Start 里初始化：BossNest 会在 Instantiate 之后、Start 之前改 maxHealth
		m_Health = Mathf.Max(1f, maxHealth);

		BuildBar();
		RefreshBar();

		// 记录原始姿态：之后转身只绕世界 Y 轴叠加偏航，不改变模型本身的立正方向
		m_BaseRotation = transform.rotation;
		m_Yaw = 0f;
	}

	// 没有刚体和抓取组件就补上：拿在手里能跟着手走，松手能靠重力摔在地上
	void SetupGrab()
	{
		m_Rigidbody = GetComponent<Rigidbody>();
		if (m_Rigidbody == null) m_Rigidbody = gameObject.AddComponent<Rigidbody>();

		m_Rigidbody.mass = 1f;
		m_Rigidbody.drag = 2f;
		m_Rigidbody.angularDrag = 5f;
		m_Rigidbody.useGravity = false;   // 平时靠脚本移动，松手扔出去时才打开重力
		m_Rigidbody.isKinematic = false;

		m_Grab = GetComponent<XRGrabInteractable>();
		if (m_Grab == null) m_Grab = gameObject.AddComponent<XRGrabInteractable>();

		m_Grab.selectEntered.AddListener(OnGrabbed);
		m_Grab.selectExited.AddListener(OnReleased);
	}

	void OnGrabbed(SelectEnterEventArgs args)
	{
		m_Grabbed = true;
		m_Thrown = false;

		if (m_Rigidbody != null) m_Rigidbody.useGravity = false;
	}

	// 拿在手里时，让它的正面朝向玩家（只转偏航，保持模型立正）
	void FaceCameraWhileGrabbed()
	{
		if (m_Camera == null) return;

		Vector3 toCamera = m_Camera.position - transform.position;
		toCamera.y = 0f;
		if (toCamera.sqrMagnitude < 0.0001f) return;

		Vector3 direction = toCamera.normalized;

		// 和追玩家时同一套朝向算法：正面指着头显
		float targetYaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg + facingYawOffset;

		m_Yaw = Mathf.MoveTowardsAngle(m_Yaw, targetYaw, grabTurnSpeed * Time.deltaTime);
		transform.rotation = Quaternion.AngleAxis(m_Yaw, Vector3.up) * m_BaseRotation;

		// 朝向由脚本接管，避免和 XRI 的旋转跟踪打架
		if (m_Rigidbody != null) m_Rigidbody.angularVelocity = Vector3.zero;
	}

	void OnReleased(SelectExitEventArgs args)
	{
		m_Grabbed = false;

		// 松手后打开重力，落到地面时再结算摔伤
		m_Thrown = true;
		m_ReleasedTime = Time.time;

		if (m_Rigidbody != null) m_Rigidbody.useGravity = true;
	}

	// 松手后等它落到地面，扣掉一半血
	void CheckThrowLanding()
	{
		if (Time.time - m_ReleasedTime < 0.15f) return;   // 刚松手还在手边
		if (m_Rigidbody != null && m_Rigidbody.velocity.y < -1f) return;   // 还在加速下落

		if (!IsTouchingGround()) return;

		m_Thrown = false;
		SettleAfterThrow();
	}

	bool IsTouchingGround()
	{
		Bounds bounds = CurrentBounds();
		Vector3 origin = bounds.center;
		float maxDistance = bounds.extents.y + 0.3f;

		RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, maxDistance,
			~0, QueryTriggerInteraction.Ignore);

		float allowedDistance = bounds.extents.y + 0.15f;

		for (int i = 0; i < hits.Length; i++)
		{
			Collider collider = hits[i].collider;
			if (collider == null) continue;
			if (collider.transform.IsChildOf(transform)) continue;
			if (m_PlayerRoot != null && collider.transform.IsChildOf(m_PlayerRoot)) continue;
			if (hits[i].distance <= allowedDistance) return true;
		}

		return false;
	}

	Bounds CurrentBounds()
	{
		if (m_Colliders != null)
		{
			for (int i = 0; i < m_Colliders.Length; i++)
			{
				if (m_Colliders[i] != null && m_Colliders[i].enabled)
					return m_Colliders[i].bounds;
			}
		}

		return ComputeBounds();
	}

	// 落地结算：停住物理、恢复立正姿态、扣血
	void SettleAfterThrow()
	{
		if (m_Rigidbody != null)
		{
			m_Rigidbody.useGravity = false;
			m_Rigidbody.velocity = Vector3.zero;
			m_Rigidbody.angularVelocity = Vector3.zero;
		}

		// 摔得东倒西歪的模型恢复立正，方便继续朝玩家游走
		transform.rotation = Quaternion.AngleAxis(m_Yaw, Vector3.up) * m_BaseRotation;

		ApplyThrowDamage();
	}

	void ApplyThrowDamage()
	{
		if (throwDamageRatio <= 0f) return;

		m_Health = Mathf.Max(0f, m_Health - maxHealth * Mathf.Clamp01(throwDamageRatio));
		RefreshBar();

		if (m_Health <= 0f) Die();
	}

	void Update()
	{
		if (m_Dead) return;

		// 被拿在手里或还在空中飞的时候不自己走
		if (!m_Grabbed && !m_Thrown)
		{
			if (shootBubbles) ShootBehaviour();
			else if (chasePlayer) ChasePlayer();
		}

		if (m_Thrown) CheckThrowLanding();

		// 运行中改了血条尺寸就立即重建，不用退出播放模式
		if (!Mathf.Approximately(m_BuiltBarWidth, barWidth) ||
			!Mathf.Approximately(m_BuiltBarHeight, barHeight))
		{
			if (m_BarRoot != null) Destroy(m_BarRoot);
			m_BarRoot = null;
			BuildBar();
			RefreshBar();
		}
	}

	// 水平方向朝玩家转身，并慢慢走过去；很近时只转身不移动
	void ChasePlayer()
	{
		if (m_Camera == null) return;

		Vector3 toPlayer = m_Camera.position - transform.position;
		toPlayer.y = 0f;

		float distance = toPlayer.magnitude;
		if (distance < 0.001f) return;

		Vector3 direction = toPlayer / distance;

		float targetYaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg + facingYawOffset;
		m_Yaw = Mathf.LerpAngle(m_Yaw, targetYaw, Mathf.Clamp01(turnSpeed * Time.deltaTime));
		transform.rotation = Quaternion.AngleAxis(m_Yaw, Vector3.up) * m_BaseRotation;

		if (distance > stopDistance)
		{
			float step = Mathf.Min(moveSpeed * Time.deltaTime, distance - stopDistance);
			transform.position += direction * step;
		}
	}

	// Boss：原地站着面向玩家，按间隔朝玩家发射泡泡
	void ShootBehaviour()
	{
		if (m_Camera == null) return;

		Vector3 toPlayer = m_Camera.position - transform.position;
		toPlayer.y = 0f;

		float distance = toPlayer.magnitude;
		if (distance < 0.001f) return;

		// 只转身不移动
		Vector3 direction = toPlayer / distance;
		float targetYaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg + facingYawOffset;
		m_Yaw = Mathf.LerpAngle(m_Yaw, targetYaw, Mathf.Clamp01(turnSpeed * Time.deltaTime));
		transform.rotation = Quaternion.AngleAxis(m_Yaw, Vector3.up) * m_BaseRotation;

		if (distance > bubbleRange) return;
		if (Time.time < m_NextFire) return;

		m_NextFire = Time.time + Mathf.Max(0.2f, bubbleInterval);
		FireBubble();
	}

	void FireBubble()
	{
		if (bubbleTemplate == null) bubbleTemplate = FindSceneObject("BossBubble");
		if (bubbleTemplate == null || m_Camera == null)
		{
			Debug.LogWarning("[Boss] 没找到泡泡模板，请把 Hierarchy 里的 BossBubble 拖到 bubbleTemplate 上", this);
			return;
		}

		// 从模型中心稍微往前的位置射出，避免泡泡出生在身体里
		Bounds bounds = CurrentBounds();
		Vector3 toPlayer = (m_Camera.position - bounds.center).normalized;
		Vector3 muzzle = bounds.center + toPlayer * Mathf.Max(0.2f, bounds.extents.magnitude * 0.6f);

		GameObject bubble = Instantiate(bubbleTemplate, muzzle, Quaternion.identity);
		if (!bubble.activeSelf) bubble.SetActive(true);

		bubble.transform.localScale = bubbleTemplate.transform.localScale * Mathf.Max(0.01f, bubbleScale);

		BossBubble projectile = bubble.GetComponent<BossBubble>();
		if (projectile == null) projectile = bubble.AddComponent<BossBubble>();
		projectile.Launch(m_Camera, bubbleSpeed, bubbleDamage);
	}

	// 按名字找场景对象，包括未激活的隐藏模板
	static GameObject FindSceneObject(string objectName)
	{
		GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
		for (int i = 0; i < all.Length; i++)
		{
			if (all[i] != null && all[i].name == objectName && all[i].scene.IsValid())
				return all[i];
		}
		return null;
	}

	void LateUpdate()
	{
		if (m_Dead) return;

		// 拿在手里时，正面朝向自己
		if (m_Grabbed) FaceCameraWhileGrabbed();

		if (m_BarRoot == null) return;

		Vector3 anchor = transform.TransformPoint(m_BarAnchorLocal) + Vector3.up * barHeadOffset;
		m_BarRoot.transform.position = anchor;

		if (m_Camera != null)
		{
			// 血条始终朝着头显，任何角度都能正面看到
			m_BarRoot.transform.rotation = Quaternion.LookRotation(anchor - m_Camera.position, Vector3.up);
		}
	}

	void OnDestroy()
	{
		if (m_BarRoot != null) Destroy(m_BarRoot);
	}

	// 巢穴撤退时用：小怪一边离开一边溶解，头顶血条先收掉
	public void HideOverheadBar()
	{
		if (m_BarRoot != null) m_BarRoot.SetActive(false);
	}

	// 泡泡命中时调用：扣 damage 点血；返回 true 表示这发泡泡被消耗
	public bool TakeDamage(float damage)
	{
		if (m_Dead || damage <= 0f) return false;

		m_Health = Mathf.Max(0f, m_Health - damage);
		RefreshBar();

		if (m_Health <= 0f) Die();
		return true;
	}

	void Die()
	{
		m_Dead = true;
		// 杀一只小怪加一次分
		PlayerScoreBoard.NotifyKill();

		// 死在手里就先脱手，避免抓着一团空气
		if (m_Grabbed) DropFromHand();
		ResetThrownState();

		// 死亡瞬间就不能再被打到 / 被捡起来；溶解结束后才隐藏
		if (m_Colliders != null)
		{
			for (int i = 0; i < m_Colliders.Length; i++)
			{
				if (m_Colliders[i] != null) m_Colliders[i].enabled = false;
			}
		}
		if (m_Grab != null) m_Grab.enabled = false;
		if (m_BarRoot != null) m_BarRoot.SetActive(false);

		if (deathDissolve) StartCoroutine(DeathDissolveRoutine());
		else SetVisible(false);
	}

	// 播完溶解再隐藏；材质由 DissolveEffect 还原，复活后还能正常显示
	IEnumerator DeathDissolveRoutine()
	{
		DissolveEffect effect = GetComponent<DissolveEffect>();
		if (effect == null) effect = gameObject.AddComponent<DissolveEffect>();

		yield return effect.Play();

		// 溶解期间已经复活的话保持可见
		if (!m_Dead) yield break;
		SetVisible(false);
	}

	// 取消物理和摔伤状态，回到脚本驱动移动
	void ResetThrownState()
	{
		m_Grabbed = false;
		m_Thrown = false;

		if (m_Rigidbody != null)
		{
			m_Rigidbody.useGravity = false;
			m_Rigidbody.velocity = Vector3.zero;
			m_Rigidbody.angularVelocity = Vector3.zero;
		}

		// 摔歪了就摆正，保证继续朝玩家走的时候姿态正常
		transform.rotation = Quaternion.AngleAxis(m_Yaw, Vector3.up) * m_BaseRotation;
	}

	void DropFromHand()
	{
		if (m_Grab == null) return;

		XRInteractionManager manager = m_Grab.interactionManager;
		if (manager == null) manager = FindFirstObjectByType<XRInteractionManager>();
		if (manager == null) return;

		manager.CancelInteractableSelection((IXRSelectInteractable)m_Grab);
	}

	void SetVisible(bool visible)
	{
		if (m_Renderers != null)
		{
			for (int i = 0; i < m_Renderers.Length; i++)
			{
				if (m_Renderers[i] != null) m_Renderers[i].enabled = visible;
			}
		}

		if (m_Colliders != null)
		{
			for (int i = 0; i < m_Colliders.Length; i++)
			{
				if (m_Colliders[i] != null) m_Colliders[i].enabled = visible;
			}
		}

		// 死掉的小怪不能被捡起来
		if (m_Grab != null) m_Grab.enabled = visible;

		if (m_BarRoot != null) m_BarRoot.SetActive(visible);
	}

	Transform FindCamera()
	{
		Camera camera = Camera.main;
		if (camera == null) camera = FindFirstObjectByType<Camera>();
		return camera != null ? camera.transform : null;
	}

	// 模型本身没有碰撞体时补一个盒体，泡泡才能打中
	void EnsureCollider()
	{
		for (int i = 0; i < m_Colliders.Length; i++)
		{
			if (m_Colliders[i] != null && !m_Colliders[i].isTrigger) return;
		}

		Bounds bounds = ComputeBounds();
		Vector3 scale = transform.lossyScale;

		BoxCollider box = gameObject.AddComponent<BoxCollider>();
		box.center = transform.InverseTransformPoint(bounds.center);
		box.size = new Vector3(
			bounds.size.x / Mathf.Max(0.0001f, Mathf.Abs(scale.x)),
			bounds.size.y / Mathf.Max(0.0001f, Mathf.Abs(scale.y)),
			bounds.size.z / Mathf.Max(0.0001f, Mathf.Abs(scale.z)));

		m_Colliders = GetComponentsInChildren<Collider>(true);
	}

	Bounds ComputeBounds()
	{
		bool hasBounds = false;
		Bounds result = new Bounds(transform.position, Vector3.zero);

		for (int i = 0; i < m_Renderers.Length; i++)
		{
			Renderer renderer = m_Renderers[i];
			if (renderer == null) continue;

			// 跳过空包围盒（粒子特效之类）
			if (renderer.bounds.size.sqrMagnitude < 0.0001f) continue;

			if (!hasBounds)
			{
				result = renderer.bounds;
				hasBounds = true;
			}
			else
			{
				result.Encapsulate(renderer.bounds);
			}
		}

		if (!hasBounds) result = new Bounds(transform.position + Vector3.up, Vector3.one);
		return result;
	}

	// 运行时搭一个 world-space 画布：黑底 + 绿色血量条，不依赖场景里预制 UI
	void BuildBar()
	{
		if (m_BarRoot != null) return;

		Bounds bounds = ComputeBounds();
		m_BarAnchorLocal = transform.InverseTransformPoint(
			new Vector3(bounds.center.x, bounds.max.y, bounds.center.z));

		m_BuiltBarWidth = barWidth;
		m_BuiltBarHeight = barHeight;

		m_BarRoot = new GameObject("HealthBar", typeof(RectTransform), typeof(Canvas));
		Canvas canvas = m_BarRoot.GetComponent<Canvas>();
		canvas.renderMode = RenderMode.WorldSpace;

		RectTransform rootRect = m_BarRoot.GetComponent<RectTransform>();

		// 怪物比预设大时，血条按模型宽度自适应，免得比例太小
		float currentBarWidth = Mathf.Max(barWidth, bounds.size.x * 0.6f);

		float scale = Mathf.Max(0.0001f, currentBarWidth) / k_BarRectWidth;
		m_BarRoot.transform.localScale = new Vector3(scale, scale, scale);

		// barHeight 是米，不能拿 1 米做下限，否则永远被撑成 1 米高的方块
		float rectHeight = Mathf.Max(0.001f, barHeight) / scale;

		// 画布和子元素都用“中心锚点 + 显式尺寸”，形状只由 barWidth / barHeight 决定
		rootRect.anchorMin = new Vector2(0.5f, 0.5f);
		rootRect.anchorMax = new Vector2(0.5f, 0.5f);
		rootRect.pivot = new Vector2(0.5f, 0.5f);
		rootRect.anchoredPosition = Vector2.zero;
		rootRect.sizeDelta = new Vector2(k_BarRectWidth, rectHeight);

		// 内边距跟着血条高度走，条很细时也不会把填充挤成负高度
		float padding = Mathf.Clamp(rectHeight * 0.12f, 0.5f, 8f);
		m_BarInnerWidth = Mathf.Max(1f, k_BarRectWidth - padding * 2f);
		m_BarInnerHeight = Mathf.Max(1f, rectHeight - padding * 2f);

		Image background = CreateBarImage(m_BarRoot.transform, "Background", new Color(0f, 0f, 0f, 0.6f));
		RectTransform backgroundRect = background.rectTransform;
		backgroundRect.anchorMin = new Vector2(0.5f, 0.5f);
		backgroundRect.anchorMax = new Vector2(0.5f, 0.5f);
		backgroundRect.pivot = new Vector2(0.5f, 0.5f);
		backgroundRect.anchoredPosition = Vector2.zero;
		backgroundRect.sizeDelta = new Vector2(k_BarRectWidth, rectHeight);

		m_BarFill = CreateBarImage(m_BarRoot.transform, "Fill", Color.green);
		RectTransform fillRect = m_BarFill.rectTransform;
		fillRect.anchorMin = new Vector2(0.5f, 0.5f);
		fillRect.anchorMax = new Vector2(0.5f, 0.5f);
		fillRect.pivot = new Vector2(0f, 0.5f);
		fillRect.anchoredPosition = new Vector2(-k_BarRectWidth * 0.5f + padding, 0f);
		fillRect.sizeDelta = new Vector2(m_BarInnerWidth, m_BarInnerHeight);
	}

	Image CreateBarImage(Transform parent, string name, Color color)
	{
		GameObject go = new GameObject(name);
		go.transform.SetParent(parent, false);

		Image image = go.AddComponent<Image>();
		image.color = color;
		image.raycastTarget = false;
		return image;
	}

	void RefreshBar()
	{
		if (m_BarFill == null) return;

		float fraction = HealthFraction;

		RectTransform fillRect = m_BarFill.rectTransform;
		fillRect.sizeDelta = new Vector2(m_BarInnerWidth * fraction, m_BarInnerHeight);

		// 满血绿色，逐渐变红
		m_BarFill.color = Color.Lerp(new Color(0.95f, 0.2f, 0.2f), new Color(0.25f, 0.9f, 0.35f), fraction);
	}
}
