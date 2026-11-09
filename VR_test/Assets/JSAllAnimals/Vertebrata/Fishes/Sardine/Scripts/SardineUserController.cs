using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace JSukoAnimals
{
	// 沙丁鱼：鸟群算法（分离 / 对齐 / 聚合）+ 绕鱼群放置点转圈 + VR 抓取
	public class SardineUserController : MonoBehaviour
	{
		[Header("游动")]
		[Tooltip("偏离目标时加速的快慢")]
		public float speedGain = 2.5f;
		[Tooltip("转向平滑速度")]
		public float turnSpeed = 6f;
		[Tooltip("速度/方向的平滑，越大越跟手越小越稳")]
		[Range(0.02f, 1f)]
		public float steerSmoothing = 0.25f;
		[Tooltip("模型朝向修正（度），鱼头朝向不对时调整")]
		public float yawOffset = 0f;

		[Header("鸟群算法")]
		[Tooltip("感知同伴的范围（米）")]
		public float neighborRadius = 5f;
		[Tooltip("拥挤时互相推开的距离（米）")]
		public float separationRadius = 1.2f;
		[Tooltip("分离权重：避免挤在一起")]
		public float separationWeight = 1.6f;
		[Tooltip("对齐权重：速度尽量和邻居一致")]
		public float alignmentWeight = 0.8f;
		[Tooltip("聚合权重：向鱼群中心靠拢")]
		public float cohesionWeight = 0.15f;
		[Tooltip("跟随圆周目标的权重")]
		public float seekWeight = 1f;
		[Tooltip("每条鱼最多参考的邻居数量，越小越省性能")]
		public int maxNeighbors = 16;
		[Tooltip("每隔多少个物理步计算一次鸟群邻居，越大越省性能")]
		[Range(1, 4)] public int neighborInterval = 2;

		[Header("避障")]
		[Tooltip("向前探测障碍的距离（米），0 表示不检测前方障碍")]
		public float avoidLookAhead = 1.5f;
		[Tooltip("前方探测球的半径（米）")]
		public float avoidRadius = 0.3f;
		[Tooltip("前方避障转向权重，越大躲得越急")]
		public float avoidWeight = 2f;
		[Tooltip("离地低于这个高度就开始上抬（米），0 表示不检测地面")]
		public float groundClearance = 1.2f;
		[Tooltip("离地太近时的上抬权重")]
		public float groundAvoidWeight = 2f;
		[Tooltip("参与避障的层；默认全部，但会自动忽略其他鱼")]
		public LayerMask obstacleMask = ~0;
		[Tooltip("每隔多少个物理步做一次避障检测，越大越省性能")]
		[Range(1, 10)] public int avoidInterval = 3;

		[Header("抓取")]
		[Tooltip("是否允许被手/射线抓取；鱼多时可只留一只可抓")]
		public bool grabbable = true;

		[Header("被抓后甩飞 / 扑腾")]
		[Tooltip("被抓起后，在空中飞行的时长（秒）；落地后剩下的时间用于扑腾")]
		public float flopDuration = 10f;
		[Tooltip("松手后的空气阻力，越小甩得越远")]
		public float flopDrag = 0.5f;
		[Tooltip("扑腾时的翻转力度")]
		public float flopTorque = 8f;
		[Tooltip("扑腾时的弹跳力度")]
		public float flopHopSpeed = 1.2f;
		[Tooltip("速度低于该值视为落地，开始原地扑腾")]
		public float landedSpeed = 0.4f;

		[Header("泡泡")]
		[Tooltip("泡泡模板：可直接拖 Hierarchy 里的 Bubble；留空则运行时按名字 Bubble 自动查找（Hierarchy 里的对象也行）")]
		public GameObject bubblePrefab;
		[Tooltip("从鱼头前方多远生成泡泡（米）")]
		public float bubbleForwardOffset = 0.4f;
		[Tooltip("泡泡射出速度（米/秒）")]
		public float bubbleSpeed = 12f;
		[Tooltip("泡泡存活时间（秒），到时间自动销毁")]
		public float bubbleLifetime = 5f;
		[Tooltip("泡泡的命中判定半径（米），0.25 左右比较宽容")]
		public float bubbleHitRadius = 0.28f;

		[Header("耐久")]
		[Tooltip("拿在手里最多能发射多少次泡泡，用完就会从手里掉到地上")]
		public int maxDurability = 20;
		[Tooltip("耐久条宽度（米）")]
		public float durabilityBarWidth = 0.2f;
		[Tooltip("耐久条高度（米）")]
		public float durabilityBarHeight = 0.03f;
		[Tooltip("耐久条在鱼上方的高度（米）")]
		public float durabilityBarHeightOffset = 0.15f;

		// 静态数据：整群共享的轨迹角度、共享的泡泡模板与扳机输入
		static readonly List<SardineUserController> s_fish = new List<SardineUserController>();
		static readonly HashSet<Rigidbody> s_fishBodies = new HashSet<Rigidbody>();   // 快速判断命中的碰撞体是不是鱼
		static GameObject s_bubbleTemplate;   // 没手动指定时，缓存按名字找到的场景泡泡模板
		static bool s_bubbleSearched;
		static InputAction s_leftTrigger;     // 左手食指扳机，所有鱼共用
		static InputAction s_rightTrigger;    // 右手食指扳机

		// 单条鱼的状态
		Rigidbody rb;
		XRGrabInteractable grabInteractable;
		bool isGrabbed;
		InteractorHandedness grabbingHand = InteractorHandedness.None;   // 抓着自己的手，用来区分左右扳机
		float lastFireTime = -1f;                                       // 事件与轮询去重，避免一次按下射两个

		// 耐久：只在拿在手里时消耗，用完自动脱手
		int durability;
		GameObject durabilityBar;
		Image durabilityFill;
		RectTransform durabilityFillRect;
		float durabilityInnerWidth;
		float durabilityInnerHeight;
		Transform durabilityCamera;

		bool isFlop;               // 被抓后甩飞 / 扑腾的状态
		float flopEndTime;
		float nextFlopTime;
		float originalDrag = 5f;   // 保存物体原本的阻力，甩飞结束后恢复
		CollisionDetectionMode originalCollisionMode = CollisionDetectionMode.Discrete;

		Vector3 formationOffset;   // 每只鱼在目标点周围的固定占位
		Vector3 steerDir;          // 平滑后的游动方向
		Vector3 cachedAvoid;       // 缓存的避障方向，不必每个物理步都做射线检测
		int avoidTick;             // 避障检测的错峰计数
		Vector3 cachedFlock;       // 缓存的鸟群合力，不必每个物理步都遍历邻居
		int neighborTick;          // 鸟群检测的错峰计数
		SardineGroupOrbit orbitGroup;   // 所属鱼群，提供圆心、半径、速度
		int groupId = -1;               // 鸟群规则只在同群鱼之间计算
		float currentSpeed;
		float goalLinearSpeed;     // 目标点自身的线速度

		// 视锥剔除（由 SardineGroupOrbit 调用）
		bool culled;
		Collider[] cachedColliders;
		Renderer[] cachedRenderers;
		Animator[] cachedAnimators;

		// 被抓 / 扑腾中的鱼不能剔除，否则动作会断
		public bool IsBusy => isGrabbed || isFlop;

		// 整组离开视野时暂停这条鱼的脚本、物理、动画与绘制
		public void SetCulled(bool value)
		{
			if (culled == value) return;
			if (value && IsBusy) return;
			culled = value;

			if (cachedColliders == null) cachedColliders = GetComponentsInChildren<Collider>(true);
			if (cachedRenderers == null) cachedRenderers = GetComponentsInChildren<Renderer>(true);
			if (cachedAnimators == null) cachedAnimators = GetComponentsInChildren<Animator>(true);

			for (int i = 0; i < cachedColliders.Length; i++) cachedColliders[i].enabled = !value;
			for (int i = 0; i < cachedRenderers.Length; i++) cachedRenderers[i].enabled = !value;
			for (int i = 0; i < cachedAnimators.Length; i++) cachedAnimators[i].enabled = !value;

			if (rb != null)
			{
				rb.isKinematic = value;
				if (value) rb.velocity = Vector3.zero;
			}

			// 放在最后：关掉脚本逻辑，恢复时最后再打开
			enabled = !value;
		}

		// 关闭“域重载”时静态数据会跨播放保留，进游戏时重置一下
		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
		static void ResetStatics()
		{
			s_fish.Clear();
			s_fishBodies.Clear();
			s_bubbleTemplate = null;
			s_bubbleSearched = false;

			DisposeAction(ref s_leftTrigger);
			DisposeAction(ref s_rightTrigger);
		}

		static InputAction CreateTriggerAction(string name, string binding)
		{
			var action = new InputAction(name, InputActionType.Button, binding);
			action.Enable();
			return action;
		}

		static void DisposeAction(ref InputAction action)
		{
			if (action == null) return;
			action.Disable();
			action.Dispose();
			action = null;
		}

		void OnEnable()
		{
			if (!s_fish.Contains(this)) s_fish.Add(this);
			// 剔除后重新入画时，要把刚体重新登记进鱼的集合
			if (rb != null) s_fishBodies.Add(rb);
		}

		void OnDisable()
		{
			s_fish.Remove(this);
			if (rb != null) s_fishBodies.Remove(rb);
		}

		void Start()
		{
			rb = GetComponent<Rigidbody>();

			// 用插值把 50/90Hz 的物理步平滑到渲染帧，避免头显里一格一格地跳
			if (rb != null)
			{
				rb.interpolation = RigidbodyInterpolation.Interpolate;
				originalDrag = rb.drag;
				originalCollisionMode = rb.collisionDetectionMode;
				s_fishBodies.Add(rb);
			}

			// 避障错峰：每条鱼在不同的物理步检测，避免所有鱼同一帧一起打射线
			avoidTick = Mathf.Abs(GetInstanceID()) % Mathf.Max(1, avoidInterval);
			neighborTick = Mathf.Abs(GetInstanceID()) % Mathf.Max(1, neighborInterval);

			// 屏幕外时停止骨骼变换更新，降低蒙皮动画的 CPU 开销
			Animator animator = GetComponentInChildren<Animator>();
			if (animator != null)
			{
				animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
			}

			// VR 抓取：组件缺失时自动补上
			if (grabbable)
			{
				grabInteractable = GetComponent<XRGrabInteractable>();
				if (grabInteractable == null)
				{
					grabInteractable = gameObject.AddComponent<XRGrabInteractable>();
				}
				grabInteractable.selectEntered.AddListener(OnGrabbed);
				grabInteractable.selectExited.AddListener(OnReleased);
				// 备用路径：XRI 的 Activate 事件（也是食指扳机）
				grabInteractable.activated.AddListener(OnActivated);
			}

			// 两只手的食指扳机各建一个（所有鱼共用）；具体用哪个由抓着鱼的那只手决定
			if (s_leftTrigger == null)
			{
				s_leftTrigger = CreateTriggerAction("LeftTrigger", "<XRController>{LeftHand}/{TriggerButton}");
				s_rightTrigger = CreateTriggerAction("RightTrigger", "<XRController>{RightHand}/{TriggerButton}");
			}

			// 所属鱼群：以鱼群父物体被放置的位置为圆心；组件缺失时自动补一个
			orbitGroup = GetComponentInParent<SardineGroupOrbit>();
			if (orbitGroup == null)
			{
				Transform parent = transform.parent;
				orbitGroup = parent != null
					? parent.gameObject.AddComponent<SardineGroupOrbit>()
					: gameObject.AddComponent<SardineGroupOrbit>();
			}
			orbitGroup.Setup();
		}

		// 由 SardineGroupOrbit 调用：登记分组，并按斐波那契球面分配队形占位
		public void AttachGroup(SardineGroupOrbit group, int index, int total)
		{
			orbitGroup = group;
			groupId = group.GetInstanceID();

			float golden = index * 2.399963f;
			float y = 1f - (2f * index + 1f) / Mathf.Max(1, total);
			float ringRadius = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
			formationOffset = new Vector3(
				Mathf.Cos(golden) * ringRadius,
				y * 0.45f,
				Mathf.Sin(golden) * ringRadius) * group.formationSpread;

			goalLinearSpeed = Mathf.Abs(group.orbitSpeed) * Mathf.Deg2Rad * Mathf.Max(group.orbitRadius, 0.1f);
			currentSpeed = goalLinearSpeed;
		}

		void OnDestroy()
		{
			if (grabInteractable != null)
			{
				grabInteractable.selectEntered.RemoveListener(OnGrabbed);
				grabInteractable.selectExited.RemoveListener(OnReleased);
				grabInteractable.activated.RemoveListener(OnActivated);
			}

			if (durabilityBar != null) Destroy(durabilityBar);
		}

		void OnGrabbed(SelectEnterEventArgs args)
		{
			isGrabbed = true;
			// 记住是哪只手抓的，之后只读这只手的扳机
			grabbingHand = args.interactorObject.handedness;

			// 每次拿起来都是满耐久，并显示头顶的耐久条
			durability = Mathf.Max(1, maxDurability);
			BuildDurabilityBar();
			RefreshDurabilityBar();
			SetDurabilityBarVisible(true);

			// 拿在手里时停止甩飞/扑腾，交给抓取逻辑
			isFlop = false;
			if (rb != null)
			{
				rb.useGravity = false;
				rb.drag = originalDrag;
				rb.collisionDetectionMode = originalCollisionMode;
			}
		}

		void OnReleased(SelectExitEventArgs args)
		{
			isGrabbed = false;
			SetDurabilityBarVisible(false);
			StartFlop();
		}

		void OnActivated(ActivateEventArgs args)
		{
			FireBubble();
		}

		// 抓在手里时，只响应抓着自己的那只手的食指扳机
		void Update()
		{
			if (!isGrabbed) return;

			InputAction trigger = null;
			if (grabbingHand == InteractorHandedness.Left) trigger = s_leftTrigger;
			else if (grabbingHand == InteractorHandedness.Right) trigger = s_rightTrigger;

			if (trigger != null && trigger.WasPressedThisFrame()) FireBubble();
		}

		// 拿到泡泡模板：优先用 Inspector 里指定的，没指定就在场景里按名字找（可以是不激活的隐藏对象）
		GameObject GetBubbleTemplate()
		{
			if (bubblePrefab != null) return bubblePrefab;

			if (!s_bubbleSearched)
			{
				s_bubbleSearched = true;
				foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
				{
					// 只要场景里的对象（排除工程里的预制体资源），名字叫 Bubble
					if (go.name == "Bubble" && go.scene.IsValid())
					{
						s_bubbleTemplate = go;
						break;
					}
				}
			}

			return s_bubbleTemplate;
		}

		// 拿着鱼时按扳机：从鱼头前方射出一个泡泡
		void FireBubble()
		{
			if (!isGrabbed || rb == null || durability <= 0) return;

			// 事件和轮询两条路径可能同时报到，0.1 秒内只认第一次
			if (Time.unscaledTime - lastFireTime < 0.1f) return;
			lastFireTime = Time.unscaledTime;

			GameObject template = GetBubbleTemplate();
			if (template == null)
			{
				Debug.LogWarning("SardineUserController：没找到 Bubble，请把它拖到 bubblePrefab 上，或让 Hierarchy 里的对象叫 Bubble", this);
				return;
			}

			// 鱼头朝向：当前朝向去掉 yawOffset 修正后的方向
			Quaternion headRot = rb.rotation * Quaternion.Euler(0f, -yawOffset, 0f);
			Vector3 dir = headRot * Vector3.forward;
			Vector3 spawnPos = rb.position + dir * bubbleForwardOffset;

			GameObject bubble = Instantiate(template, spawnPos, headRot);
			// 模板如果在场景里是隐藏的（不激活），克隆出来后要手动打开，否则看不见
			if (!bubble.activeSelf) bubble.SetActive(true);

			Rigidbody bubbleRb = bubble.GetComponent<Rigidbody>();
			if (bubbleRb == null) bubbleRb = bubble.AddComponent<Rigidbody>();
			bubbleRb.useGravity = false;
			bubbleRb.velocity = dir * bubbleSpeed;

			// 兜底：即使 Inspector 里把存活时间误设为 0，也保证泡泡一定会自行销毁
			Destroy(bubble, bubbleLifetime > 0f ? bubbleLifetime : 3f);

			// 泡泡飞行时自己检测是否打到鱼，打到就让那条鱼掉下去扑腾
			BubbleProjectile projectile = bubble.GetComponent<BubbleProjectile>();
			if (projectile == null) projectile = bubble.AddComponent<BubbleProjectile>();
			projectile.Setup(this, bubbleHitRadius);

			// 每成功射出一发扣一点耐久，耐久归零就从手里掉下去
			durability--;
			RefreshDurabilityBar();
			if (durability <= 0) DropFromHand();
		}

		// ---- 耐久条：世界空间 Canvas，不挂在鱼的子节点上，避免被鱼的缩放影响 ----
		const float k_BarRectWidth = 200f;

		void BuildDurabilityBar()
		{
			if (durabilityBar != null) return;

			durabilityBar = new GameObject("DurabilityBar", typeof(RectTransform), typeof(Canvas));
			Canvas canvas = durabilityBar.GetComponent<Canvas>();
			canvas.renderMode = RenderMode.WorldSpace;
			canvas.sortingOrder = 10;

			RectTransform rootRect = durabilityBar.GetComponent<RectTransform>();
			float scale = Mathf.Max(0.0001f, durabilityBarWidth) / k_BarRectWidth;
			durabilityBar.transform.localScale = new Vector3(scale, scale, scale);

			// 参数按米给，换算成画布单位
			float rectHeight = Mathf.Max(0.001f, durabilityBarHeight) / scale;
			rootRect.anchorMin = rootRect.anchorMax = new Vector2(0.5f, 0.5f);
			rootRect.pivot = new Vector2(0.5f, 0.5f);
			rootRect.anchoredPosition = Vector2.zero;
			rootRect.sizeDelta = new Vector2(k_BarRectWidth, rectHeight);

			// 内边距跟着高度走，条很细时也不会把填充挤成负高度
			float padding = Mathf.Clamp(rectHeight * 0.12f, 0.5f, 8f);
			durabilityInnerWidth = Mathf.Max(1f, k_BarRectWidth - padding * 2f);
			durabilityInnerHeight = Mathf.Max(1f, rectHeight - padding * 2f);

			CreateDurabilityImage("Background", new Color(0f, 0f, 0f, 0.65f),
				new Vector2(k_BarRectWidth, rectHeight)).transform.SetAsFirstSibling();

			durabilityFill = CreateDurabilityImage("Fill", Color.green,
				new Vector2(durabilityInnerWidth, durabilityInnerHeight));
			durabilityFillRect = durabilityFill.rectTransform;
			// 左对齐：耐久减少时从右边缩
			durabilityFillRect.pivot = new Vector2(0f, 0.5f);
			durabilityFillRect.anchoredPosition = new Vector2(-k_BarRectWidth * 0.5f + padding, 0f);
		}

		Image CreateDurabilityImage(string name, Color color, Vector2 size)
		{
			GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
			go.transform.SetParent(durabilityBar.transform, false);

			RectTransform rect = go.GetComponent<RectTransform>();
			rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
			rect.pivot = new Vector2(0.5f, 0.5f);
			rect.anchoredPosition = Vector2.zero;
			rect.sizeDelta = size;

			Image image = go.GetComponent<Image>();
			image.color = color;
			image.raycastTarget = false;
			return image;
		}

		void RefreshDurabilityBar()
		{
			if (durabilityFillRect == null) return;

			float fraction = maxDurability > 0
				? Mathf.Clamp01((float)durability / maxDurability)
				: 0f;

			durabilityFillRect.sizeDelta = new Vector2(durabilityInnerWidth * fraction, durabilityInnerHeight);
			durabilityFill.color = Color.Lerp(Color.red, Color.green, fraction);
		}

		void SetDurabilityBarVisible(bool visible)
		{
			if (durabilityBar != null) durabilityBar.SetActive(visible);
		}

		// 耐久用完：让 XRI 结束这次抓取，鱼会走正常的松手流程掉到地上扑腾
		void DropFromHand()
		{
			if (grabInteractable == null) return;

			XRInteractionManager manager = grabInteractable.interactionManager;
			if (manager == null) manager = FindFirstObjectByType<XRInteractionManager>();
			if (manager == null) return;

			manager.CancelInteractableSelection((IXRSelectInteractable)grabInteractable);
		}

		// 耐久条跟着鱼走，并且始终面向玩家
		void LateUpdate()
		{
			if (!isGrabbed || durabilityBar == null) return;

			if (durabilityCamera == null)
			{
				Camera cam = Camera.main;
				if (cam == null) cam = FindFirstObjectByType<Camera>();
				if (cam == null) return;
				durabilityCamera = cam.transform;
			}

			Vector3 anchor = transform.position + Vector3.up * durabilityBarHeightOffset;
			durabilityBar.transform.position = anchor;
			durabilityBar.transform.rotation = Quaternion.LookRotation(anchor - durabilityCamera.position, Vector3.up);
		}

		// 被泡泡射中：停下巡游速度，交给重力掉到地上扑腾
		public bool HitByBubble()
		{
			if (rb == null || isGrabbed || isFlop) return false;

			rb.velocity = Vector3.zero;
			rb.angularVelocity = Vector3.zero;
			StartFlop();
			return true;
		}

		// 松手后开启物理：保留手部甩出的速度，让它飞出去，落地后再扑腾
		void StartFlop()
		{
			if (rb == null) return;

			isFlop = true;
			flopEndTime = Time.time + flopDuration;
			nextFlopTime = Time.time;

			rb.useGravity = true;
			rb.drag = flopDrag;   // 降低阻力，才甩得远；结束后恢复
			// 高速飞行时防止穿过地面/墙面
			rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

			// 不要清空速度：XRGrabInteractable 已经把甩手速度写进 Rigidbody 了
			steerDir = Vector3.zero;
			currentSpeed = 0f;
		}

		void EndFlop()
		{
			isFlop = false;

			rb.useGravity = false;
			rb.drag = originalDrag;
			rb.collisionDetectionMode = originalCollisionMode;
			rb.velocity = Vector3.zero;
			rb.angularVelocity = Vector3.zero;
			steerDir = Vector3.zero;
			currentSpeed = 0f;
		}

		void UpdateFlop()
		{
			if (rb == null) return;

			if (Time.time >= flopEndTime)
			{
				EndFlop();
				return;
			}

			// 还在空中飞：完全交给物理，不要干扰轨迹
			bool landed = rb.velocity.magnitude < landedSpeed && Mathf.Abs(rb.velocity.y) < landedSpeed * 0.5f;

			if (landed)
			{
				// 落地后不允许水平移动，只保留原地小弹跳
				Vector3 velocity = rb.velocity;
				rb.velocity = new Vector3(0f, velocity.y, 0f);
			}

			if (Time.time >= nextFlopTime)
			{
				nextFlopTime = Time.time + Random.Range(0.12f, 0.3f);

				rb.AddTorque(new Vector3(
					Random.Range(-1f, 1f),
					Random.Range(-1f, 1f),
					Random.Range(-1f, 1f)) * flopTorque, ForceMode.VelocityChange);

				if (landed)
				{
					rb.AddForce(Vector3.up * Random.Range(flopHopSpeed * 0.5f, flopHopSpeed),
						ForceMode.VelocityChange);
				}
			}
		}

		// 鸟群规则：返回叠加到期望方向上的分离 / 对齐 / 聚合合力
		Vector3 ComputeFlockDirection(Vector3 pos)
		{
			Vector3 separation = Vector3.zero;
			Vector3 alignment = Vector3.zero;
			Vector3 cohesion = Vector3.zero;
			int count = 0;

			float neighborSqr = neighborRadius * neighborRadius;

			for (int i = 0; i < s_fish.Count; i++)
			{
				SardineUserController other = s_fish[i];
				if (other == this || other.rb == null || other.isGrabbed || other.isFlop) continue;
				// 不同鱼群的圆心相隔很远，没必要互相计算鸟群规则
				if (other.groupId != groupId) continue;

				Vector3 otherPos = other.rb.position;
				Vector3 diff = pos - otherPos;
				float sqr = diff.sqrMagnitude;
				if (sqr > neighborSqr || sqr < 0.000001f) continue;

				count++;
				cohesion += otherPos;
				alignment += other.rb.velocity;

				// 推开力随距离线性衰减，近距离也不会突然爆掉
				float dist = Mathf.Sqrt(sqr);
				if (dist < separationRadius)
				{
					separation += diff / dist * (1f - dist / separationRadius);
				}

				if (count >= maxNeighbors) break;
			}

			if (count == 0) return Vector3.zero;

			Vector3 center = cohesion / count;
			Vector3 averageVelocity = alignment / count;

			return (center - pos).normalized * cohesionWeight
				+ (averageVelocity - rb.velocity).normalized * alignmentWeight
				+ separation.normalized * separationWeight;
		}

		// 避障：沿游动方向球形探测（墙壁/树），再向下探测地面，返回叠加到期望方向上的躲避向量
		Vector3 ComputeAvoidance(Vector3 pos, Vector3 direction)
		{
			Vector3 forward = direction.sqrMagnitude > 0.0001f ? direction.normalized : steerDir;
			if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;

			Vector3 avoid = Vector3.zero;

			if (avoidLookAhead > 0f
				&& Physics.SphereCast(pos, avoidRadius, forward, out RaycastHit aheadHit,
					avoidLookAhead, obstacleMask, QueryTriggerInteraction.Ignore)
				&& !IsFishCollider(aheadHit.collider))
			{
				avoid += aheadHit.normal * avoidWeight;
			}

			// 越接近地面，向上抬的力越大，避免钻进地形
			if (groundClearance > 0f
				&& Physics.Raycast(pos, Vector3.down, out RaycastHit groundHit,
					groundClearance, obstacleMask, QueryTriggerInteraction.Ignore)
				&& !IsFishCollider(groundHit.collider))
			{
				float closeness = 1f - Mathf.Clamp01(groundHit.distance / groundClearance);
				avoid += Vector3.up * (groundAvoidWeight * closeness);
			}

			return avoid;
		}

		// 鱼之间的碰撞体不算障碍，否则鱼会互相把对方当成墙
		static bool IsFishCollider(Collider other)
		{
			Rigidbody body = other != null ? other.attachedRigidbody : null;
			return body != null && s_fishBodies.Contains(body);
		}

		void FixedUpdate()
		{
			// 被拿在手里时不参与环绕和鸟群，交还给抓取逻辑
			if (rb == null || isGrabbed) return;

			if (orbitGroup == null) return;

			// 被抓过之后：在地上扑腾，十秒内不能游动
			if (isFlop)
			{
				UpdateFlop();
				return;
			}

			// 目标点沿鱼群自己的圆周前进，整组鱼追同一个点才会聚成一“群”
			float rad = orbitGroup.AngleRadians;
			float goalY = orbitGroup.heightOffset
				+ Mathf.Sin(orbitGroup.VerticalRadians) * orbitGroup.verticalAmplitude;
			Vector3 goal = orbitGroup.Center
				+ new Vector3(Mathf.Sin(rad) * orbitGroup.orbitRadius, goalY,
					Mathf.Cos(rad) * orbitGroup.orbitRadius)
				+ formationOffset;

			Vector3 pos = rb.position;
			Vector3 seekDir = goal - pos;
			float goalDist = seekDir.magnitude;
			seekDir = goalDist > 0.0001f ? seekDir / goalDist : Vector3.zero;

			// 鸟群三条规则（分离 / 对齐 / 聚合）；按 neighborInterval 错峰执行，中间步复用上次结果
			if (++neighborTick >= Mathf.Max(1, neighborInterval))
			{
				neighborTick = 0;
				cachedFlock = ComputeFlockDirection(pos);
			}

			// 朝向圆周目标
			Vector3 direction = seekDir * seekWeight + cachedFlock;

			// 障碍避让（前方与地面）；按 avoidInterval 错峰执行，中间步复用上次结果，省掉大部分射线检测
			if (++avoidTick >= Mathf.Max(1, avoidInterval))
			{
				avoidTick = 0;
				cachedAvoid = ComputeAvoidance(pos, direction);
			}
			direction += cachedAvoid;

			// 方向做低通，避免力在每步之间来回翻导致抖动
			if (direction.sqrMagnitude > 0.0001f)
			{
				Vector3 targetDir = direction.normalized;
				steerDir = steerDir.sqrMagnitude > 0.0001f
					? Vector3.Lerp(steerDir, targetDir, steerSmoothing).normalized
					: targetDir;
			}

			// 速度不再用“距离/物理步”算，避免在目标点附近来回冲刺
			goalLinearSpeed = Mathf.Abs(orbitGroup.orbitSpeed) * Mathf.Deg2Rad
				* Mathf.Max(orbitGroup.orbitRadius, 0.1f);
			float desiredSpeed = Mathf.Min(orbitGroup.swimSpeed,
				Mathf.Max(goalLinearSpeed, goalDist * speedGain));
			currentSpeed = Mathf.Lerp(currentSpeed, desiredSpeed, steerSmoothing);

			rb.velocity = steerDir * currentSpeed;

			// 鱼头朝游动方向
			if (steerDir.sqrMagnitude > 0.0001f)
			{
				Quaternion targetRot = Quaternion.LookRotation(steerDir, Vector3.up)
					* Quaternion.Euler(0f, yawOffset, 0f);
				rb.rotation = Quaternion.Slerp(rb.rotation, targetRot,
					Mathf.Clamp01(turnSpeed * Time.fixedDeltaTime));
			}
		}
	}

	// 挂在射出的泡泡上：飞行途中扫到鱼或小怪，就让目标吃一次命中的结果
	public class BubbleProjectile : MonoBehaviour
	{
		static readonly RaycastHit[] s_hits = new RaycastHit[16];   // 复用数组，避免每帧为检测分配内存

		SardineUserController owner;   // 发射它的鱼，避免刚射出就打到自己
		float hitRadius = 0.28f;
		float bornTime;
		Vector3 lastPos;

		public void Setup(SardineUserController ownerFish, float radius)
		{
			owner = ownerFish;
			hitRadius = radius;
			bornTime = Time.unscaledTime;
			lastPos = transform.position;
		}

		void Update()
		{
			Vector3 pos = transform.position;

			// 刚射出的一小段时间内不判定，免得打中发射它的那条鱼
			if (Time.unscaledTime - bornTime < 0.15f)
			{
				lastPos = pos;
				return;
			}

			Vector3 delta = pos - lastPos;
			float dist = delta.magnitude;

			if (dist > 0.0001f)
			{
				// 球形扫掠覆盖上一帧到这一帧的整段路径，12m/s 也不会漏检
				int hitCount = Physics.SphereCastNonAlloc(lastPos, hitRadius, delta / dist, s_hits,
					dist, ~0, QueryTriggerInteraction.Collide);

				for (int i = 0; i < hitCount; i++)
				{
					SardineUserController fish = s_hits[i].collider.GetComponentInParent<SardineUserController>();
					if (fish != null && fish != owner && fish.HitByBubble())
					{
						Destroy(gameObject);
						return;
					}

					// 小怪：命中一次掉 1/3 血
					LuffyEnemy enemy = s_hits[i].collider.GetComponentInParent<LuffyEnemy>();
					if (enemy != null && enemy.TakeHit())
					{
						Destroy(gameObject);
						return;
					}
				}
			}

			lastPos = pos;
		}
	}
}
