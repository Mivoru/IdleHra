using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul: Crafting Tree screen. The first time any of ContentRegistry's
    // 103 recipes has been visible to a player.
    //
    // The tree has always been real server-side - CommandType.InitializeCrafting
    // reaches CraftingEngine.ExecuteCraftingAsync, which consumes materials
    // through the unified backpack+stash path and enqueues a genuine
    // completion notification - but it had no HTTP endpoint, no client
    // cache and no UI, so nothing could enumerate a recipe, let alone craft
    // one. (Two separate crafting systems exist: this one, and the narrow
    // equipment-affix CraftingReceptuary the Forge screen already shows.)
    //
    // Grouped by profession with a filter strip, since 103 flat rows on a
    // portrait phone is not a browsable list. Every row states its material
    // cost against real current stock, so "why is this greyed out" is always
    // answered on the row itself.
    public class UiCraftingTreePanel : MonoBehaviour
    {
        public WebSocketClient NetworkClient;
        public AssetRegistry Registry;

        [Header("Header")]
        public TMP_Text SummaryText;
        public TMP_Text StatusText;
        public Button RefreshButton;

        [Header("Profession filter")]
        public Button[] ProfessionFilterButtons;
        public int[] ProfessionFilterValues;

        [Header("Rows - pooled")]
        public Transform RowContainer;
        public UiCraftingRecipeRow RowPrefab;
        public UiSectionHeaderRow SectionHeaderPrefab;
        public int InitialRowPoolCapacity = 20;

        private UIComponentPool<UiCraftingRecipeRow> _rowPool;
        private UIComponentPool<UiSectionHeaderRow> _headerPool;
        private readonly List<UiCraftingRecipeRow> _activeRows = new List<UiCraftingRecipeRow>();
        private readonly List<UiSectionHeaderRow> _activeHeaders = new List<UiSectionHeaderRow>();
        private readonly System.Text.StringBuilder _requirementBuilder = new System.Text.StringBuilder(160);

        // -1 shows every profession; otherwise a ContentRegistry
        // ProfessionType value.
        private int _activeProfessionFilter = CraftingTreeCache.ProfessionSmelting;
        private bool _isDirty;

        private void Awake()
        {
            if (RowPrefab != null && RowContainer != null)
            {
                _rowPool = new UIComponentPool<UiCraftingRecipeRow>(RowPrefab, RowContainer, InitialRowPoolCapacity);
            }

            if (SectionHeaderPrefab != null && RowContainer != null)
            {
                _headerPool = new UIComponentPool<UiSectionHeaderRow>(SectionHeaderPrefab, RowContainer, 6);
            }

            if (RefreshButton != null)
            {
                RefreshButton.onClick.AddListener(CraftingTreeCache.RequestSnapshot);
            }

            if (ProfessionFilterButtons != null)
            {
                for (int i = 0; i < ProfessionFilterButtons.Length; i++)
                {
                    if (ProfessionFilterButtons[i] == null) continue;

                    int filterValue = ProfessionFilterValues != null && i < ProfessionFilterValues.Length
                        ? ProfessionFilterValues[i]
                        : -1;
                    ProfessionFilterButtons[i].onClick.AddListener(() => SetProfessionFilter(filterValue));
                }
            }
        }

        private void OnEnable()
        {
            CraftingTreeCache.OnRecipesUpdated += HandleRecipesUpdated;
            CraftingTreeCache.RequestSnapshot();
            _isDirty = true;
        }

        private void OnDisable()
        {
            CraftingTreeCache.OnRecipesUpdated -= HandleRecipesUpdated;
        }

        private void Update()
        {
            if (!_isDirty) return;
            _isDirty = false;
            RebuildRows();
        }

        private void HandleRecipesUpdated()
        {
            _isDirty = true;
        }

        private void SetProfessionFilter(int professionType)
        {
            _activeProfessionFilter = professionType;
            _isDirty = true;
        }

        private void RebuildRows()
        {
            if (_rowPool == null) return;

            for (int i = 0; i < _activeRows.Count; i++) _rowPool.Despawn(_activeRows[i]);
            _activeRows.Clear();

            if (_headerPool != null)
            {
                for (int i = 0; i < _activeHeaders.Count; i++) _headerPool.Despawn(_activeHeaders[i]);
                _activeHeaders.Clear();
            }

            IReadOnlyList<CraftingRecipeData> recipes = CraftingTreeCache.Recipes;
            int playerLevel = CraftingTreeCache.PlayerLevel;

            int shown = 0;
            int craftable = 0;
            int lastProfession = int.MinValue;

            for (int i = 0; i < recipes.Count; i++)
            {
                CraftingRecipeData recipe = recipes[i];
                if (_activeProfessionFilter >= 0 && recipe.ProfessionType != _activeProfessionFilter) continue;

                if (recipe.ProfessionType != lastProfession)
                {
                    AddSectionHeader(CraftingTreeCache.GetProfessionName(recipe.ProfessionType).ToUpperInvariant());
                    lastProfession = recipe.ProfessionType;
                }

                bool levelMet = playerLevel >= recipe.RequiredLevel;
                bool hasMaterials = recipe.HasMaterials;
                if (levelMet && hasMaterials) craftable++;

                Sprite icon = null;
                Registry?.TryGetItemSprite(recipe.ResultBaseItemId, out icon);

                UiCraftingRecipeRow row = _rowPool.Spawn();
                row.Bind(
                    recipe.ResultItemId,
                    ClientContentRegistry.GetItemDisplayName(recipe.ResultBaseItemId),
                    BuildRequirementText(recipe),
                    hasMaterials,
                    levelMet,
                    recipe.RequiredLevel,
                    icon,
                    HandleCraftClicked);
                row.transform.SetAsLastSibling();
                _activeRows.Add(row);
                shown++;
            }

            if (SummaryText != null)
            {
                SummaryText.text = shown + " recipes shown   -   " + craftable + " craftable now   -   "
                    + recipes.Count + " total in the tree   -   your level " + playerLevel;
            }

            if (shown == 0)
            {
                AddSectionHeader("NO RECIPES IN THIS PROFESSION");
            }
        }

        // "3 Tin Ore (have 12)  +  1 Coal (have 0)" - the point is that the
        // player can see exactly which half of the cost they are short on,
        // rather than a single unexplained blocked button.
        private string BuildRequirementText(CraftingRecipeData recipe)
        {
            _requirementBuilder.Clear();

            if (recipe.Mat1Id > 0)
            {
                AppendMaterial(recipe.Mat1Count, recipe.Mat1BaseItemId, recipe.Mat1CurrentStock);
            }

            if (recipe.Mat2Id > 0)
            {
                if (_requirementBuilder.Length > 0) _requirementBuilder.Append("   +   ");
                AppendMaterial(recipe.Mat2Count, recipe.Mat2BaseItemId, recipe.Mat2CurrentStock);
            }

            if (_requirementBuilder.Length == 0)
            {
                _requirementBuilder.Append("No materials required");
            }

            return _requirementBuilder.ToString();
        }

        private void AppendMaterial(int required, string baseItemId, long currentStock)
        {
            _requirementBuilder
                .Append(required)
                .Append(' ')
                .Append(ClientContentRegistry.GetItemDisplayName(baseItemId))
                .Append(" (have ")
                .Append(currentStock)
                .Append(')');
        }

        private void HandleCraftClicked(int resultItemId)
        {
            if (NetworkClient == null) return;

            // CommandType.InitializeCrafting = 18. The server resolves the
            // recipe from the result item id and consumes the materials
            // itself - this is the same command the long-dead
            // UiCommandDispatcher.DispatchInitializeCrafting was written for
            // and which nothing has ever actually sent.
            NetworkClient.SendCraftingCommandZeroAlloc(18, resultItemId);

            // Modul: audio pipeline. Sounded on dispatch rather than on
            // completion: the server grants the output inside its own
            // transaction and reports back only as a refreshed snapshot, so
            // there is no discrete "craft succeeded" packet to hang this on. A
            // rejected craft still raises the error toast, which has its own
            // sound, so a failure is audibly distinct.
            GameAudioDirector.Play(GameSfx.CraftingCompleted);

            if (StatusText != null)
            {
                StatusText.text = "Crafting " + ClientContentRegistry.GetItemDisplayName(ResolveBaseId(resultItemId)) + "...";
            }

            // The craft consumes materials server-side, so the stock numbers
            // on every row that shares an input are now stale. Re-pull rather
            // than trying to predict the deduction client-side.
            CraftingTreeCache.RequestSnapshot();
        }

        private static string ResolveBaseId(int itemId)
        {
            return ClientContentRegistry.TryGetItemById(itemId, out ItemEntry item) ? item.BaseId : string.Empty;
        }

        private void AddSectionHeader(string title)
        {
            if (_headerPool == null) return;

            UiSectionHeaderRow header = _headerPool.Spawn();
            header.Bind(title);
            header.transform.SetAsLastSibling();
            _activeHeaders.Add(header);
        }
    }
}
