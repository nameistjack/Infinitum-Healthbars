# Infinitum Health Bars — Documentation

##  Overview

Infinitum Health Bars displays a real-time CUI health bar above your HUD when you look at any damageable entity. It supports a wide range of entity types — NPCs, animals, helicopters, Bradley, vehicles, barrels, crates, doors, turrets, and players — each with configurable icons, a dynamic color-coded health bar, and optional elite gold border for high-priority targets.



Extended-permission users additionally see the entity's distance and a live TTK (time-to-kill) estimate based on recent DPS.



## Requirements

Dependency

*ImageLibrary    Optional*       : Required for custom entity icons. Without it the icon panel is hidden.

Installation



* Drop InfinitumHealthBars.cs into your oxide/plugins/ folder.

* The plugin generates a default config at oxide/config/InfinitumHealthBars.json on first load.

* (Optional) Install ImageLibrary and populate icon URLs in the config (see Icons section below).

* Grant the extended permission to players or groups who should see distance and TTK.



## Permissions



`infinitumhealthbars.extended`



"Shows distance label and TTK estimate. Standard players see health bar   icon only."



Grant via console:



`oxide.grant group vip infinitumhealthbars.extended`

`oxide.grant user STEAMID infinitumhealthbars.extended`



```

## Configuration



{

  // ── Entity toggles ─────────────────────────────────────────────

  // Set any to false to skip that category entirely.



  "ShowForNPCs":        true,   // Scientists, tunnel dwellers, bandits, custom NPCs

  "ShowForAnimals":     true,   // Bear, wolf, boar, chicken, shark, croc, panther, etc.

  "ShowForPlayers":     false,  // Other players — enable on PvP servers

  "ShowForHelicopters": true,   // Patrol heli, attack heli, CH47

  "ShowForBradleys":    true,   // Bradley APC

  "ShowForBarrels":     true,   // Loot barrels, oil barrels

  "ShowForDoors":       true,   // All door types

  "ShowForTurrets":     true,   // Auto turret, flame turret, SAM site, gun trap

  "ShowForStorage":     false,  // Crates and boxes — disabled by default (high entity count)

  "ShowForVehicles":    true,   // Cars, boats, helis, subs, snowmobiles



  // ── Performance ────────────────────────────────────────────────



  "UpdateInterval":            0.2,   // Seconds between raycast   UI refresh per player (recommended 0.1–0.3)

  "RaycastDistance":           100.0, // Standard raycast range in metres

  "RaycastDistanceLong":       1500.0,// Extended range for helicopters and Bradley

  "HeliAngleDetectionRange":   500.0, // Max distance for angle-based heli fallback if raycast misses

  "SphereCastRadius":          2.0,   // Sphere radius for the cast — increase for large entities (Bradley, tugboat)

  "HealthChangePctThreshold":  0.5,   // Min HP change (% of max) before CUI refreshes — raise to reduce flicker



  // ── Colours (Rust RGBA format: "R G B A", each channel 0.0–1.0) ─



  "BarColorHigh":       "0.2 0.8 0.25 1.0",   // HP > 60%

  "BarColorMedium":     "0.85 0.72 0.1 1.0",  // HP 25%–60%

  "BarColorLow":        "0.82 0.18 0.12 1.0", // HP < 25%

  "BackgroundColor":    "0.07 0.07 0.07 0.93",// Outer panel background

  "BarBackgroundColor": "0.03 0.03 0.03 0.88",// Bar track background

  "EliteBorderColor":   "1 0.84 0 1",         // Gold border for elite entities



  // ── Position & size (fractions of screen: 0.0–1.0) ─────────────



  "PositionX": 0.4,   // Horizontal centre of the bar

  "PositionY": 0.75,  // Vertical position (0 = bottom, 1 = top)

  "Width":     0.2,   // Width of the bar panel

  "Height":    0.03,  // Height of the bar panel



  // ── UX ─────────────────────────────────────────────────────────



  "CompactMode":       false, // Hides icon and HP number — shows bar strip only

  "ShowDistanceLabel": false, // Show distance for all players (extended perm always sees it)



  // ── Elite keys ─────────────────────────────────────────────────

  // Entities whose key matches any entry here get a gold border.

  // Use /hbdebug while looking at an entity to find its key.



  "EliteKeys": [

    "scientistnpc_heavy",

    "bradleyapc",

    "codelockedhackablecrate",

    "patrolhelicopter",

    "attackhelicopter"

  ],



  // ── Icons ───────────────────────────────────────────────────────

  // Map a prefab key to a direct image URL. Empty string = no icon.

  // Use imgur or Discord CDN — wiki.rustclash.com / rustlabs.com return 403.

  // Use /hbdebug while looking at an entity to find its key.



  "DefaultIcon": "[MEDIA=imgur]6gmZOYm[/MEDIA]", // Fallback when no key matches

  "EntityIcons": {

    "bear":               "[MEDIA=imgur]yourlink[/MEDIA]",

    "scientistnpc_heavy": "[MEDIA=imgur]THOBLOC[/MEDIA]",

    "bradleyapc":         "",

    "..."

  }

}





## Admin Commands



**/hbdebug**    "Aim at any entity and run this command. Prints entity Type, Prefab, Layer, IsAnimalNPC, and IsAnimalByPrefab to chat. Use this to find the correct key for a new entity type. Admin only."



## FAQ



  Q: Icons show a broken image or nothing.

  A: Either ImageLibrary is not installed, the URL returns a 403, or the image hasn't finished downloading yet. Use oxide.reload ImageLibrary then oxide.reload InfinitumHealthBars after adding new URLs.



  Q: The bar flickers or updates too fast.

  A: Increase UpdateInterval (e.g. 0.3) or raise HealthChangePctThreshold (e.g. 1.0).



  Q: The bar appears in the wrong position.

  A: Adjust PositionX / PositionY / Width / Height in the config. Values are 0–1 fractions of screen size.



  Q: I want a minimal bar with no icon or numbers.

  A: Set "CompactMode": true.



## Changelog



Version Changes



1.4.1

* Fixed: dictionaries and timer are now fully cleared on plugin unload, preventing memory leaks on reload

* Fixed: removed System.Reflection usage (GetType().Name) in the debug command — replaced with ShortPrefabName

* Added: // Requires: ImageLibrary dependency declaration

* Added: MIT license header with author credits

* Improved: full inline documentation in the Configuration class

1.4.0

* Added prefab-based animal fallback for wolf, crocodile, panther.

* Added /hbdebug admin command.

* Improved heli angle detection. Per-player DPS cache with reverse entity-watcher index.

1.3.0

* Added TTK (time-to-kill) estimate, distance label, HealthChangePctThreshold optimisation.

1.2.0

*Elite border system, configurable EliteKeys list.

1.1.0

* ImageLibrary icon support, EntityIcons config dictionary.

1.0.0    Initial release.
