<!--
What's new in the NEXT playtest build. ship.cmd puts everything below this comment at the top of the release description, then moves it to
release/history/<version>.md and leaves only this comment here.

Rules (the text is public; ship.cmd refuses lines that break them):
- One "- " bullet per change a tester can notice, in plain words: what to press, what looks or sounds different, what to try.
- Content only: no PR or issue numbers, file or folder names of the repository, branch names, commit hashes or developer jargon.
- Say when a build does not play with earlier ones (a new network version): testers must all update.
- Leave out changes a tester cannot notice (tooling, docs, refactors). Optional "### " headings group long lists.
- Add your bullets in the same pull request as the change, when it merges.
-->
- Mapmaker: editing no longer pins your CPU once both spawn areas are on the floor. The check of whether bots can walk between the spawns and to every capture point now runs only when you press the new Check route button on the Test tab; the other checks still update after every edit.
- Warfront: in a new campaign, each battle on the war map has a weather forecast (Clear, Low Fog, Rain or Thunderstorm, or the usual storm; campaigns started before this build keep the storm everywhere). It shows on every node of the Thunderhawk's war map, and the battle there plays in that weather. Weather changes only the look and sound, never damage, movement or the Orks.
- Warfront: in a campaign on this PC (New or Continue campaign), the F6 developer panel has a Weather section to switch the weather live, remove it, or go back to the forecast. It also previews two weathers not yet in forecasts: Ash Squall (a warning, then a short crosswind of ash) and Ember Wind (drifting embers).
- Warfront: rain is thicker, with splashes and low mist, and a Thunderstorm darkens the sky and rolls thunder. Options, Graphics has a new Storm lightning choice: REDUCED (default, a soft glow in the clouds) or FULL (bright flashes, spaced apart).
- Warfront: on a held point, the Imperial Guardsmen now stand in clear ranks, with the rear squads in the gaps of the row in front.
- Warfront: Host for friends shows only what matters for a campaign (who can join, port, emblems) with a short explainer; the Classic mode, map and bot rows are gone there.
- Warfront: the Dreadnought's first stomp of a match no longer makes the game stutter.
- Warfront campaigns need this build: older builds cannot join a Warfront campaign hosted on it (Classic matches still play across both).
- Classic matches: hosting has a new Weather choice (None by default, Clear, Low Fog, Rain, Thunderstorm, Ash Squall, Ember Wind or Random for a different one each map). The weather builds up, peaks mid-match, eases before the end and starts again every round and map; the Tab scoreboard shows what is on. It only changes looks and sound. Hosting on this PC, F6 can still preview or remove it.
- Weather: under a roof or in a tunnel the rain, ash and embers stop falling near you and the weather sound turns to a muffled indoor mix (thunder still rolls, duller), fading over about a second as you step in or out. On slow machines the weather quietly thins its particles when the game stutters and brings them back when it recovers; it never changes damage, movement or the Orks.
- Warfront: the battle's wind now dulls and quietens when you step under a roof or into a tunnel, in the usual storm look and in every weather, and comes back as you walk out.
- Warfront: Clear, Low Fog and Rain no longer have the storm's ash and embers in the air, and a Thunderstorm keeps only distant dust and debris; in the usual storm look, Ash Squall and Ember Wind the falling ash and embers now stop near you under a roof, fading over a couple of seconds.
