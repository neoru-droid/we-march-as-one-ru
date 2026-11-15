using System;
using System.IO;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;

[assembly: System.Reflection.AssemblyTitle("RuModRuntime")]
[assembly: System.Reflection.AssemblyDescription("Runtime localization hooks for We March as One")]
[assembly: System.Reflection.AssemblyCompany("neoru-droid")]
[assembly: System.Reflection.AssemblyProduct("We March as One RU Localization")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (c) 2026 neoru-droid")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace ShapeWar
{
    public static class RuModHooks
    {
        private const string CyrillicChars =
            "АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ" +
            "абвгдеёжзийклмнопрстуфхцчшщъыьэюя" +
            "«»№";

        public static TMP_FontAsset CreateCyrillicFontAsset(Font font)
        {
            if (font == null)
            {
                return null;
            }
            TMP_FontAsset asset = TMP_FontAsset.CreateFontAsset(
                font,
                90,
                9,
                (GlyphRenderMode)4165,
                2048,
                2048,
                AtlasPopulationMode.Dynamic,
                true);
            if (asset != null)
            {
                asset.isMultiAtlasTexturesEnabled = true;
                asset.TryAddCharacters(CyrillicChars, true);
            }
            return asset;
        }
    }

    public static class RuAchievementsHooks
    {
        public static bool IsSkillTreeOrModalBlockingEsc()
        {
            return SkillTreeUI.IsOpen || RuAchievementsModal.BlocksEsc;
        }

        public static void AfterMainMenuBuild(MainMenuUI menu)
        {
            if (menu == null)
            {
                return;
            }
            Transform home = menu.transform.Find("MainMenuCanvas/Ground/Home");
            if (home == null || home.Find("ACHIEVEMENTS") != null)
            {
                return;
            }

            RectTransform settingsRt = home.Find("SETTINGS") as RectTransform;
            RectTransform exitRt = home.Find("EXIT") as RectTransform;
            float achY = settingsRt != null ? settingsRt.anchoredPosition.y : -502f;
            float step = UIStyle.Metrics(UIStyle.PieceSize.Normal).Height + 12f;

            if (settingsRt != null)
            {
                Vector2 p = settingsRt.anchoredPosition;
                settingsRt.anchoredPosition = new Vector2(p.x, p.y - step);
            }
            if (exitRt != null)
            {
                Vector2 p = exitRt.anchoredPosition;
                exitRt.anchoredPosition = new Vector2(p.x, p.y - step);
            }

            Vector2 topLeft = new Vector2(0f, 1f);
            UIPieceButton btn = UIStyle.BuildPiece(
                "ACHIEVEMENTS",
                home,
                topLeft,
                topLeft,
                new Vector2(96f, achY),
                360f,
                UIStyle.PieceSize.Normal,
                "ДОСТИЖЕНИЯ",
                UIStyle.ButtonKind.Normal,
                new UnityAction(RuAchievementsModal.OpenFromButton));

            if (btn != null && btn.Root != null && settingsRt != null)
            {
                btn.Root.SetSiblingIndex(settingsRt.GetSiblingIndex());
            }
        }

        private static UIPieceButton _modeAchBtn;

        public static UIPieceButton FitModeHomeAndSyncAchievements(UIPieceButton home, float min)
        {
            if (home == null)
            {
                return null;
            }
            if (home.Label != null && home.Label.gameObject.activeInHierarchy)
            {
                home.Label.ForceMeshUpdate(false, false);
            }
            home.FitWidth(min);
            if (home.Root == null || home.Root.parent == null)
            {
                return home;
            }

            Transform ground = home.Root.parent;
            UIPieceButton achBtn = null;
            if (_modeAchBtn != null && _modeAchBtn.Root != null && _modeAchBtn.Root.parent == ground)
            {
                achBtn = _modeAchBtn;
            }
            else
            {
                Transform existing = ground.Find("RuModeAchievements");
                if (existing != null)
                {
                    achBtn = existing.GetComponent(typeof(UIPieceButton)) as UIPieceButton;
                }
            }

            UIStyle.PieceMetrics pm = UIStyle.Metrics(UIStyle.PieceSize.Normal);
            float measuredTextW = 0f;
            if (home.Label != null)
            {
                float prevSize = home.Label.fontSize;
                home.Label.fontSize = pm.Text;
                measuredTextW = home.Label.GetPreferredValues("ДОСТИЖЕНИЯ", 4096f, 4096f).x;
                home.Label.fontSize = prevSize;
            }
            float minWidth = Mathf.Max(190f, Mathf.Ceil(measuredTextW + 2f * pm.Pad));

            Vector2 homePos = home.Root.anchoredPosition;
            float homeRight = homePos.x + (1f - home.Root.pivot.x) * home.Root.sizeDelta.x;
            float nextX = homeRight + 16f;

            if (achBtn == null)
            {
                Vector2 bottomLeft = new Vector2(0f, 0f);
                achBtn = UIStyle.BuildPiece(
                    "RuModeAchievements",
                    ground,
                    bottomLeft,
                    bottomLeft,
                    new Vector2(nextX, homePos.y),
                    minWidth,
                    UIStyle.PieceSize.Normal,
                    "ДОСТИЖЕНИЯ",
                    UIStyle.ButtonKind.Normal,
                    new UnityAction(RuAchievementsModal.OpenFromButton));
                _modeAchBtn = achBtn;
            }

            if (achBtn != null)
            {
                _modeAchBtn = achBtn;
                if (achBtn.Label != null)
                {
                    achBtn.Label.text = "ДОСТИЖЕНИЯ";
                    if (achBtn.Label.gameObject.activeInHierarchy)
                    {
                        achBtn.Label.ForceMeshUpdate(false, false);
                    }
                }
                achBtn.FitWidth(minWidth);
                if (achBtn.Root != null)
                {
                    achBtn.Root.anchoredPosition = new Vector2(nextX, homePos.y);
                    RuModeAchButtonSync sync =
                        achBtn.Root.gameObject.GetComponent(typeof(RuModeAchButtonSync)) as RuModeAchButtonSync;
                    if (sync == null)
                    {
                        sync = achBtn.Root.gameObject.AddComponent(typeof(RuModeAchButtonSync)) as RuModeAchButtonSync;
                    }
                    if (sync != null)
                    {
                        sync.Home = home;
                        sync.HomeMinWidth = min;
                        sync.Self = achBtn;
                        sync.MinWidth = minWidth;
                    }
                }
            }

            return home;
        }
    }

    public sealed class RuModeAchButtonSync : MonoBehaviour
    {
        public UIPieceButton Home;
        public float HomeMinWidth;
        public UIPieceButton Self;
        public float MinWidth = 196f;

        private void OnEnable()
        {
            SyncLayout();
        }

        private void Start()
        {
            SyncLayout();
        }

        private void SyncLayout()
        {
            if (Home != null)
            {
                if (Home.Label != null)
                {
                    Home.Label.ForceMeshUpdate(false, false);
                }
                Home.FitWidth(HomeMinWidth);
            }
            if (Self != null)
            {
                if (Self.Label != null)
                {
                    Self.Label.ForceMeshUpdate(false, false);
                }
                Self.FitWidth(MinWidth);
                if (Home != null && Home.Root != null && Self.Root != null)
                {
                    Vector2 hp = Home.Root.anchoredPosition;
                    float right = hp.x + (1f - Home.Root.pivot.x) * Home.Root.sizeDelta.x;
                    Self.Root.anchoredPosition = new Vector2(right + 16f, hp.y);
                }
            }
        }
    }

    public sealed class RuAchievementsModal : MonoBehaviour
    {
        [Serializable]
        private sealed class LocalAchJson
        {
            public string[] unlocked = null;
        }

        private struct AchDef
        {
            public int Number;
            public string ApiId;
            public string NameRu;
            public string DescRu;

            public AchDef(int number, string apiId, string nameRu, string descRu)
            {
                Number = number;
                ApiId = apiId;
                NameRu = nameRu;
                DescRu = descRu;
            }
        }

        private sealed class RowCard
        {
            public AchDef Def;
            public RectTransform Root;
            public Image Border;
            public Image Fill;
            public Image Accent;
            public TextMeshProUGUI IndexText;
            public TextMeshProUGUI TitleText;
            public TextMeshProUGUI DescText;
            public Image BadgeBorder;
            public Image BadgeFill;
            public TextMeshProUGUI BadgeText;
        }

        private static RuAchievementsModal _instance;
        private static bool _isOpen;
        private static int _escClosedFrame = -1;

        private static readonly AchDef[] AllDefs = new AchDef[]
        {
            new AchDef(1, "WIN_FIRST", "Начало похода", "Выиграйте своё первое сражение."),
            new AchDef(2, "WIN_L5", "Первые шаги", "Победите на уровне 5."),
            new AchDef(3, "WIN_L10", "Ключи от города", "Победите на уровне 10."),
            new AchDef(4, "WIN_L20", "Назад пути нет", "Победите на уровне 20."),
            new AchDef(5, "WIN_L30", "Перевал пройден", "Победите на уровне 30."),
            new AchDef(6, "WIN_L40", "Долгий поход", "Победите на уровне 40."),
            new AchDef(7, "WIN_L50", "Мы идём как один", "Победите на уровне 50."),
            new AchDef(8, "UNIT_SECOND", "Свежее пополнение", "Откройте второй род войск."),
            new AchDef(9, "UNIT_ALL", "Полный состав", "Откройте все рода войск."),
            new AchDef(10, "TREE_HALF", "Военная наука", "Изучите наполовину древо навыков."),
            new AchDef(11, "TREE_FULL", "До последнего листа", "Полностью изучите древо навыков, открыв все улучшения."),
            new AchDef(12, "BADGE_DECISIVE", "Решительная", "Получите знак «РЕШИТЕЛЬНАЯ» на любом уровне."),
            new AchDef(13, "BADGE_HEROIC", "Героическая", "Получите знак «ГЕРОИЧЕСКАЯ» на любом уровне."),
            new AchDef(14, "LEVEL_MASTERED", "Освоено", "Получите знаки «РЕШИТЕЛЬНАЯ» и «ГЕРОИЧЕСКАЯ» на одном уровне."),
            new AchDef(15, "MASTER_1", "Латник", "Получите знаки «РЕШИТЕЛЬНАЯ» и «ГЕРОИЧЕСКАЯ» на всех уровнях с 1 по 10."),
            new AchDef(16, "MASTER_2", "Рыцарь", "Получите знаки «РЕШИТЕЛЬНАЯ» и «ГЕРОИЧЕСКАЯ» на всех уровнях с 11 по 20."),
            new AchDef(17, "MASTER_3", "Капитан", "Получите знаки «РЕШИТЕЛЬНАЯ» и «ГЕРОИЧЕСКАЯ» на всех уровнях с 21 по 30."),
            new AchDef(18, "MASTER_4", "Маршал", "Получите знаки «РЕШИТЕЛЬНАЯ» и «ГЕРОИЧЕСКАЯ» на всех уровнях с 31 по 40."),
            new AchDef(19, "MASTER_5", "Верховный маршал", "Получите знаки «РЕШИТЕЛЬНАЯ» и «ГЕРОИЧЕСКАЯ» на всех уровнях с 41 по 50."),
            new AchDef(20, "MASTER_ALL", "Завоеватель", "Получите знаки «РЕШИТЕЛЬНАЯ» и «ГЕРОИЧЕСКАЯ» на всех 50 уровнях."),
            new AchDef(21, "CITY_DECISIVE", "Взятие города", "Получите знак «РЕШИТЕЛЬНАЯ» при штурме города: на уровне 10, 20, 30, 40 или 50."),
            new AchDef(22, "FULL_MUSTER", "Полный сбор", "Завершите полный «Сбор»: откройте все рода войск и купите все улучшения вместимости армии."),
            new AchDef(23, "ONE_UNIT_TYPE", "Единый строй", "Победите на уровне 10 или выше, используя только один род войск."),
            new AchDef(24, "FIVE_UNIT_TYPES", "Общевойсковой бой", "Выиграйте бой кампании, выставив на поле пять или более родов войск."),
            new AchDef(25, "KILLS_100K", "Сто тысяч", "Сокрушите 100 000 врагов вне режима «Песочница»."),
            new AchDef(26, "KILLS_1M", "Миллион фигур", "Сокрушите 1 000 000 врагов вне режима «Песочница»."),
            new AchDef(27, "LOSE_BATTLE", "Горький урок", "Проиграйте сражение."),
            new AchDef(28, "TACTICS_FIRST", "Тактик", "Решите задачу в режиме «Тактика»."),
            new AchDef(29, "SANDBOX_BATTLE", "Чистый холст", "Начните сражение в режиме «Песочница»."),
            new AchDef(30, "COMMAND_FIRST", "Личное командование", "Выиграйте сражение в режиме «Командование».")
        };

        private GameObject _canvasGo;
        private RectTransform _contentRt;
        private ScrollRect _scrollRect;
        private TextMeshProUGUI _progressText;
        private Image _progressFill;
        private Image _progressBorder;
        private RowCard[] _rows;

        public static bool IsOpen
        {
            get { return _isOpen; }
        }

        public static bool BlocksEsc
        {
            get { return _isOpen || _escClosedFrame == Time.frameCount; }
        }

        public static void OpenFromButton()
        {
            UISfx.Confirm();
            EnsureInstance().ShowModal();
        }

        private static RuAchievementsModal EnsureInstance()
        {
            if (_instance != null)
            {
                return _instance;
            }
            GameObject go = new GameObject("RuAchievementsModal");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent(typeof(RuAchievementsModal)) as RuAchievementsModal;
            _instance.BuildModalUI();
            return _instance;
        }

        private void OnCloseClicked()
        {
            CloseModal(true);
        }

        private void OnScrimClicked()
        {
            CloseModal(true);
        }

        private void CloseModal(bool playSfx)
        {
            if (!_isOpen)
            {
                return;
            }
            _isOpen = false;
            _escClosedFrame = Time.frameCount;
            if (playSfx)
            {
                UISfx.Back();
            }
            if (_canvasGo != null)
            {
                _canvasGo.SetActive(false);
            }
        }

        private void Update()
        {
            if (!_isOpen)
            {
                return;
            }
            Keyboard kb = Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame)
            {
                CloseModal(true);
            }
        }

        private void ShowModal()
        {
            if (_canvasGo == null)
            {
                BuildModalUI();
            }
            RefreshAchievementStates();
            _canvasGo.SetActive(true);
            _isOpen = true;
            Canvas.ForceUpdateCanvases();
            if (_contentRt != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(_contentRt);
            }
            if (_scrollRect != null)
            {
                _scrollRect.verticalNormalizedPosition = 1f;
            }
        }

        private void BuildModalUI()
        {
            UIStyle.EnsureEventSystem();
            Transform canvasTr = UIStyle.NewCanvas("RuAchievementsCanvas", transform, 260, true);
            _canvasGo = canvasTr.gameObject;

            // Full-screen dark tabletop wash scrim (click outside closes modal)
            Image scrim = UIStyle.NewImage(
                "Scrim",
                canvasTr,
                new Color32(0x16, 0x15, 0x12, 0xE6));
            UIStyle.Stretch(scrim.rectTransform, 0f);
            UIStyle.Clickable(scrim.gameObject, scrim, new UnityAction(OnScrimClicked));

            Vector2 center = new Vector2(0.5f, 0.5f);
            Vector2 cardSize = new Vector2(1060f, 852f);

            // Subtle drop shadow behind modal card
            RectTransform shadowRt = UIStyle.NewRect(
                "CardShadow",
                canvasTr,
                center,
                center,
                center,
                new Vector2(0f, -10f),
                new Vector2(cardSize.x + 12f, cardSize.y + 12f));
            Image shadowImg = UIStyle.Rounded("ShadowFill", shadowRt, 20f);
            UIStyle.Stretch(shadowImg.rectTransform, 0f);
            shadowImg.color = new Color32(0x09, 0x09, 0x08, 0x9A);
            shadowImg.raycastTarget = false;

            // Main dark-slate tabletop card (#23211C body with #52545C border)
            RectTransform cardRt = UIStyle.NewRect(
                "ModalCard",
                canvasTr,
                center,
                center,
                center,
                Vector2.zero,
                cardSize);

            Image outerBorder = UIStyle.Rounded("CardBorder", cardRt, 16f);
            UIStyle.Stretch(outerBorder.rectTransform, 0f);
            outerBorder.color = new Color32(0x52, 0x54, 0x5C, 0xFF);
            outerBorder.raycastTarget = true;

            Image cardFill = UIStyle.Rounded("CardFill", cardRt, 14.5f);
            UIStyle.Stretch(cardFill.rectTransform, 1.5f);
            cardFill.color = new Color32(0x23, 0x21, 0x1C, 0xFF);
            cardFill.raycastTarget = true;

            BuildHeader(cardRt);
            BuildScrollList(cardRt);
            BuildFooter(cardRt);

            _canvasGo.SetActive(false);
        }

        private void BuildHeader(RectTransform cardRt)
        {
            RectTransform headerRt = UIStyle.NewRect(
                "Header",
                cardRt,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(0.5f, 1f),
                Vector2.zero,
                new Vector2(0f, 92f));

            TextMeshProUGUI title = UIStyle.NewText(
                "Title",
                headerRt,
                new Vector2(0f, 0.5f),
                new Vector2(0f, 0.5f),
                new Vector2(36f, 0f),
                new Vector2(620f, 60f),
                "ДОСТИЖЕНИЯ",
                44f,
                UIStyle.Shoulders,
                TextAlignmentOptions.MidlineLeft,
                new Color32(0xE8, 0xE6, 0xE1, 0xFF));
            UIStyle.SetTracking(title, 0.04f);
            title.raycastTarget = false;

            // Progress pill on the right: ОТКРЫТО: X / 30
            RectTransform pillRt = UIStyle.NewRect(
                "ProgressPill",
                headerRt,
                new Vector2(1f, 0.5f),
                new Vector2(1f, 0.5f),
                new Vector2(1f, 0.5f),
                new Vector2(-36f, 0f),
                new Vector2(256f, 42f));

            _progressBorder = UIStyle.Rounded("PillBorder", pillRt, 10f);
            UIStyle.Stretch(_progressBorder.rectTransform, 0f);
            _progressBorder.color = new Color32(0x63, 0x65, 0x70, 0xFF);
            _progressBorder.raycastTarget = false;

            _progressFill = UIStyle.Rounded("PillFill", pillRt, 9f);
            UIStyle.Stretch(_progressFill.rectTransform, 1f);
            _progressFill.color = new Color32(0x3E, 0x3F, 0x47, 0xFF);
            _progressFill.raycastTarget = false;

            _progressText = UIStyle.NewText(
                "PillLabel",
                pillRt,
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                Vector2.zero,
                new Vector2(240f, 36f),
                "ОТКРЫТО: 0 / 30",
                16.5f,
                UIStyle.DisplayBold,
                TextAlignmentOptions.Center,
                new Color32(0xF2, 0xCC, 0x6B, 0xFF));
            UIStyle.SetTracking(_progressText, 0.03f);
            _progressText.raycastTarget = false;

            // Hairline divider
            RectTransform ruleRt = UIStyle.NewRect(
                "HeaderRule",
                headerRt,
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0.5f, 0f),
                Vector2.zero,
                new Vector2(-72f, 1f));
            Image ruleImg = UIStyle.NewImage(
                "Line",
                ruleRt,
                new Color32(0x52, 0x54, 0x5C, 0x8C));
            UIStyle.Stretch(ruleImg.rectTransform, 0f);
            ruleImg.raycastTarget = false;
        }

        private void BuildScrollList(RectTransform cardRt)
        {
            RectTransform bodyRt = UIStyle.NewRect(
                "Body",
                cardRt,
                Vector2.zero,
                Vector2.one,
                new Vector2(0.5f, 0.5f),
                new Vector2(0f, 4f),
                new Vector2(-72f, -184f));

            RectTransform viewportRt = UIStyle.NewRect(
                "Viewport",
                bodyRt,
                Vector2.zero,
                Vector2.one,
                new Vector2(0f, 0.5f),
                Vector2.zero,
                new Vector2(-18f, 0f));
            viewportRt.gameObject.AddComponent(typeof(RectMask2D));

            Image hit = UIStyle.NewImage("Hit", viewportRt, Color.clear);
            UIStyle.Stretch(hit.rectTransform, 0f);
            hit.raycastTarget = true;

            _contentRt = UIStyle.NewRect(
                "Content",
                viewportRt,
                new Vector2(0f, 1f),
                Vector2.one,
                new Vector2(0.5f, 1f),
                Vector2.zero,
                Vector2.zero);

            VerticalLayoutGroup vlg =
                _contentRt.gameObject.AddComponent(typeof(VerticalLayoutGroup)) as VerticalLayoutGroup;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.spacing = 10f;
            vlg.padding = new RectOffset(2, 6, 12, 14);

            ContentSizeFitter csf =
                _contentRt.gameObject.AddComponent(typeof(ContentSizeFitter)) as ContentSizeFitter;
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // Minimal native vertical scrollbar on right edge
            RectTransform sbRt = UIStyle.NewRect(
                "Scrollbar",
                bodyRt,
                new Vector2(1f, 0f),
                Vector2.one,
                new Vector2(1f, 0.5f),
                Vector2.zero,
                new Vector2(6f, -24f));
            Image sbTrack = UIStyle.Rounded("Track", sbRt, 3f);
            UIStyle.Stretch(sbTrack.rectTransform, 0f);
            sbTrack.color = new Color32(0x2F, 0x30, 0x36, 0xCC);

            RectTransform slideArea = UIStyle.NewRect("SlidingArea", sbRt);
            UIStyle.Stretch(slideArea, 0f);

            RectTransform handleRt = UIStyle.NewRect("Handle", slideArea);
            UIStyle.Stretch(handleRt, 0f);
            Image handleImg = UIStyle.Rounded("HandleFill", handleRt, 3f);
            UIStyle.Stretch(handleImg.rectTransform, 0f);
            handleImg.color = new Color32(0x74, 0x76, 0x82, 0xFF);

            Scrollbar sb = sbRt.gameObject.AddComponent(typeof(Scrollbar)) as Scrollbar;
            sb.direction = Scrollbar.Direction.BottomToTop;
            sb.handleRect = handleRt;
            sb.targetGraphic = handleImg;
            Navigation nav = new Navigation();
            nav.mode = Navigation.Mode.None;
            sb.navigation = nav;

            _scrollRect = bodyRt.gameObject.AddComponent(typeof(ScrollRect)) as ScrollRect;
            _scrollRect.viewport = viewportRt;
            _scrollRect.content = _contentRt;
            _scrollRect.horizontal = false;
            _scrollRect.vertical = true;
            _scrollRect.movementType = ScrollRect.MovementType.Clamped;
            _scrollRect.scrollSensitivity = 42f;
            _scrollRect.verticalScrollbar = sb;
            _scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

            _rows = new RowCard[AllDefs.Length];
            for (int i = 0; i < AllDefs.Length; i++)
            {
                _rows[i] = BuildRowCard(_contentRt, AllDefs[i]);
            }
        }

        private RowCard BuildRowCard(RectTransform parent, AchDef def)
        {
            RowCard card = new RowCard();
            card.Def = def;

            RectTransform root = UIStyle.NewRect("Ach_" + def.ApiId, parent);
            LayoutElement le = root.gameObject.AddComponent(typeof(LayoutElement)) as LayoutElement;
            le.minHeight = 86f;
            le.preferredHeight = 86f;
            card.Root = root;

            card.Border = UIStyle.Rounded("Border", root, 11f);
            UIStyle.Stretch(card.Border.rectTransform, 0f);
            card.Border.raycastTarget = false;

            card.Fill = UIStyle.Rounded("Fill", root, 10f);
            UIStyle.Stretch(card.Fill.rectTransform, 1f);
            card.Fill.raycastTarget = false;

            RectTransform accentRt = UIStyle.NewRect(
                "Accent",
                root,
                new Vector2(0f, 0f),
                new Vector2(0f, 1f),
                new Vector2(0f, 0.5f),
                new Vector2(10f, 0f),
                new Vector2(5f, -22f));
            card.Accent = UIStyle.Rounded("AccentBar", accentRt, 2.5f);
            UIStyle.Stretch(card.Accent.rectTransform, 0f);
            card.Accent.raycastTarget = false;

            card.IndexText = UIStyle.NewText(
                "Index",
                root,
                new Vector2(0f, 1f),
                new Vector2(0f, 1f),
                new Vector2(28f, -15f),
                new Vector2(44f, 26f),
                def.Number.ToString("00"),
                14.5f,
                UIStyle.DisplayBold,
                TextAlignmentOptions.MidlineLeft,
                Color.white);
            card.IndexText.raycastTarget = false;

            card.TitleText = UIStyle.NewText(
                "Title",
                root,
                new Vector2(0f, 1f),
                new Vector2(0f, 1f),
                new Vector2(68f, -13f),
                new Vector2(680f, 30f),
                def.NameRu,
                20f,
                UIStyle.DisplayBold,
                TextAlignmentOptions.MidlineLeft,
                Color.white);
            card.TitleText.raycastTarget = false;

            card.DescText = UIStyle.NewText(
                "Desc",
                root,
                new Vector2(0f, 0f),
                new Vector2(0f, 0f),
                new Vector2(68f, 12f),
                new Vector2(690f, 36f),
                def.DescRu,
                15f,
                UIStyle.Display,
                TextAlignmentOptions.MidlineLeft,
                Color.white);
            card.DescText.textWrappingMode = TextWrappingModes.Normal;
            card.DescText.raycastTarget = false;

            RectTransform badgeRt = UIStyle.NewRect(
                "StatusBadge",
                root,
                new Vector2(1f, 0.5f),
                new Vector2(1f, 0.5f),
                new Vector2(1f, 0.5f),
                new Vector2(-20f, 0f),
                new Vector2(152f, 34f));

            card.BadgeBorder = UIStyle.Rounded("BadgeBorder", badgeRt, 8f);
            UIStyle.Stretch(card.BadgeBorder.rectTransform, 0f);
            card.BadgeBorder.raycastTarget = false;

            card.BadgeFill = UIStyle.Rounded("BadgeFill", badgeRt, 7f);
            UIStyle.Stretch(card.BadgeFill.rectTransform, 1f);
            card.BadgeFill.raycastTarget = false;

            card.BadgeText = UIStyle.NewText(
                "BadgeLabel",
                badgeRt,
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                Vector2.zero,
                new Vector2(140f, 28f),
                "ЗАКРЫТО",
                13.5f,
                UIStyle.DisplayBold,
                TextAlignmentOptions.Center,
                Color.white);
            UIStyle.SetTracking(card.BadgeText, 0.03f);
            card.BadgeText.raycastTarget = false;

            return card;
        }

        private void BuildFooter(RectTransform cardRt)
        {
            RectTransform footerRt = UIStyle.NewRect(
                "Footer",
                cardRt,
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0.5f, 0f),
                Vector2.zero,
                new Vector2(0f, 84f));

            RectTransform ruleRt = UIStyle.NewRect(
                "FooterRule",
                footerRt,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(0.5f, 1f),
                Vector2.zero,
                new Vector2(-72f, 1f));
            Image ruleImg = UIStyle.NewImage(
                "Line",
                ruleRt,
                new Color32(0x52, 0x54, 0x5C, 0x8C));
            UIStyle.Stretch(ruleImg.rectTransform, 0f);
            ruleImg.raycastTarget = false;

            TextMeshProUGUI hint = UIStyle.NewText(
                "EscHint",
                footerRt,
                new Vector2(0f, 0.5f),
                new Vector2(0f, 0.5f),
                new Vector2(36f, 0f),
                new Vector2(420f, 34f),
                "ESC — ЗАКРЫТЬ",
                14f,
                UIStyle.Display,
                TextAlignmentOptions.MidlineLeft,
                new Color32(0x8E, 0x90, 0x9A, 0xFF));
            UIStyle.SetTracking(hint, 0.03f);
            hint.raycastTarget = false;

            Vector2 rightMid = new Vector2(1f, 0.5f);
            UIStyle.BuildPiece(
                "CloseButton",
                footerRt,
                rightMid,
                rightMid,
                new Vector2(-36f, 0f),
                200f,
                UIStyle.PieceSize.Normal,
                "ЗАКРЫТЬ",
                UIStyle.ButtonKind.Normal,
                new UnityAction(OnCloseClicked));
        }

        private void RefreshAchievementStates()
        {
            if (_rows == null)
            {
                return;
            }

            string[] fileIds = LoadUnlockedFromDisk();
            int unlockedCount = 0;
            bool[] unlockedFlags = new bool[_rows.Length];

            for (int i = 0; i < _rows.Length; i++)
            {
                bool unlocked = CheckUnlocked(_rows[i].Def.ApiId, fileIds);
                unlockedFlags[i] = unlocked;
                if (unlocked)
                {
                    unlockedCount++;
                }
                ApplyRowVisuals(_rows[i], unlocked);
            }

            // Reorder sibling indices: unlocked items at top (in 1..30 order), then locked items
            int sibling = 0;
            for (int i = 0; i < _rows.Length; i++)
            {
                if (unlockedFlags[i])
                {
                    _rows[i].Root.SetSiblingIndex(sibling++);
                }
            }
            for (int i = 0; i < _rows.Length; i++)
            {
                if (!unlockedFlags[i])
                {
                    _rows[i].Root.SetSiblingIndex(sibling++);
                }
            }

            if (_progressText != null)
            {
                _progressText.text = "ОТКРЫТО: " + unlockedCount + " / " + _rows.Length;
                _progressText.color = unlockedCount > 0
                    ? (Color)new Color32(0xF5, 0xD0, 0x6F, 0xFF)
                    : (Color)new Color32(0xE8, 0xE6, 0xE1, 0xFF);
            }
            if (_progressBorder != null)
            {
                _progressBorder.color = unlockedCount > 0
                    ? (Color)new Color32(0xB8, 0x96, 0x48, 0xDD)
                    : (Color)new Color32(0x63, 0x65, 0x70, 0xFF);
            }
        }

        private static void ApplyRowVisuals(RowCard card, bool unlocked)
        {
            if (unlocked)
            {
                card.Border.color = new Color32(0xAD, 0x8F, 0x49, 0xCC);
                card.Fill.color = new Color32(0x46, 0x48, 0x52, 0xFF);
                card.Accent.color = new Color32(0xED, 0xC6, 0x64, 0xFF);
                card.IndexText.color = new Color32(0xED, 0xC6, 0x64, 0xFF);
                card.TitleText.color = new Color32(0xF4, 0xF2, 0xED, 0xFF);
                card.DescText.color = new Color32(0xD8, 0xD6, 0xD0, 0xFF);
                card.BadgeBorder.color = new Color32(0xC9, 0xA4, 0x49, 0xE0);
                card.BadgeFill.color = new Color32(0x54, 0x48, 0x2B, 0xFF);
                card.BadgeText.text = "✓ ОТКРЫТО";
                card.BadgeText.color = new Color32(0xFA, 0xD8, 0x7D, 0xFF);
            }
            else
            {
                card.Border.color = new Color32(0x49, 0x4B, 0x54, 0x99);
                card.Fill.color = new Color32(0x33, 0x34, 0x3B, 0xFF);
                card.Accent.color = new Color32(0x52, 0x54, 0x5E, 0xFF);
                card.IndexText.color = new Color32(0x88, 0x8A, 0x94, 0xFF);
                card.TitleText.color = new Color32(0xC8, 0xC6, 0xC0, 0xFF);
                card.DescText.color = new Color32(0x98, 0x9A, 0xA4, 0xFF);
                card.BadgeBorder.color = new Color32(0x48, 0x4A, 0x53, 0xCC);
                card.BadgeFill.color = new Color32(0x28, 0x29, 0x2F, 0xFF);
                card.BadgeText.text = "ЗАКРЫТО";
                card.BadgeText.color = new Color32(0x8C, 0x8E, 0x98, 0xFF);
            }
        }

        private static bool CheckUnlocked(string apiId, string[] diskUnlocked)
        {
            if (ContainsString(diskUnlocked, apiId))
            {
                return true;
            }
            try
            {
                if (Achievements.IsUnlocked(apiId))
                {
                    return true;
                }
            }
            catch
            {
            }
            try
            {
                if (SteamStore.Up)
                {
                    bool achieved;
                    if (SteamUserStats.GetAchievement(apiId, out achieved) && achieved)
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        private static bool ContainsString(string[] arr, string value)
        {
            if (arr == null || string.IsNullOrEmpty(value))
            {
                return false;
            }
            for (int i = 0; i < arr.Length; i++)
            {
                if (string.Equals(arr[i], value, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static string[] LoadUnlockedFromDisk()
        {
            string[] merged = new string[32];
            int count = 0;

            try
            {
                string savePath = SaveSystem.PathFor("achievements");
                count = ReadUnlockedFileInto(savePath, merged, count);
            }
            catch
            {
            }

            try
            {
                string rootDir = Application.persistentDataPath;
                if (!string.IsNullOrEmpty(rootDir))
                {
                    count = ReadUnlockedFileInto(
                        Path.Combine(rootDir, "achievements.json"),
                        merged,
                        count);
                    count = ReadUnlockedFileInto(
                        Path.Combine(Path.Combine(rootDir, "saves-launch"), "achievements.json"),
                        merged,
                        count);
                }
            }
            catch
            {
            }

            if (count == 0)
            {
                return null;
            }
            string[] trimmed = new string[count];
            Array.Copy(merged, trimmed, count);
            return trimmed;
        }

        private static int ReadUnlockedFileInto(string path, string[] buffer, int count)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return count;
            }
            string json = File.ReadAllText(path);
            if (string.IsNullOrEmpty(json))
            {
                return count;
            }
            LocalAchJson parsed = JsonUtility.FromJson(json, typeof(LocalAchJson)) as LocalAchJson;
            if (parsed != null && parsed.unlocked != null)
            {
                for (int i = 0; i < parsed.unlocked.Length; i++)
                {
                    string id = parsed.unlocked[i];
                    if (!string.IsNullOrEmpty(id) && !ContainsString(buffer, id) && count < buffer.Length)
                    {
                        buffer[count++] = id;
                    }
                }
            }
            return count;
        }
    }
}
