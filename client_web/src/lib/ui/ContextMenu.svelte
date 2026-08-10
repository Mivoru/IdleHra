<script lang="ts">

  interface Props {
    x: number;
    y: number;
    username: string;
    playerId: number;
    onClose: () => void;
    onWhisper: (username: string) => void;
    onAddFriend: (playerId: number) => void;
    onBlock: (playerId: number) => void;
    onViewProfile: (playerId: number) => void;
  }

  let {
    x,
    y,
    username,
    playerId,
    onClose,
    onWhisper,
    onAddFriend,
    onBlock,
    onViewProfile,
  }: Props = $props();

  function clickOutside(node: HTMLElement) {
    const handleClick = (event: MouseEvent) => {
      if (node && !node.contains(event.target as Node) && !event.defaultPrevented) {
        onClose();
      }
    };
    
    // Use capture phase to ensure it triggers before other things stop propagation
    document.addEventListener('click', handleClick, true);
    
    return {
      destroy() {
        document.removeEventListener('click', handleClick, true);
      }
    };
  }
</script>

<div
  class="context-menu"
  style="top: {y}px; left: {x}px;"
  use:clickOutside
>
  <div class="header">
    <strong>{username}</strong>
  </div>
  <button onclick={() => { onWhisper(username); onClose(); }}>Whisper</button>
  <button onclick={() => { onAddFriend(playerId); onClose(); }}>Add Friend</button>
  <button onclick={() => { onViewProfile(playerId); onClose(); }}>View Profile</button>
  <div class="divider"></div>
  <button class="danger" onclick={() => { onBlock(playerId); onClose(); }}>Block</button>
</div>

<style>
  .context-menu {
    position: fixed;
    z-index: 10000;
    background: var(--bg-dark, #1a1a1a);
    border: 1px solid var(--border, #333);
    border-radius: 4px;
    box-shadow: 0 4px 12px rgba(0, 0, 0, 0.5);
    min-width: 150px;
    display: flex;
    flex-direction: column;
    padding: 0.25rem 0;
  }

  .header {
    padding: 0.5rem 1rem;
    font-size: 0.85rem;
    color: var(--accent, #a68a5c);
    border-bottom: 1px solid var(--border, #333);
    margin-bottom: 0.25rem;
  }

  button {
    background: transparent;
    border: none;
    color: inherit;
    text-align: left;
    padding: 0.5rem 1rem;
    cursor: pointer;
    font-size: 0.9rem;
    border-radius: 0;
  }

  button:hover {
    background: var(--bg-hover, #2a2a2a);
  }

  button.danger {
    color: var(--danger, #ff4444);
  }

  button.danger:hover {
    background: rgba(255, 68, 68, 0.1);
  }

  .divider {
    height: 1px;
    background: var(--border, #333);
    margin: 0.25rem 0;
  }
</style>
