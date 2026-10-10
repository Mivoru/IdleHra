<script lang="ts">
  // Modul: EVERY OVERLAY THAT NEEDS A SESSION, in one lazily loaded chunk.
  // They were static imports of App.svelte, so a stranger loading the login
  // form downloaded and parsed chat, the profile modal, the release notes and
  // the tutorial machinery as well. App requests this the moment a session
  // exists. Order below is DOM order, which decides stacking between equal
  // z-indexes - keep it as it was in App.
  import OnboardingCoach from './OnboardingCoach.svelte';
  import GuidedOverlay from './GuidedOverlay.svelte';
  import QuestSpotlight from './QuestSpotlight.svelte';
  import WhatsNew from './WhatsNew.svelte';
  import EventIntro from './EventIntro.svelte';
  import OfflineSummary from './OfflineSummary.svelte';
  import LootReveal from './LootReveal.svelte';
  import VictoryCard from './VictoryCard.svelte';
  import DeathCard from './DeathCard.svelte';
  import ChatDock from './ChatDock.svelte';
  import PlayerProfileModal from './PlayerProfileModal.svelte';
</script>

<!-- Modul: A BANNER THAT DOES SOMETHING.
     The old one printed "Step 1 of 3" and a sentence, and its only button
     went to Settings to turn itself off - so the one action it offered was
     to make it go away. It names the step, says WHY the step matters, and
     its main button takes the player to the screen where the thing is
     done. Pointing is the whole job.
     It is now ONE surface for both onboarding tiers - the three
     first-session steps and the seventeen discovery moments - because a
     second, differently-shaped hint box would teach the player that hints
     come in kinds. -->
<OnboardingCoach />
<GuidedOverlay />
<QuestSpotlight />

<!-- Modul: what changed since the player was last here, and whether the
     bundle this tab is running has been replaced since it loaded. Both
     live in one component - see WhatsNew.svelte for why the second waits
     for the first. -->
<WhatsNew />

<!-- The seasonal event explains itself once, after What's new closes. -->
<EventIntro />

<OfflineSummary />
<!-- Modul: the two moments the game never marked - a first boss
     clear and a death. Both are modal because both are things the
     player must not miss while looking at another screen. -->
<!-- Task 50: a Legendary+ drop, shown over any screen for a moment. -->
<LootReveal />
<VictoryCard />
<DeathCard />
<ChatDock />
<!-- The one profile host: any name opens it through stores/profile.ts. -->
<PlayerProfileModal />
