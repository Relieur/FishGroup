using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

// 挂在 YG_01 小怪上：随机出现在玩家视野内 + 头顶血条 + 被泡泡命中三次死亡 + 可抓取
[AddComponentMenu("VR/Luffy Enemy")]
[DisallowMultipleComponent]
[DefaultExecutionOrder(-1000)]   // 碰撞体要在 XRGrabInteractable 之前建好
public class LuffyEnemy : MonoBehaviour
{
	[Header("血量")]
	[Tooltip("被泡泡命中多少次后死亡；3 次 = 每命中一次掉 1/3 血")]
	[Min(1)] public int maxHits = 3;
	[Tooltip("死亡后隔多久重新出现在玩家视野内（秒）")]
	public float respawnDelay = 3f;

	[Header("随机出现在玩家视野内")]
	[Tooltip("取消勾选后不会自动改变位置，只保留血条与受击逻辑")]
	public bool spawnInView = true;
	[Tooltip("离头显的最近距离（米）")]
	public float viewDistanceMin = 4f;
	[Tooltip("离头显的最远距离（米）")]
	public float viewDistanceMax = 12f;
	[Tooltip("水平视野半角（度），左右各多少度")]
	[Range(10f, 80f)] public float viewHalfAngle = 45f;
	[Tooltip("上下机位偏移（度），没有地面命中时用来决定生成高度")]
	[Range(0f, 45f)] public float viewHalfPitch = 15f;
	[Tooltip("向下找地面时射线起点离生成点的高度（米）")]
	public float groundRayHeight = 10f;
	[Tooltip("贴地后的额外高度偏移（米）")]
	public float groundOffset = 0.15f;
	[Tooltip("生成点尝试次数，全部被遮挡时使用最后一次结果")]
	[Min(1)] public int placementAttempts = 10;

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

	const float k_BarRectWidth = 200f;   // 血条画布的参考宽度，实际米数由 barWidth 换算

	float m_Hits;   // 用浮点，摔伤可以扣半格
	bool m_Dead;
	float m_RespawnTime;

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

		PlaceInPlayerView();
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

		m_Hits += Mathf.Max(1, maxHits) * Mathf.Clamp01(throwDamageRatio);
		RefreshBar();

		if (m_Hits >= maxHits) Die();
	}

	void Update()
	{
		if (m_Dead)
		{
			if (Time.time < m_RespawnTime) return;

			// 复活：回到满血，并在玩家视野里换一个随机位置
			m_Dead = false;
			m_Hits = 0f;
			ResetThrownState();
			SetVisible(true);
			RefreshBar();
			PlaceInPlayerView();
			return;
		}

		// 被拿在手里或还在空中飞的时候不自己走
		if (chasePlayer && !m_Grabbed && !m_Thrown) ChasePlayer();

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

	// 泡泡命中时调用：每命中一次掉 1/maxHits 血；返回 true 表示这发泡泡被消耗
	public bool TakeHit()
	{
		if (m_Dead) return false;

		m_Hits += 1f;
		RefreshBar();

		if (m_Hits >= maxHits) Die();
		return true;
	}

	void Die()
	{
		m_Dead = true;
		// 杀一只小怪加一次分
		PlayerScoreBoard.NotifyKill();
		m_RespawnTime = Time.time + Mathf.Max(0f, respawnDelay);

		// 死在手里就先脱手，避免抓着一团空气
		if (m_Grabbed) DropFromHand();
		ResetThrownState();
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

	// 在头显前方的水平视野内随机挑一个位置，并贴到地面
	void PlaceInPlayerView()
	{
		if (!spawnInView || m_Camera == null) return;

		Vector3 cameraPosition = m_Camera.position;
		Vector3 forward = m_Camera.forward;
		forward.y = 0f;
		if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
		forward.Normalize();

		float minDistance = Mathf.Min(viewDistanceMin, viewDistanceMax);
		float maxDistance = Mathf.Max(viewDistanceMin, viewDistanceMax);

		Vector3 lastCandidate = transform.position;

		for (int i = 0; i < Mathf.Max(1, placementAttempts); i++)
		{
			float yaw = Random.Range(-viewHalfAngle, viewHalfAngle);
			float pitch = Random.Range(-viewHalfPitch, viewHalfPitch);
			float distance = Random.Range(minDistance, maxDistance);

			Vector3 direction = Quaternion.AngleAxis(yaw, Vector3.up) * forward;
			Vector3 candidate = cameraPosition + direction * distance;
			candidate.y = cameraPosition.y + Mathf.Tan(pitch * Mathf.Deg2Rad) * distance;

			candidate = SnapToGround(candidate);
			lastCandidate = candidate;

			// 只考虑真正看得到的位置，被树等挡住就重新随机
			if (IsVisibleFromCamera(cameraPosition, candidate))
			{
				transform.position = candidate;
				return;
			}
		}

		transform.position = lastCandidate;
	}

	// 向下打射线贴地：取最远（最低）的命中点，避免踩在水面之类的上层碰撞体上
	Vector3 SnapToGround(Vector3 point)
	{
		Vector3 origin = new Vector3(point.x, point.y + groundRayHeight, point.z);
		float maxDistance = groundRayHeight + Mathf.Abs(point.y) + 50f;

		RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, maxDistance,
			~0, QueryTriggerInteraction.Ignore);

		bool found = false;
		float farthest = 0f;
		Vector3 groundPoint = point;

		for (int i = 0; i < hits.Length; i++)
		{
			Collider collider = hits[i].collider;
			if (collider == null) continue;
			if (collider.transform.IsChildOf(transform)) continue;
			if (m_PlayerRoot != null && collider.transform.IsChildOf(m_PlayerRoot)) continue;

			if (!found || hits[i].distance > farthest)
			{
				found = true;
				farthest = hits[i].distance;
				groundPoint = hits[i].point;
			}
		}

		if (!found) return point;

		return new Vector3(point.x, groundPoint.y + groundOffset, point.z);
	}

	bool IsVisibleFromCamera(Vector3 cameraPosition, Vector3 target)
	{
		Vector3 end = target + Vector3.up * 0.5f;
		Vector3 delta = end - cameraPosition;
		float distance = delta.magnitude;
		if (distance < 0.001f) return true;

		RaycastHit[] hits = Physics.RaycastAll(cameraPosition, delta / distance, distance,
			~0, QueryTriggerInteraction.Ignore);

		for (int i = 0; i < hits.Length; i++)
		{
			Collider collider = hits[i].collider;
			if (collider == null) continue;
			if (collider.transform.IsChildOf(transform)) continue;
			if (m_PlayerRoot != null && collider.transform.IsChildOf(m_PlayerRoot)) continue;

			// 中间被别的东西挡住，这个位置对玩家不可见
			return false;
		}

		return true;
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

		float fraction = Mathf.Clamp01(1f - (float)m_Hits / Mathf.Max(1, maxHits));

		RectTransform fillRect = m_BarFill.rectTransform;
		fillRect.sizeDelta = new Vector2(m_BarInnerWidth * fraction, m_BarInnerHeight);

		// 满血绿色，逐渐变红
		m_BarFill.color = Color.Lerp(new Color(0.95f, 0.2f, 0.2f), new Color(0.25f, 0.9f, 0.35f), fraction);
	}
}
