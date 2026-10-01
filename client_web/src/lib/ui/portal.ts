// Modul: MOVE AN OVERLAY TO <body>, because no z-index can climb out of an
// ancestor's stacking context - and some ancestors do worse than that.
//
// Two traps made this a shared action rather than PersonPicker's private
// helper:
//
// - A stacking context. `.panel`'s entrance animation keeps one for good
//   (opacity with fill-mode both), and a `position: fixed; z-index: 1401`
//   picker sheet inside it still drew UNDER the z-index 40 chat dock.
// - A containing block. `backdrop-filter`, `filter` and `transform` on an
//   ancestor make it the box a `position: fixed` descendant is laid out in.
//   The chat dock's window was blurred, so the player profile and the name
//   menu opened from chat were sized to the ~416px window instead of the
//   screen, and clipped by its `overflow: hidden`.
//
// On <body> neither can happen, whatever the layout around the component
// becomes later. The node is still owned by the component: it is removed when
// the component (or the `{#if}` around it) goes away.
export function portal(node: HTMLElement) {
  document.body.appendChild(node);
  return {
    destroy() {
      node.remove();
    },
  };
}
