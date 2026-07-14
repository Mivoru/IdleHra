using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using FolkIdle.Client.Engine;
using FolkIdle.Client.Network;

namespace FolkIdle.Client.UI
{
    // Modul 21: Forge crafting panel. Event-driven only - the recipe list and
    // selected-recipe detail are rebuilt from EquipmentInventoryCache.OnSnapshotUpdated
    // or a row click, never from an Update() loop.
    public class UiForgeCraftingPanel : MonoBehaviour
    {
        public EquipmentInventoryCache InventoryCache;
        public WebSocketClient NetworkClient;
        public uint CraftingSlotIndex = 0;

        [Header("Recipe List - Pooled")]
        public Transform RowContainer;
        public UiForgeRecipeRow RowPrefab;
        public int InitialRowPoolCapacity = 8;

        [Header("Selected Recipe Detail")]
        public TextMeshProUGUI SelectedRecipeNameText;
        public TextMeshProUGUI RequiredMaterialText;
        public Button CraftButton;

        [Header("Stock Color - Insufficient Material Indicator")]
        public Color SufficientStockColor = Color.white;
        public Color InsufficientStockColor = Color.red;

        private UIComponentPool<UiForgeRecipeRow> _rowPool;
        private readonly List<UiForgeRecipeRow> _activeRows = new List<UiForgeRecipeRow>();

        private readonly char[] _nameBuffer = new char[128];
        private readonly char[] _materialBuffer = new char[96];

        private int _selectedRecipeId = -1;

        private void Awake()
        {
            if (RowPrefab != null && RowContainer != null)
            {
                _rowPool = new UIComponentPool<UiForgeRecipeRow>(RowPrefab, RowContainer, InitialRowPoolCapacity);
            }

            if (CraftButton != null)
            {
                CraftButton.onClick.AddListener(HandleCraftClicked);
            }
        }

        private void OnEnable()
        {
            if (InventoryCache == null) return;

            InventoryCache.OnSnapshotUpdated += HandleSnapshotUpdated;
            InventoryCache.RequestSnapshot();
        }

        private void OnDisable()
        {
            if (InventoryCache == null) return;

            InventoryCache.OnSnapshotUpdated -= HandleSnapshotUpdated;
        }

        private void HandleSnapshotUpdated()
        {
            RefreshRecipeList();
            RefreshSelectedRecipeDetail();
        }

        private void RefreshRecipeList()
        {
            if (_rowPool == null || InventoryCache == null) return;

            for (int i = 0; i < _activeRows.Count; i++)
            {
                _rowPool.Despawn(_activeRows[i]);
            }
            _activeRows.Clear();

            IReadOnlyList<ForgeRecipeData> recipes = InventoryCache.Recipes;

            if (_selectedRecipeId < 0 && recipes.Count > 0)
            {
                _selectedRecipeId = recipes[0].RecipeId;
            }

            for (int i = 0; i < recipes.Count; i++)
            {
                ForgeRecipeData recipe = recipes[i];
                UiForgeRecipeRow row = _rowPool.Spawn();
                row.Bind(recipe.RecipeId, recipe.ResultBaseItemId, recipe.TierIndex, recipe.RecipeId == _selectedRecipeId, HandleRecipeSelected);
                _activeRows.Add(row);
            }
        }

        private void HandleRecipeSelected(int recipeId)
        {
            if (_selectedRecipeId == recipeId) return;

            _selectedRecipeId = recipeId;
            RefreshRecipeList();
            RefreshSelectedRecipeDetail();
        }

        private void RefreshSelectedRecipeDetail()
        {
            ForgeRecipeData selected = FindSelectedRecipe();

            if (selected == null)
            {
                if (SelectedRecipeNameText != null) SelectedRecipeNameText.SetCharArray(System.Array.Empty<char>(), 0, 0);
                if (RequiredMaterialText != null) RequiredMaterialText.SetCharArray(System.Array.Empty<char>(), 0, 0);
                if (CraftButton != null) CraftButton.interactable = false;
                return;
            }

            if (SelectedRecipeNameText != null)
            {
                int offset = WriteTextToBuffer(_nameBuffer, 0, selected.ResultBaseItemId);
                SelectedRecipeNameText.SetCharArray(_nameBuffer, 0, offset);
            }

            bool hasEnoughMaterial = selected.CurrentMaterialStock >= selected.MaterialCost;

            if (RequiredMaterialText != null)
            {
                int offset = WriteLongToBuffer(_materialBuffer, 0, selected.CurrentMaterialStock);
                offset = WriteTextToBuffer(_materialBuffer, offset, " / ");
                offset = WriteIntToBuffer(_materialBuffer, offset, selected.MaterialCost);
                offset = WriteTextToBuffer(_materialBuffer, offset, " ");
                offset = WriteTextToBuffer(_materialBuffer, offset, selected.MaterialName);
                RequiredMaterialText.SetCharArray(_materialBuffer, 0, offset);
                RequiredMaterialText.color = hasEnoughMaterial ? SufficientStockColor : InsufficientStockColor;
            }

            if (CraftButton != null)
            {
                CraftButton.interactable = hasEnoughMaterial;
            }
        }

        private ForgeRecipeData FindSelectedRecipe()
        {
            if (InventoryCache == null) return null;

            IReadOnlyList<ForgeRecipeData> recipes = InventoryCache.Recipes;
            for (int i = 0; i < recipes.Count; i++)
            {
                if (recipes[i].RecipeId == _selectedRecipeId) return recipes[i];
            }
            return null;
        }

        private void HandleCraftClicked()
        {
            if (NetworkClient == null || _selectedRecipeId < 0) return;

            NetworkClient.SendEquipmentCraftingCommandZeroAlloc((uint)_selectedRecipeId, CraftingSlotIndex);
            Invoke(nameof(RefreshAfterCraft), 0.5f);
        }

        private void RefreshAfterCraft()
        {
            if (InventoryCache != null) InventoryCache.RequestSnapshot();
        }

        private static int WriteTextToBuffer(char[] buffer, int offset, string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                buffer[offset++] = text[i];
            }
            return offset;
        }

        private static int WriteIntToBuffer(char[] buffer, int offset, int value)
        {
            return (int)WriteLongToBuffer(buffer, offset, value);
        }

        private static int WriteLongToBuffer(char[] buffer, int offset, long value)
        {
            if (value == 0)
            {
                buffer[offset++] = '0';
                return offset;
            }

            if (value < 0)
            {
                buffer[offset++] = '-';
                value = -value;
            }

            long temp = value;
            int length = 0;
            while (temp > 0)
            {
                temp /= 10;
                length++;
            }

            int endOffset = offset + length;
            temp = value;
            for (int i = endOffset - 1; i >= offset; i--)
            {
                buffer[i] = (char)('0' + (temp % 10));
                temp /= 10;
            }
            return endOffset;
        }
    }
}
