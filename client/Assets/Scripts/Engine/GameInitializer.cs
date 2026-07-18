using UnityEngine;
using FolkIdle.Client.Network;
using FolkIdle.Client.UI;

namespace FolkIdle.Client.Engine
{
    // Modul: Unity UI & Network Automation, Part 3. Runtime bridge between
    // WebSocketClient and UiChatWindow - both are ordinary MonoBehaviours
    // living on scene GameObjects (see ChatSceneBuilder) with no
    // persistent-manager or singleton wiring between them, so without this
    // hook UiChatWindow.NetworkClient stays null and its own Update loop
    // (which already polls NetworkClient.ChatMessageQueue every frame -
    // see UiChatWindow.Update) never has a client to read from. Runs
    // exactly once per scene load via RuntimeInitializeOnLoadMethod, never
    // on any per-frame or per-tick path, so the two FindAnyObjectByType
    // lookups here are a one-time startup cost, not a hot-path allocation
    // concern.
    public static class GameInitializer
    {
        // Modul: caught via a live Play Mode run - UiActionBar.Awake reads
        // ClientContentRegistry.GetSkill(1..4) unconditionally to seed its
        // mana-cost labels, but nothing was ever calling
        // ClientContentRegistry.Initialize() to parse StreamingAssets/
        // GameData/skills.json first, so it threw KeyNotFoundException and
        // aborted the rest of Awake (leaving the other 3 slots unwired).
        // Must run at BeforeSceneLoad, not AfterSceneLoad - GameObject
        // Awake calls happen during scene load, before AfterSceneLoad
        // fires, so anything those Awake methods depend on has to be ready
        // strictly earlier.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InitializeClientContentRegistry()
        {
            ClientContentRegistry.Initialize();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void BindNetworkClientToChatWindow()
        {
            // Modul: FindAnyObjectByType, not FindFirstObjectByType - the
            // latter is deprecated in this Unity version (relies on
            // instance ID ordering); a live scene is expected to host at
            // most one WebSocketClient and one UiChatWindow, so which
            // match is returned in a multi-instance scenario is moot here.
            WebSocketClient networkClient = Object.FindAnyObjectByType<WebSocketClient>();
            UiChatWindow chatWindow = Object.FindAnyObjectByType<UiChatWindow>();

            if (networkClient == null || chatWindow == null)
            {
                return;
            }

            // Modul: this assignment alone completes the bridge -
            // UiChatWindow.Update already dequeues NetworkClient.
            // ChatMessageQueue unconditionally once NetworkClient is
            // non-null, so no additional event subscription is needed to
            // route incoming chat dispatches from WebSocketClient into
            // UiChatWindow.
            chatWindow.NetworkClient = networkClient;
        }
    }
}
