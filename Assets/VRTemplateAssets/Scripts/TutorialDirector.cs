using JSukoAnimals;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Locomotion;
using VRTest.Environment;

// 新手引导：视野偏上方弹字，并按玩家操作一步步推进
// 教程期间禁用移动/跳跃，结束后恢复
[AddComponentMenu("VR/Tutorial Director")]
[DefaultExecutionOrder(-2000)]
public class TutorialDirector : MonoBehaviour
{
	[Header("对话（按 A 继续）")]
	public string[] introLines =
	{
		"(@^0^@)你好",
		"你的任务是清剿这里的怪物",
		"完成任务以后有特殊CG",
		"右手抓握键可以抓鱼",
		"首先抓条鱼过来吧（别问为什么）",
	};

	[Header("操作提示")]
	public string grabPrompt = "手柄震动时，按下右手抓握键抓鱼";
	public string firePrompt = "按下扳机键发射泡泡";
	public string practicePrompt = "用你手里的鱼射击它";
	public string durabilityPrompt = "一条鱼能吐出的泡泡有限，如果用完了就该抓下一条了";
	public string scoldPrompt = "给我把鱼用到耐久归零，还让不让我教了(╯▔皿▔)╯";
	public string upgradeIntro = "接下来我教你升级";
	public string upgradePrompt = "按Y调出升级菜单，按B升级";

	[Header("升级后 / 盾牌教学")]
	public string damageIntro = "现在你从鱼肚子里挤出来的泡泡伤害更高了";
	public string radiationLine = "辐射，很神奇吧";
	public string shieldIntro = "接下来学习怎么挡住敌人的泡泡";
	public string grabShieldPrompt = "现在左手抓一只鱼";
	public string blockPrompt = "扳机键可以释放气泡盾，试试阻挡它的攻击";
	public string blockDoneIntro = "已经没什么好教的了";
	public string startTaskLine = "现在开始你的任务";
	public string farewellLine = "去你的吧";

	[Header("节奏")]
	[Tooltip("练枪阶段要累计发射的泡泡数")]
	[Min(1)] public int practiceShotCount = 10;
	[Tooltip("连续打死几只怪还没把耐久打空就换成吐槽文本")]
	[Min(1)] public int killLimitBeforeScold = 4;
	[Tooltip("练枪小怪相对玩家出生点的偏移（+Z 为出生时面朝方向），教程开始时锁定一次")]
	public Vector3 practiceSpawnOffset = new Vector3(0f, 0f, 4.5f);
	[Tooltip("教学 Boss 出现在玩家前方多远（米）")]
	[Min(2f)] public float bossSpawnDistance = 10f;
	[Tooltip("用盾挡下多少个泡泡算学会")]
	[Min(1)] public int blockTargetCount = 5;
	[Tooltip("左手抓好鱼后，Boss 等多长时间开始攻击（秒），留出看字的时间")]
	[Min(0f)] public float bossAttackDelay = 2.5f;

	[Header("文字位置")]
	public float distanceFromCamera = 1f;
	public float verticalOffset = 0.18f;
	public float panelWidth = 0.9f;
	public float panelHeight = 0.26f;
	public int mainFontSize = 34;
	public int hintFontSize = 20;

	enum Step
	{
		Intro, Grab, Fire, Practice, NextFish,
		UpgradeIntro, UpgradePrompt,
		DamageIntro, Radiation, ShieldIntro, GrabShield, BlockPractice,
		Outro1, Outro2, Outro3, Done
	}

	const float k_BaseWidth = 900f;
	const string k_Hint = "按A继续";

	Step m_Step = Step.Intro;
	int m_IntroIndex;
	int m_Shots;
	int m_PracticeKills;
	int m_Blocked;
	bool m_Scolded;
	LuffyEnemy m_PracticeEnemy;
	LuffyEnemy m_Boss;
	float m_BossFireAt;
	bool m_BossFiring;

	GameObject[] m_EnemyPrefabs;
	Vector3 m_SpawnEuler = new Vector3(270f, 270f, 0f);
	Vector3 m_PracticeSpawnPoint;

	// 教学 Boss 的配置从场景里的 BossNest 抄一份
	GameObject m_BossPrefab;
	float m_BossScale = 4f;
	float m_BossHealth = 100f;
	float m_BossTurnSpeed = 1.5f;
	GameObject m_BubbleTemplate;
	float m_BubbleInterval = 2.5f;
	float m_BubbleSpeed = 8f;
	float m_BubbleScale = 3f;
	float m_BubbleDamage = 10f;
	Vector3 m_BossEuler = new Vector3(270f, 270f, 0f);

	readonly System.Collections.Generic.List<Behaviour> m_PausedProviders =
		new System.Collections.Generic.List<Behaviour>();
	readonly System.Collections.Generic.List<EnemyNest> m_PausedNests =
		new System.Collections.Generic.List<EnemyNest>();
	readonly System.Collections.Generic.List<BossNest> m_PausedBossNests =
		new System.Collections.Generic.List<BossNest>();

	InputAction m_Continue;
	GameObject m_Panel;
	Canvas m_Canvas;
	Text m_MainText;
	Text m_HintText;
	Camera m_Camera;

	static Font s_Font;

	// 由菜单的「重新开始」置位：重载场景后跳过新手引导
	public static bool SkipTutorialOnce;

	void Awake()
	{
		// 菜单里点了「重新开始」：重载场景后直接进入正式游戏，不播教程
		if (SkipTutorialOnce)
		{
			SkipTutorialOnce = false;
			EnemyNest.SetAllSuppressed(false);
			Destroy(this);
			return;
		}

		// 先记下可用的怪模型，再把巢穴停掉，避免教程期间被围殴
		CaptureEnemyPrefabs();
		CaptureBossSettings();
		PauseWorld();

		m_Continue = new InputAction("TutorialContinue", InputActionType.Button,
			"<XRController>{RightHand}/primaryButton");
		m_Continue.AddBinding("<Keyboard>/enter");
		m_Continue.Enable();

		BuildUI();
		AttachToCamera();
	}

	void OnEnable()
	{
		SardineUserController.FishGrabbed += OnFishGrabbed;
		SardineUserController.FishReleased += OnFishReleased;
		SardineUserController.BubbleFired += OnBubbleFired;
		SardineUserController.DurabilityDepleted += OnDurabilityDepleted;
		SardineUserController.BubbleBlocked += OnBubbleBlocked;
	}

	void OnDisable()
	{
		SardineUserController.FishGrabbed -= OnFishGrabbed;
		SardineUserController.FishReleased -= OnFishReleased;
		SardineUserController.BubbleFired -= OnBubbleFired;
		SardineUserController.DurabilityDepleted -= OnDurabilityDepleted;
		SardineUserController.BubbleBlocked -= OnBubbleBlocked;
	}

	void Start()
	{
		// 教程期间玩家不扣血
		if (PlayerHealthBar.Instance != null) PlayerHealthBar.Instance.invulnerable = true;

		AttachToCamera();
		CapturePracticeSpawnPoint();
		ShowText(introLines[0], true);
	}

	void Update()
	{
		if (m_Continue != null && m_Continue.WasPressedThisFrame())
			Advance();

		CheckPracticeEnemy();

		// 玩家举好盾之后，Boss 才开始攻击
		if (m_Step == Step.BlockPractice && !m_BossFiring && m_Boss != null && Time.time >= m_BossFireAt)
		{
			m_BossFiring = true;
			m_Boss.shootBubbles = true;
		}

		// 升级成功：进入后续教学（伤害 → 辐射 → 盾牌）
		if (m_Step == Step.UpgradePrompt && PlayerScoreBoard.UpgradeLevel > 0)
		{
			m_Step = Step.DamageIntro;
			ShowText(damageIntro, true);
		}
	}

	void LateUpdate()
	{
		// 相机被换掉（重新进场景）时重新挂一下
		if (m_Panel != null && (m_Camera == null || m_Panel.transform.parent != m_Camera.transform))
			AttachToCamera();
	}

	// ---- 流程 ----

	void Advance()
	{
		if (m_Step == Step.Intro)
		{
			m_IntroIndex++;
			if (m_IntroIndex < introLines.Length)
			{
				ShowText(introLines[m_IntroIndex], true);
				return;
			}

			m_Step = Step.Grab;
			ShowText(grabPrompt, false);
			return;
		}

		if (m_Step == Step.UpgradeIntro)
		{
			m_Step = Step.UpgradePrompt;
			ShowText(upgradePrompt, false);
			return;
		}

		if (m_Step == Step.DamageIntro)
		{
			m_Step = Step.Radiation;
			ShowText(radiationLine, true);
			return;
		}

		if (m_Step == Step.Radiation)
		{
			m_Step = Step.ShieldIntro;
			ShowText(shieldIntro, true);
			return;
		}

		// 盾牌教学前，把教学 Boss 放到玩家正前方
		if (m_Step == Step.ShieldIntro)
		{
			m_Step = Step.GrabShield;
			SpawnTutorialBoss();
			ShowText(grabShieldPrompt, false);
			return;
		}

		if (m_Step == Step.Outro1)
		{
			m_Step = Step.Outro2;
			ShowText(startTaskLine, true);
			return;
		}

		if (m_Step == Step.Outro2)
		{
			m_Step = Step.Outro3;
			ShowText(farewellLine, true);
			return;
		}

		// 最后一句消失：传送到原点，结束教程
		if (m_Step == Step.Outro3)
		{
			// 传送前收起血条，传送后积分和等级归零
			if (PlayerHealthBar.Instance != null) PlayerHealthBar.Instance.SetVisible(false);

			TeleportPlayerToOrigin();
			PlayerScoreBoard.ResetProgress();
			Finish();
		}
	}

	void OnFishGrabbed(SardineUserController fish, InteractorHandedness hand)
	{
		// 左手抓鱼：举起盾牌挡住 Boss 泡泡
		if (hand == InteractorHandedness.Left)
		{
			if (m_Step != Step.GrabShield) return;

			m_Step = Step.BlockPractice;
			ShowText(blockPrompt, false);

			// 玩家准备好盾牌后 Boss 才开始攻击
			m_BossFireAt = Time.time + Mathf.Max(0f, bossAttackDelay);
			m_BossFiring = false;
			return;
		}

		if (hand != InteractorHandedness.Right) return;

		// 第一次抓到鱼：提示发射泡泡
		if (m_Step == Step.Grab)
		{
			m_Step = Step.Fire;
			ShowText(firePrompt, false);
			return;
		}

		// 文本7期间松过手，重新抓到鱼就再次显示文本7
		if (m_Step == Step.Fire)
		{
			ShowText(firePrompt, false);
			return;
		}

		// 用坏一条鱼后重新抓到下一条：进入升级教学
		if (m_Step == Step.NextFish)
		{
			m_Step = Step.UpgradeIntro;
			ShowText(upgradeIntro, true);
		}
	}

	// 文本7弹出后放开鱼：退回抓鱼提示，直到射够次数文本7正常消失
	void OnFishReleased(SardineUserController fish, InteractorHandedness hand)
	{
		if (hand != InteractorHandedness.Right) return;
		if (m_Step != Step.Fire) return;

		ShowText(grabPrompt, false);
	}

	void OnBubbleFired(SardineUserController fish)
	{
		if (m_Step != Step.Fire) return;

		m_Shots++;
		if (m_Shots < Mathf.Max(1, practiceShotCount)) return;

		// 射够次数：换成打靶练习
		m_Step = Step.Practice;
		ShowText(practicePrompt, false);
		SpawnPracticeEnemy();
	}

	void OnDurabilityDepleted(SardineUserController fish)
	{
		// 只在打靶练习阶段响应“第一条鱼用坏”
		if (m_Step != Step.Practice) return;

		m_Step = Step.NextFish;
		// 已经吐槽过就保留吐槽文本
		if (!m_Scolded) ShowText(durabilityPrompt, false);
	}

	// 盾牌挡下泡泡：数够十次就算学会
	void OnBubbleBlocked(SardineUserController fish)
	{
		if (m_Step != Step.BlockPractice) return;

		m_Blocked++;
		if (m_Blocked < Mathf.Max(1, blockTargetCount)) return;

		m_Step = Step.Outro1;
		ShowText(blockDoneIntro, true);

		// 教学结束，Boss 停火
		m_BossFiring = false;
		if (m_Boss != null) m_Boss.shootBubbles = false;
	}

	// 练枪小怪被打死：继续补一只，直到鱼耐久归零
	void CheckPracticeEnemy()
	{
		if (m_Step != Step.Practice) return;
		if (m_PracticeEnemy == null || m_PracticeEnemy.IsAlive) return;

		Destroy(m_PracticeEnemy.gameObject, 2f);
		m_PracticeEnemy = null;
		m_PracticeKills++;

		if (!m_Scolded && m_PracticeKills >= Mathf.Max(1, killLimitBeforeScold))
		{
			m_Scolded = true;
			ShowText(scoldPrompt, false);
		}

		SpawnPracticeEnemy();
	}

	void Finish()
	{
		m_Step = Step.Done;
		if (m_Panel != null) m_Panel.SetActive(false);

		// 教程结束，清掉还留着的练枪靶子和教学 Boss
		if (m_PracticeEnemy != null)
		{
			Destroy(m_PracticeEnemy.gameObject);
			m_PracticeEnemy = null;
		}
		if (m_Boss != null)
		{
			Destroy(m_Boss.gameObject);
			m_Boss = null;
		}

		// 教程结束，恢复正常受伤，并把血条放回视野
		if (PlayerHealthBar.Instance != null)
		{
			PlayerHealthBar.Instance.invulnerable = false;
			PlayerHealthBar.Instance.SetVisible(true);
		}

		ResumeWorld();
	}

	// ---- 练枪小怪 ----

	// 教程开始时按玩家出生点锁定一次生成位置，之后转头也不会跑偏
	void CapturePracticeSpawnPoint()
	{
		Transform rig = m_Camera != null ? m_Camera.transform.root : null;
		Vector3 point = rig != null
			? rig.position + rig.rotation * practiceSpawnOffset
			: practiceSpawnOffset;

		m_PracticeSpawnPoint = SnapToGround(point);
	}

	void SpawnPracticeEnemy()
	{
		if (m_EnemyPrefabs == null || m_EnemyPrefabs.Length == 0) return;

		GameObject prefab = m_EnemyPrefabs[Random.Range(0, m_EnemyPrefabs.Length)];
		if (prefab == null) return;

		Vector3 point = m_PracticeSpawnPoint;

		GameObject go = Instantiate(prefab, point, Quaternion.Euler(m_SpawnEuler));
		go.name = "TutorialEnemy";

		if (go.transform.childCount > 0)
		{
			Transform child = go.transform.GetChild(0);
			Vector3 lp = child.localPosition;
			child.localPosition = new Vector3(0f, lp.y, 0f);
		}

		LuffyEnemy enemy = go.GetComponent<LuffyEnemy>();
		if (enemy == null) enemy = go.AddComponent<LuffyEnemy>();

		// 站桩靶子：不追人、不发射
		enemy.maxHealth = 20f;
		enemy.chasePlayer = false;
		enemy.shootBubbles = false;

		m_PracticeEnemy = enemy;
	}

	// 贴地，跳过玩家和鱼群，避免把靶子放在它们身上
	Vector3 SnapToGround(Vector3 point)
	{
		Vector3 origin = new Vector3(point.x, point.y + 12f, point.z);
		RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, 40f,
			~0, QueryTriggerInteraction.Ignore);

		float best = float.MaxValue;
		float y = point.y;

		for (int i = 0; i < hits.Length; i++)
		{
			Collider collider = hits[i].collider;
			if (collider == null) continue;
			if (collider is CharacterController) continue;
			if (collider.GetComponentInParent<SardineUserController>() != null) continue;
			if (hits[i].distance >= best) continue;

			best = hits[i].distance;
			y = hits[i].point.y;
		}

		return new Vector3(point.x, y + 0.15f, point.z);
	}

	// ---- 教学 Boss ----

	// 从场景里的 BossNest 抄一份外观与泡泡参数，教学 Boss 用同一套
	void CaptureBossSettings()
	{
		BossNest nest = FindFirstObjectByType<BossNest>(FindObjectsInactive.Include);
		if (nest == null) return;

		m_BossPrefab = nest.bossPrefab;
		m_BossScale = Mathf.Max(1f, nest.bossScale);
		m_BossHealth = Mathf.Max(1f, nest.bossHealth);
		m_BossTurnSpeed = nest.bossTurnSpeed;
		m_BubbleTemplate = nest.bubbleTemplate;
		m_BubbleInterval = nest.bubbleInterval;
		m_BubbleSpeed = nest.bubbleSpeed;
		m_BubbleScale = nest.bubbleScale;
		m_BubbleDamage = nest.bubbleDamage;
		m_BossEuler = nest.spawnEuler;

		if (m_BossPrefab == null)
			Debug.LogWarning("[Tutorial] BossNest 没有配置 bossPrefab，盾牌教学无法生成 Boss");
	}

	// 在玩家正前方生成一只不动的教学 Boss：先不开火，等玩家举起盾牌再攻击
	void SpawnTutorialBoss()
	{
		if (m_BossPrefab == null || m_Camera == null) return;

		Vector3 forward = m_Camera.transform.forward;
		forward.y = 0f;
		if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
		forward.Normalize();

		Vector3 point = m_Camera.transform.position + forward * Mathf.Max(2f, bossSpawnDistance);
		point = SnapToGround(point);

		GameObject go = Instantiate(m_BossPrefab, point, Quaternion.Euler(m_BossEuler));
		go.name = "TutorialBoss";
		go.transform.localScale = Vector3.one * m_BossScale;

		if (go.transform.childCount > 0)
		{
			Transform child = go.transform.GetChild(0);
			Vector3 lp = child.localPosition;
			child.localPosition = new Vector3(0f, lp.y, 0f);
		}

		LuffyEnemy boss = go.GetComponent<LuffyEnemy>();
		if (boss == null) boss = go.AddComponent<LuffyEnemy>();

		boss.maxHealth = m_BossHealth;
		boss.turnSpeed = m_BossTurnSpeed;
		boss.chasePlayer = false;
		boss.shootBubbles = false;

		boss.bubbleTemplate = m_BubbleTemplate;
		boss.bubbleInterval = m_BubbleInterval;
		boss.bubbleSpeed = m_BubbleSpeed;
		boss.bubbleScale = m_BubbleScale;
		boss.bubbleDamage = m_BubbleDamage;
		boss.bubbleRange = Mathf.Max(12f, bossSpawnDistance + 6f);

		boss.barWidth = 0.9f * m_BossScale;
		boss.barHeight = 0.14f * m_BossScale;
		boss.barHeadOffset = 0.3f * m_BossScale;

		m_Boss = boss;
	}

	// 教程结束：把玩家（XR Origin 根节点）搬到原点
	void TeleportPlayerToOrigin()
	{
		if (m_Camera == null) return;

		Transform rig = m_Camera.transform.root;
		if (rig != null) rig.position = Vector3.zero;
	}

	// ---- 世界开关 ----

	void CaptureEnemyPrefabs()
	{
		EnemyNest[] nests = FindObjectsByType<EnemyNest>(
			FindObjectsInactive.Include, FindObjectsSortMode.None);

		for (int i = 0; i < nests.Length; i++)
		{
			if (nests[i] == null || nests[i].enemyPrefabs == null) continue;
			if (nests[i].enemyPrefabs.Length == 0) continue;

			m_EnemyPrefabs = nests[i].enemyPrefabs;
			m_SpawnEuler = nests[i].spawnEuler;
			break;
		}

		if (m_EnemyPrefabs == null)
			Debug.LogWarning("[Tutorial] 场景里没有配置小怪预制体的 EnemyNest，练枪小怪无法生成");
	}

	// 教程期间：禁掉移动 / 跳跃 / 传送，并暂停所有巢穴的刷新
	void PauseWorld()
	{
		CharacterController controller = FindFirstObjectByType<CharacterController>();
		if (controller != null)
		{
			LocomotionProvider[] providers =
				controller.GetComponentsInChildren<LocomotionProvider>(true);

			for (int i = 0; i < providers.Length; i++)
			{
				string typeName = providers[i].GetType().Name;
				bool block = typeName.Contains("Move") ||
							 typeName.Contains("Jump") ||
							 typeName.Contains("Teleport");
				if (!block || !providers[i].enabled) continue;

				m_PausedProviders.Add(providers[i]);
				providers[i].enabled = false;
			}
		}

		EnemyNest[] nests = FindObjectsByType<EnemyNest>(
			FindObjectsInactive.Include, FindObjectsSortMode.None);

		for (int i = 0; i < nests.Length; i++)
		{
			if (nests[i] == null || !nests[i].enabled) continue;
			m_PausedNests.Add(nests[i]);
			nests[i].enabled = false;
		}

		// Boss 也不要在教学期间开打：玩家被定在原地，躲不开
		BossNest[] bosses = FindObjectsByType<BossNest>(
			FindObjectsInactive.Include, FindObjectsSortMode.None);

		for (int i = 0; i < bosses.Length; i++)
		{
			if (bosses[i] == null || !bosses[i].enabled) continue;
			m_PausedBossNests.Add(bosses[i]);
			bosses[i].enabled = false;
		}
	}

	void ResumeWorld()
	{
		for (int i = 0; i < m_PausedProviders.Count; i++)
		{
			if (m_PausedProviders[i] != null) m_PausedProviders[i].enabled = true;
		}
		m_PausedProviders.Clear();

		for (int i = 0; i < m_PausedNests.Count; i++)
		{
			if (m_PausedNests[i] != null) m_PausedNests[i].enabled = true;
		}
		m_PausedNests.Clear();

		for (int i = 0; i < m_PausedBossNests.Count; i++)
		{
			if (m_PausedBossNests[i] != null) m_PausedBossNests[i].enabled = true;
		}
		m_PausedBossNests.Clear();
	}

	// ---- 文字 UI ----

	void ShowText(string text, bool showHint)
	{
		if (m_MainText == null) return;

		m_Panel.SetActive(true);
		m_MainText.text = text;
		m_HintText.gameObject.SetActive(showHint);
		if (showHint) m_HintText.text = k_Hint;
	}

	void BuildUI()
	{
		m_Panel = new GameObject("TutorialPanel", typeof(RectTransform), typeof(Canvas));
		m_Canvas = m_Panel.GetComponent<Canvas>();
		m_Canvas.renderMode = RenderMode.WorldSpace;
		m_Canvas.sortingOrder = 30;

		float scale = Mathf.Max(0.0001f, panelWidth) / k_BaseWidth;
		m_Panel.transform.localScale = Vector3.one * scale;

		float rectHeight = Mathf.Max(1f, panelHeight / scale);

		RectTransform root = m_Panel.GetComponent<RectTransform>();
		root.anchorMin = root.anchorMax = new Vector2(0.5f, 0.5f);
		root.pivot = new Vector2(0.5f, 0.5f);
		root.anchoredPosition = Vector2.zero;
		root.sizeDelta = new Vector2(k_BaseWidth, rectHeight);

		CreateImage("Background", new Color(0f, 0f, 0f, 0.45f),
			new Vector2(k_BaseWidth, rectHeight), Vector2.zero);

		m_MainText = CreateText("Main", mainFontSize,
			new Vector2(k_BaseWidth * 0.94f, rectHeight * 0.62f),
			new Vector2(0f, rectHeight * 0.1f));

		m_HintText = CreateText("Hint", hintFontSize,
			new Vector2(k_BaseWidth * 0.94f, rectHeight * 0.16f),
			new Vector2(0f, -rectHeight * 0.34f));
		m_HintText.color = new Color(1f, 1f, 1f, 0.65f);
		m_HintText.text = k_Hint;

		m_Panel.SetActive(false);
	}

	void AttachToCamera()
	{
		if (m_Panel == null) return;

		if (m_Camera == null)
		{
			m_Camera = Camera.main;
			if (m_Camera == null) m_Camera = FindFirstObjectByType<Camera>();
			if (m_Camera == null) return;
		}

		// 挂在相机下：XR 相机 LateUpdate 之后才最终定位，用世界坐标追会慢半拍
		if (m_Panel.transform.parent != m_Camera.transform)
			m_Panel.transform.SetParent(m_Camera.transform, false);

		m_Panel.transform.localPosition = new Vector3(0f, verticalOffset,
			Mathf.Max(0.1f, distanceFromCamera));
		m_Panel.transform.localRotation = Quaternion.identity;
		m_Panel.transform.localScale = Vector3.one * (panelWidth / k_BaseWidth);

		m_Canvas.worldCamera = m_Camera;
	}

	Image CreateImage(string name, Color color, Vector2 size, Vector2 anchoredPos)
	{
		GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
		go.transform.SetParent(m_Panel.transform, false);

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

	Text CreateText(string name, int fontSize, Vector2 size, Vector2 anchoredPos)
	{
		GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
		go.transform.SetParent(m_Panel.transform, false);

		RectTransform rect = go.GetComponent<RectTransform>();
		rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
		rect.pivot = new Vector2(0.5f, 0.5f);
		rect.anchoredPosition = anchoredPos;
		rect.sizeDelta = size;

		Text text = go.GetComponent<Text>();
		text.text = "";
		text.font = GetFont();
		text.fontSize = fontSize;
		text.color = Color.white;
		text.alignment = TextAnchor.MiddleCenter;
		text.horizontalOverflow = HorizontalWrapMode.Wrap;
		text.verticalOverflow = VerticalWrapMode.Overflow;
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
		if (m_Continue != null)
		{
			m_Continue.Disable();
			m_Continue.Dispose();
			m_Continue = null;
		}

		if (m_Panel != null) Destroy(m_Panel);
	}
}
