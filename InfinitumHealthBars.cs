// ============================================================
//  Infinitum Health Bars
//  Author  : LemmyMaverick
//  Version : 1.4.1
//  License : MIT
//
//  MIT License
//  Copyright (c) 2026 LemmyMaverick
//
//  Permission is hereby granted, free of charge, to any person obtaining
//  a copy of this software and associated documentation files (the
//  "Software"), to deal in the Software without restriction, including
//  without limitation the rights to use, copy, modify, merge, publish,
//  distribute, sublicense, and/or sell copies of the Software, and to
//  permit persons to whom the Software is furnished to do so, subject to
//  the following conditions:
//
//  The above copyright notice and this permission notice shall be included
//  in all copies or substantial portions of the Software.
//
//  THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
//  EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
//  MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
//  IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY
//  CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
//  TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
//  SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
//  Developed with the assistance of Claude (Anthropic) — AI pair programming.
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Infinitum Health Bars", "LemmyMaverick", "1.4.1")]
    [Description("Displays a polished health bar for the entity you are looking at, with icons, elite highlighting, distance and TTK for extended users")]
    class InfinitumHealthBars : RustPlugin
    {
        #region Fields

        [PluginReference] private Plugin ImageLibrary;

        private const string LayerName    = "InfinitumHealthBarUI";
        private const string PermExtended = "infinitumhealthbars.extended";

        private Dictionary<ulong, TrackedEntity>            playerTargets   = new Dictionary<ulong, TrackedEntity>();
        private Dictionary<ulong, string>                   _iconKeyCache   = new Dictionary<ulong, string>();
        // Per-player DPS cache keyed by entity net ID — survives losing/regaining line of sight
        private Dictionary<ulong, Dictionary<ulong, float>> _dpsCache       = new Dictionary<ulong, Dictionary<ulong, float>>();
        // Reverse index: entityNetId → set of playerIds watching it — O(1) lookup in OnEntityTakeDamage
        private Dictionary<ulong, HashSet<ulong>>           _entityWatchers = new Dictionary<ulong, HashSet<ulong>>();
        private Timer raycastTimer;

        private class TrackedEntity
        {
            public BaseCombatEntity Entity;
            public ulong            EntityNetId;   // cached to avoid net?.ID.Value every frame
            public float LastHealth;
            public float MaxHealth;
            public float LastDPS;
            public float LastDamageTime;
            public bool  HasExtended;              // cached permission check — avoids string lookup every UpdateUI
        }

        #endregion

        #region Configuration

        private Configuration config;

        private class Configuration
        {
            // ── Entity toggles ────────────────────────────────────────────────────────
            // Set any of these to false to completely skip that entity category.

            public bool ShowForNPCs        = true;   // Scientists, tunnel dwellers, bandits, custom NPCs
            public bool ShowForAnimals     = true;   // Bear, wolf, boar, chicken, shark, croc, panther, etc.
            public bool ShowForPlayers     = false;  // Other players — enable on PvP servers
            public bool ShowForHelicopters = true;   // Patrol heli, attack heli, CH47
            public bool ShowForBradleys    = true;   // Bradley APC
            public bool ShowForBarrels     = true;   // Loot barrels, oil barrels
            public bool ShowForDoors       = true;   // All door types
            public bool ShowForTurrets     = true;   // Auto turret, flame turret, SAM site, gun trap
            public bool ShowForStorage     = false;  // Crates and boxes — disabled by default (high entity count)
            public bool ShowForVehicles    = true;   // Cars, boats, helis, subs, snowmobiles

            // ── Performance ───────────────────────────────────────────────────────────

            // How often (in seconds) the raycast and UI refresh run per player.
            // Lower = more responsive; higher = less server load. Recommended: 0.1–0.3
            public float UpdateInterval = 0.2f;

            // Standard raycast range in metres — applies to NPCs, animals, vehicles, etc.
            public float RaycastDistance = 100f;

            // Extended raycast range used for helicopters and Bradley (they are large and far away).
            public float RaycastDistanceLong = 1500f;

            // Maximum distance for the angle-based helicopter fallback detection.
            // If the main raycast misses a heli, the plugin checks if any heli is within
            // this range and roughly in front of the player.
            public float HeliAngleDetectionRange = 500f;

            // Radius of the sphere cast. Increase if the bar doesn't appear on large entities
            // (e.g. Bradley, tugboat). Decrease for more precise targeting on small ones.
            public float SphereCastRadius = 2.0f;

            // Minimum HP change (as % of max health) required before the CUI panel refreshes.
            // 0 = refresh every tick regardless. 0.5 = only refresh when HP changes by ≥0.5%.
            // Raise this if you notice CUI flicker or want to reduce update frequency.
            public float HealthChangePctThreshold = 0.5f;

            // ── Colours ───────────────────────────────────────────────────────────────
            // All colours use Rust RGBA format: "R G B A" where each channel is 0.0–1.0.

            public string BarColorHigh       = "0.2 0.8 0.25 1.0";   // HP > 60%
            public string BarColorMedium     = "0.85 0.72 0.1 1.0";  // HP 25%–60%
            public string BarColorLow        = "0.82 0.18 0.12 1.0"; // HP < 25%
            public string BackgroundColor    = "0.07 0.07 0.07 0.93"; // Outer panel background
            public string BarBackgroundColor = "0.03 0.03 0.03 0.88"; // Bar track background
            public string EliteBorderColor   = "1 0.84 0 1";          // Gold border for elite entities

            // ── Position & size ───────────────────────────────────────────────────────
            // All values are fractions of screen size (0.0 = left/bottom, 1.0 = right/top).

            public float PositionX = 0.4f;   // Horizontal centre of the bar panel
            public float PositionY = 0.75f;  // Vertical position (0 = bottom, 1 = top)
            public float Width     = 0.2f;   // Width of the bar panel
            public float Height    = 0.03f;  // Height of the bar panel

            // ── UX ────────────────────────────────────────────────────────────────────

            // Compact mode: hides the entity icon and HP number — shows the bar strip only.
            public bool CompactMode = false;

            // Show the distance label for all players.
            // Players with the 'infinitumhealthbars.extended' permission always see it.
            public bool ShowDistanceLabel = false;

            // ── Elite keys ────────────────────────────────────────────────────────────
            // Entities whose resolved icon key matches any entry in this list receive
            // a gold border (colour set by EliteBorderColor above).
            // Use prefab name fragments — e.g. "scientistnpc_heavy", "bradleyapc".
            // Run /hbdebug while looking at any entity to find its key.
            public List<string> EliteKeys = new List<string>
            {
                "scientistnpc_heavy", "bradleyapc", "codelockedhackablecrate",
                "patrolhelicopter",   "attackhelicopter"
            };

            // ── Icons ─────────────────────────────────────────────────────────────────
            // EntityIcons maps a prefab key to a direct image URL.
            // Empty string "" = no icon shown for that entity type.
            // Use imgur or Discord CDN links — wiki.rustclash.com and rustlabs.com
            // return HTTP 403 when the server attempts to download them.
            // Run /hbdebug while looking at an entity to find its key.
            public Dictionary<string, string> EntityIcons = new Dictionary<string, string>();

            // Fallback icon used when no EntityIcons entry matches the entity.
            public string DefaultIcon = "https://i.imgur.com/6gmZOYm.png";
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null) throw new Exception();
                CheckConfig();
            }
            catch
            {
                Config.WriteObject(config = new Configuration(), true);
            }
        }

        private void CheckConfig()
        {
            bool changed = false;

            if (config.EntityIcons == null)
            {
                config.EntityIcons = new Dictionary<string, string>();
                changed = true;
            }

            if (config.EliteKeys == null)
            {
                config.EliteKeys = new List<string>
                {
                    "scientistnpc_heavy", "bradleyapc", "codelockedhackablecrate",
                    "patrolhelicopter",   "attackhelicopter"
                };
                changed = true;
            }

            // All .webp entries default to "" — admins supply their own URLs.
            var defaults = new Dictionary<string, string>
            {
                // ── Players ───────────────────────────────────────────────────────────
                ["player"]                   = "https://i.imgur.com/q4UZ5oq.png",

                // ── Scientists ────────────────────────────────────────────────────────
                ["scientist"]                = "https://i.imgur.com/2oK58Iy.png",
                ["scientistnpc_heavy"]       = "https://i.imgur.com/THOBLOC.png",
                ["scientistnpc_peacekeeper"] = "",
                ["scientistnpc_roam"]        = "https://i.imgur.com/2oK58Iy.png",
                ["scientistnpc_arctic"]      = "https://i.imgur.com/2oK58Iy.png",
                ["scientistnpc_ch47_gunner"] = "",
                ["scientist_oilrig_small"]   = "https://i.imgur.com/2oK58Iy.png",
                ["scientist_oilrig_large"]   = "https://i.imgur.com/2oK58Iy.png",
                ["scientistnpc_oilrig"]      = "https://i.imgur.com/2oK58Iy.png",
                ["scientist_oilrig_heavy_flamethrower"] = "https://i.imgur.com/ykfFuXe.png",
                ["scientist_oilrig_heavy_m249"]         = "https://i.imgur.com/THOBLOC.png",
                ["scientist_oilrig_heavy_minigun"]      = "https://i.imgur.com/ebzbtP4.png",
                ["scientist_oilrig_heavy_spas12"]       = "",
                ["scientistnpc_heavy_flamethrower"]     = "https://i.imgur.com/ykfFuXe.png",
                ["scientistnpc_heavy_m249"]             = "https://i.imgur.com/THOBLOC.png",
                ["scientistnpc_heavy_minigun"]          = "https://i.imgur.com/ebzbtP4.png",
                ["scientistnpc_heavy_spas12"]           = "",
                ["scientist_bradley"]                   = "https://i.imgur.com/TsZnMEh.png",
                ["scientistnpc_junkpile"]    = "",
                ["scientistnpc_patrol"]      = "https://i.imgur.com/2oK58Iy.png",
                ["scientistnpc_cargo"]       = "https://i.imgur.com/A893JdX.png",
                ["scientistnpc_excavator"]   = "https://i.imgur.com/2oK58Iy.png",
                ["scientistnpc_nvg"]         = "https://i.imgur.com/uC3VBn7.png",
                ["scientistnpc_outbreak"]    = "",
                ["scientistnpc_outpost"]     = "",
                ["scientist_deepsea_ghostship"]          = "",
                ["scientist_deepsea_island"]             = "",
                ["scientistnpc_abandoned_military_base"] = "https://i.imgur.com/wpe6yBR.png",
                ["scientistnpc_airfield"]    = "https://i.imgur.com/2oK58Iy.png",
                ["scientistnpc_launchsite"]  = "https://i.imgur.com/2oK58Iy.png",
                ["scientistnpc_missilesilo_inside"]  = "https://i.imgur.com/2oK58Iy.png",
                ["scientistnpc_missilesilo_outside"] = "https://i.imgur.com/2oK58Iy.png",
                ["scientistnpc_ptboat"]      = "https://i.imgur.com/2oK58Iy.png",
                ["scientistnpc_rhib"]        = "https://i.imgur.com/2oK58Iy.png",
                ["scientistnpc_trainyard"]   = "https://i.imgur.com/2oK58Iy.png",
                ["scientistnpc_full_any"]    = "https://i.imgur.com/q4UZ5oq.png",

                // ── Bandits / Halloween ───────────────────────────────────────────────
                ["bandit"]          = "",
                ["bandit_shopkeeper"] = "",
                ["scarecrow"]       = "",
                ["murderer"]        = "",

                // ── Vendors / Shopkeepers ─────────────────────────────────────────────
                ["boat_shopkeeper"]       = "",
                ["stables_shopkeeper"]    = "",
                ["airwolf_vendor"]        = "",
                ["waterwell_shopkeeper"]  = "",
                ["travelling_vendor"]     = "",
                ["lumberjack"]            = "",
                ["miner"]                 = "",
                ["fisherman"]             = "",

                // ── Tunnel / Underwater ───────────────────────────────────────────────
                ["tunneldweller"]         = "https://i.imgur.com/lCdFEME.png",
                ["militarytunneldweller"] = "https://i.imgur.com/wpe6yBR.png",
                ["underwaterdweller"]     = "https://i.imgur.com/XJZdVwi.png",
                ["gingerbreadnpc"]        = "",

                // ── Night zombies ─────────────────────────────────────────────────────
                ["zombie"] = "",

                // ── Animals ───────────────────────────────────────────────────────────
                ["bear"]        = "https://wiki.rustclash.com/img/screenshots/bear.png",
                ["polarbear"]   = "",
                ["wolf"]        = "",
                ["boar"]        = "https://wiki.rustclash.com/img/screenshots/boar.png",
                ["chicken"]     = "https://wiki.rustclash.com/img/screenshots/chicken.png",
                ["stag"]        = "https://wiki.rustclash.com/img/screenshots/stag.png",
                ["simpleshark"] = "",
                ["horse"]       = "https://wiki.rustclash.com/img/screenshots/horse.png",
                ["crocodile"]   = "",
                ["panther"]     = "https://wiki.rustclash.com/img/screenshots/panther.png",
                ["tiger"]       = "https://wiki.rustclash.com/img/screenshots/tiger.png",
                ["snake"]       = "",
                ["animal"]      = "",

                // ── Air vehicles ──────────────────────────────────────────────────────
                ["patrolhelicopter"]  = "",
                ["ch47helicopter"]    = "",
                ["attackhelicopter"]  = "",

                // ── Ground / Water vehicles ───────────────────────────────────────────
                ["bradleyapc"]               = "",
                ["minicopter"]               = "",
                ["scraptransporthelicopter"] = "",
                ["rowboat"]                  = "",
                ["rhib"]                     = "",
                ["tugboat"]                  = "",
                ["workcart"]                 = "",
                ["sedan"]                    = "",
                ["hotairballoon"]            = "",
                ["submarinesolo"]            = "",
                ["submarinetwo"]             = "",
                ["snowmobile"]               = "",
                ["motorbike"]                = "",
                ["vehicle"]                  = "",

                // ── Turrets / Traps ───────────────────────────────────────────────────
                ["autoturret"]  = "",
                ["flameturret"] = "",
                ["samsite"]     = "",
                ["guntrap"]     = "",

                // ── Barrels ───────────────────────────────────────────────────────────
                ["barrel"]        = "",
                ["loot-barrel-1"] = "https://i.imgur.com/cr1AuRh.png",
                ["loot-barrel-2"] = "https://i.imgur.com/cr1AuRh.png",
                ["oil_barrel"]    = "https://i.imgur.com/iufZyvo.png",
                ["diesel_barrel"] = "https://i.imgur.com/iufZyvo.png",

                // ── Crates ────────────────────────────────────────────────────────────
                ["crate_basic"]              = "https://i.imgur.com/BI9wiyy.png",
                ["crate_normal"]             = "https://i.imgur.com/BI9wiyy.png",
                ["crate_normal_2"]           = "https://i.imgur.com/BI9wiyy.png",
                ["crate_tools"]              = "https://i.imgur.com/BI9wiyy.png",
                ["crate_ammunition"]         = "https://i.imgur.com/BI9wiyy.png",
                ["crate_medical"]            = "https://i.imgur.com/BI9wiyy.png",
                ["crate_food_1"]             = "https://i.imgur.com/BI9wiyy.png",
                ["crate_food_2"]             = "https://i.imgur.com/BI9wiyy.png",
                ["crate_fuel"]               = "https://i.imgur.com/BI9wiyy.png",
                ["crate_mine"]               = "https://i.imgur.com/BI9wiyy.png",
                ["crate_shore"]              = "https://i.imgur.com/BI9wiyy.png",
                ["crate_elite"]              = "https://i.imgur.com/KH26C1D.png",
                ["crate_underwater_basic"]   = "https://i.imgur.com/BI9wiyy.png",
                ["crate_underwater_advanced"]= "https://i.imgur.com/KH26C1D.png",
                ["bradley_crate"]            = "https://i.imgur.com/KH26C1D.png",
                ["heli_crate"]               = "https://i.imgur.com/KH26C1D.png",
                ["codelockedhackablecrate"]  = "",

                // ── Doors ─────────────────────────────────────────────────────────────
                ["door"] = "",
            };

            foreach (var kvp in defaults)
            {
                if (!config.EntityIcons.ContainsKey(kvp.Key))
                {
                    config.EntityIcons[kvp.Key] = kvp.Value;
                    changed = true;
                }
                else
                {
                    // Clear only confirmed-broken legacy URLs (Cloudflare-blocked domains)
                    string url = config.EntityIcons[kvp.Key];
                    bool stale = url.Contains("rustlabs.com")        ||
                                 url.Contains("wiki.rustclash.com")  ||
                                 url.Contains("stag.head.png")       ||
                                 url.Contains("tool.box.png")        ||
                                 url.Contains("cctvcamera.png")      ||
                                 url.Contains("banit.png")           ||
                                 url.Contains("door.sheet.metal.png");
                    if (stale)
                    {
                        config.EntityIcons[kvp.Key] = kvp.Value;
                        changed = true;
                    }
                }
            }

            if (changed) SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            config = new Configuration();
            config.EntityIcons = new Dictionary<string, string>();
            CheckConfig();
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(config, true);

        #endregion

        #region Oxide Hooks

        private void Init()
        {
            permission.RegisterPermission(PermExtended, this);
            LoadConfig();
            cmd.AddChatCommand("hbdebug", this, nameof(CmdHBDebug));
        }

        [ChatCommand("hbdebug")]
        private void CmdHBDebug(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin && !player.IsDeveloper) return;
            RaycastHit hit;
            int mask = Rust.Layers.Mask.Default | Rust.Layers.Mask.Deployed |
                       Rust.Layers.Mask.Vehicle_Large | Rust.Layers.Mask.Player_Server |
                       Rust.Layers.Mask.AI | Rust.Layers.Mask.Vehicle_World;
            if (Physics.Raycast(player.eyes.HeadRay(), out hit, 300f, mask))
            {
                var e = hit.GetEntity();
                if (e != null)
                    player.ChatMessage($"<color=#ff6600>[HBDebug]</color> Prefab: <color=#fff>{e.ShortPrefabName}</color>  Layer: <color=#fff>{LayerMask.LayerToName(e.gameObject.layer)}</color>  IsAnimalNPC: <color=#fff>{e is BaseAnimalNPC}</color>  IsAnimalByPrefab: <color=#fff>{IsAnimalByPrefab(e as BaseCombatEntity)}</color>");
                else
                    player.ChatMessage($"<color=#ff6600>[HBDebug]</color> Raycast hit collider <color=#fff>{hit.collider?.name}</color> but GetEntity() returned null. Layer: <color=#fff>{LayerMask.LayerToName(hit.collider?.gameObject.layer ?? 0)}</color>");
            }
            else
                player.ChatMessage("<color=#ff6600>[HBDebug]</color> No entity in raycast range.");
        }

        private void OnServerInitialized()
        {
            if (config == null) LoadConfig();
            raycastTimer = timer.Every(config.UpdateInterval, UpdateRaycasts);

            foreach (var kvp in config.EntityIcons)
                if (!string.IsNullOrEmpty(kvp.Value) && kvp.Value.StartsWith("http"))
                    ImageLibrary?.Call("AddImage", kvp.Value, kvp.Key, 0UL);

            if (!string.IsNullOrEmpty(config.DefaultIcon) && config.DefaultIcon.StartsWith("http"))
                ImageLibrary?.Call("AddImage", config.DefaultIcon, "default_healthbar_icon", 0UL);
        }

        private void Unload()
        {
            if (raycastTimer != null && !raycastTimer.Destroyed)
                raycastTimer.Destroy();
            raycastTimer = null;
            foreach (var player in BasePlayer.activePlayerList)
                ClearTarget(player);
            playerTargets.Clear();
            _iconKeyCache.Clear();
            _dpsCache.Clear();
            _entityWatchers.Clear();
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            _dpsCache.Remove(player.userID);
            ClearTarget(player);
        }

        private void OnEntityKill(BaseCombatEntity entity)
        {
            if (entity?.net == null) return;
            ulong eid = entity.net.ID.Value;
            _iconKeyCache.Remove(eid);
            _entityWatchers.Remove(eid);
            foreach (var cache in _dpsCache.Values)
                cache.Remove(eid);
        }

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            // OnEntityTakeDamage fires BEFORE damage is applied — read damage from HitInfo directly.
            if (entity == null || entity.IsDestroyed || entity.net == null) return;
            float dmg = info?.damageTypes?.Total() ?? 0f;
            if (dmg <= 0f) return;

            ulong eid = entity.net.ID.Value;
            if (!_entityWatchers.TryGetValue(eid, out var watchers) || watchers.Count == 0) return;

            float now = Time.realtimeSinceStartup;
            foreach (ulong uid in watchers)
            {
                if (!playerTargets.TryGetValue(uid, out var tracked)) continue;

                float dt = now - tracked.LastDamageTime;
                if (dt > 0f && dt < 10f)
                {
                    float instantDPS = dmg / dt;
                    tracked.LastDPS = tracked.LastDPS <= 0f
                        ? instantDPS
                        : tracked.LastDPS * 0.7f + instantDPS * 0.3f;

                    // Persist DPS so it survives losing/regaining line of sight
                    if (!_dpsCache.TryGetValue(uid, out var pc))
                        _dpsCache[uid] = pc = new Dictionary<ulong, float>();
                    pc[eid] = tracked.LastDPS;
                }
                tracked.LastDamageTime = now;

                var p = BasePlayer.FindByID(uid);
                var e = entity;
                if (p != null) NextTick(() => { if (p != null && e != null && !e.IsDestroyed) UpdateUI(p, e); });
            }
        }

        private object OnHelicopterTarget(PatrolHelicopter heli, BasePlayer player)
        {
            if (config.ShowForHelicopters) return true;
            return null;
        }

        #endregion

        #region Core Logic

        private void UpdateRaycasts()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected || player.IsSleeping() || player.IsDead())
                {
                    ClearTarget(player);
                    continue;
                }

                int mask = Rust.Layers.Mask.Default      | Rust.Layers.Mask.Deployed      |
                           Rust.Layers.Mask.Vehicle_Large | Rust.Layers.Mask.Player_Server |
                           Rust.Layers.Mask.AI            | Rust.Layers.Mask.Vehicle_World;

                BaseCombatEntity target = null;
                RaycastHit hit;

                if (Physics.Raycast(player.eyes.HeadRay(), out hit, config.RaycastDistanceLong, mask))
                {
                    var e = hit.GetEntity() as BaseCombatEntity ?? ResolveVehicleParent(hit.collider);
                    if (e != null && IsValidCategory(e))
                    {
                        float d = Vector3.Distance(player.transform.position, hit.point);
                        if (CanShowAtDistance(e, d)) target = e;
                    }
                }

                if (target == null && Physics.SphereCast(player.eyes.HeadRay(), config.SphereCastRadius, out hit, config.RaycastDistanceLong, mask))
                {
                    var e = hit.GetEntity() as BaseCombatEntity ?? ResolveVehicleParent(hit.collider);
                    if (e != null && !(e is BaseHelicopter) && !(e is BradleyAPC))
                    {
                        var parentHeli = e.GetComponentInParent<PatrolHelicopter>();
                        if (parentHeli != null) e = parentHeli;
                    }
                    if (e != null && IsValidCategory(e) && (e is PatrolHelicopter || e is BaseHelicopter || e is BradleyAPC))
                    {
                        float d = Vector3.Distance(player.transform.position, hit.point);
                        if (CanShowAtDistance(e, d)) target = e;
                    }
                }

                if (target == null)
                {
                    // LOS mask must include Terrain + World so mountains/rocks block the cast
                    int losMask = Rust.Layers.Mask.Default      | Rust.Layers.Mask.Terrain     |
                                  Rust.Layers.Mask.World        | Rust.Layers.Mask.Construction |
                                  Rust.Layers.Mask.Vehicle_Large | Rust.Layers.Mask.Vehicle_World;
                    var ray = player.eyes.HeadRay();
                    PatrolHelicopter closest = null;
                    float closestAngle = 15f;
                    foreach (var h2 in BaseNetworkable.serverEntities.OfType<PatrolHelicopter>())
                    {
                        if (h2 == null || h2.IsDestroyed || h2.Health() <= 0) continue;
                        float dist = Vector3.Distance(player.transform.position, h2.transform.position);
                        if (dist > config.HeliAngleDetectionRange) continue;
                        float angle = Vector3.Angle(ray.direction, h2.transform.position - ray.origin);
                        if (angle >= closestAngle) continue;
                        // LOS check — terrain/mountains/buildings must not block the cast
                        bool hasLOS = !Physics.Linecast(player.eyes.position, h2.transform.position, out RaycastHit losHit, losMask)
                                      || losHit.GetEntity() == h2;
                        if (!hasLOS) continue;
                        closestAngle = angle; closest = h2;
                    }
                    if (closest != null && IsValidCategory(closest)) target = closest;
                }

                if (target != null) RegisterTarget(player, target);
                else                ClearTarget(player);
            }
        }

        private BaseCombatEntity ResolveVehicleParent(Collider col)
        {
            if (col == null) return null;
            var heli = col.GetComponentInParent<PatrolHelicopter>();
            if (heli != null) return heli;
            return col.GetComponentInParent<BradleyAPC>();
        }

        // Returns true for animals that may not inherit BaseAnimalNPC in newer Rust builds
        private bool IsAnimalByPrefab(BaseCombatEntity entity)
        {
            if (entity == null) return false;
            string n = entity.ShortPrefabName;
            return n.Contains("wolf")      || n.Contains("bear")  || n.Contains("boar")  ||
                   n.Contains("chicken")   || n.Contains("stag")  || n.Contains("shark") ||
                   n.Contains("horse")     || n.Contains("croc")  || n.Contains("panther") ||
                   n.Contains("tiger")     || n.Contains("snake");
        }

        private bool CanShowAtDistance(BaseCombatEntity entity, float distance)
        {
            if (entity is PatrolHelicopter || entity is BaseHelicopter || entity is BradleyAPC ||
                entity is BasePlayer       || entity is BaseAnimalNPC  || entity is AutoTurret ||
                entity is SamSite         || entity is LootContainer   || entity is BaseVehicle ||
                IsAnimalByPrefab(entity))
                return distance <= config.RaycastDistanceLong;
            return distance <= config.RaycastDistance;
        }

        private bool IsValidCategory(BaseCombatEntity entity)
        {
            if (entity == null || entity.IsDestroyed || entity.Health() <= 0) return false;
            if (entity is BasePlayer bp)    return bp.IsNpc ? config.ShowForNPCs : config.ShowForPlayers;
            if (entity is BaseAnimalNPC || IsAnimalByPrefab(entity)) return config.ShowForAnimals;
            if (entity is PatrolHelicopter) return config.ShowForHelicopters;
            if (entity is BaseHelicopter)   return config.ShowForHelicopters;
            if (entity is BradleyAPC)       return config.ShowForBradleys;
            if (entity is LootContainer)    return config.ShowForBarrels;
            if (entity is Door)             return config.ShowForDoors;
            if (entity is AutoTurret || entity is FlameTurret || entity is SamSite || entity is GunTrap)
                                            return config.ShowForTurrets;
            if (entity is StorageContainer) return config.ShowForStorage;
            if (entity is BaseVehicle)      return config.ShowForVehicles;
            return false;
        }

        private void RegisterTarget(BasePlayer player, BaseCombatEntity entity)
        {
            float curr = entity.Health();
            float max  = entity.MaxHealth();
            ulong eid  = entity.net?.ID.Value ?? 0UL;

            if (!playerTargets.TryGetValue(player.userID, out var tracked) || tracked.Entity != entity)
            {
                // Remove player from old entity's watcher set
                if (tracked != null && tracked.EntityNetId != 0)
                {
                    if (_entityWatchers.TryGetValue(tracked.EntityNetId, out var oldSet))
                        oldSet.Remove(player.userID);
                }

                // Restore DPS from cache if we've hit this entity before
                float cachedDPS = 0f;
                if (eid != 0 && _dpsCache.TryGetValue(player.userID, out var pc))
                    pc.TryGetValue(eid, out cachedDPS);

                playerTargets[player.userID] = new TrackedEntity
                {
                    Entity         = entity,
                    EntityNetId    = eid,
                    LastHealth     = curr,
                    MaxHealth      = max,
                    LastDPS        = cachedDPS,
                    LastDamageTime = Time.realtimeSinceStartup,
                    HasExtended    = permission.UserHasPermission(player.UserIDString, PermExtended)
                };

                // Register in reverse index
                if (eid != 0)
                {
                    if (!_entityWatchers.TryGetValue(eid, out var watchers))
                        _entityWatchers[eid] = watchers = new HashSet<ulong>();
                    watchers.Add(player.userID);
                }

                UpdateUI(player, entity);
                return;
            }

            // Same entity — refresh MaxHealth (can change on boss spawns) then check redraw threshold
            tracked.MaxHealth = max;
            float threshold = config.HealthChangePctThreshold / 100f;
            float pctDelta  = max > 0f ? Math.Abs(tracked.LastHealth - curr) / max : 0f;
            if (threshold <= 0f || pctDelta >= threshold)
            {
                tracked.LastHealth = curr;
                UpdateUI(player, entity);
            }
        }

        #endregion

        #region Icon Resolution

        private string GetImage(string key) =>
            (string)ImageLibrary?.Call("GetImage", key, 0UL) ?? "";

        private string GetCachedIconKey(BaseCombatEntity entity)
        {
            ulong id = entity.net?.ID.Value ?? 0UL;
            if (id != 0 && _iconKeyCache.TryGetValue(id, out string cached)) return cached;
            string key = ResolveIconKey(entity);
            if (id != 0) _iconKeyCache[id] = key;
            return key;
        }

        private string GetEntityIcon(BaseCombatEntity entity)
        {
            if (config.EntityIcons.ContainsKey(entity.ShortPrefabName))
            {
                string img = GetImage(entity.ShortPrefabName);
                if (img != "") return img;
                img = GetImage(config.EntityIcons[entity.ShortPrefabName]);
                if (img != "") return img;
            }

            string key = GetCachedIconKey(entity);
            if (config.EntityIcons.ContainsKey(key))
            {
                string img = GetImage(key);
                if (img != "") return img;
                img = GetImage(config.EntityIcons[key]);
                if (img != "") return img;
            }

            return GetImage("default_healthbar_icon");
        }

        private string ResolveIconKey(BaseCombatEntity entity)
        {
            if (entity is BasePlayer bp)
            {
                if (!bp.IsNpc) return "player";
                string n = entity.ShortPrefabName;
                string d = bp.displayName?.ToLower() ?? "";

                if (d.Contains("oil rig heavy") || d.Contains("oilrig heavy"))
                {
                    if (d.Contains("flamethrower")) return "scientist_oilrig_heavy_flamethrower";
                    if (d.Contains("m249"))         return "scientist_oilrig_heavy_m249";
                    if (d.Contains("minigun"))      return "scientist_oilrig_heavy_minigun";
                    if (d.Contains("spas-12") || d.Contains("spas12")) return "scientist_oilrig_heavy_spas12";
                }
                if (d.Contains("bradley heavy"))
                {
                    if (d.Contains("flamethrower")) return "scientistnpc_heavy_flamethrower";
                    if (d.Contains("m249"))         return "scientistnpc_heavy_m249";
                    if (d.Contains("minigun"))      return "scientistnpc_heavy_minigun";
                    if (d.Contains("spas-12") || d.Contains("spas12")) return "scientistnpc_heavy_spas12";
                }
                if (d.Contains("ghost ship") || d.Contains("ghostship")) return "scientist_deepsea_ghostship";
                if (d.Contains("deep sea island") || d.Contains("deepsea island")) return "scientist_deepsea_island";

                if (n.Contains("heavy"))             return "scientistnpc_heavy";
                if (n.Contains("peacekeeper"))       return "scientistnpc_peacekeeper";
                if (n.Contains("roam"))              return "scientistnpc_roam";
                if (n.Contains("arctic"))            return "scientistnpc_arctic";
                if (n.Contains("ch47"))              return "scientistnpc_ch47_gunner";
                if (n.Contains("oilrig"))            return "scientistnpc_oilrig";
                if (n.Contains("junkpile"))          return "scientistnpc_junkpile";
                if (n.Contains("patrol"))            return "scientistnpc_patrol";
                if (n.Contains("cargo"))             return "scientistnpc_cargo";
                if (n.Contains("excavator"))         return "scientistnpc_excavator";
                if (n.Contains("nvg"))               return "scientistnpc_nvg";
                if (n.Contains("outbreak"))          return "scientistnpc_outbreak";
                if (n.Contains("outpost"))           return "scientistnpc_outpost";
                if (n.Contains("abandoned"))         return "scientistnpc_abandoned_military_base";
                if (n.Contains("airfield"))          return "scientistnpc_airfield";
                if (n.Contains("launchsite"))        return "scientistnpc_launchsite";
                if (n.Contains("missilesilo"))       return "scientistnpc_missilesilo_inside";
                if (n.Contains("ptboat"))            return "scientistnpc_ptboat";
                if (n.Contains("rhib"))              return "scientistnpc_rhib";
                if (n.Contains("trainyard"))         return "scientistnpc_trainyard";
                if (n.Contains("lumberjack"))        return "lumberjack";
                if (n.Contains("miner"))             return "miner";
                if (n.Contains("fisherman"))         return "fisherman";
                if (n.Contains("scientist"))         return "scientist";
                if (n.Contains("bandit"))            return "bandit";
                if (n.Contains("scarecrow"))         return "scarecrow";
                if (n.Contains("murderer"))          return "murderer";
                if (n.Contains("militarytunnel"))    return "militarytunneldweller";
                if (n.Contains("tunneldweller"))     return "tunneldweller";
                if (n.Contains("underwaterdweller")) return "underwaterdweller";
                if (n.Contains("zombie"))            return "zombie";
                if (n.Contains("gingerbread"))       return "gingerbreadnpc";
                return "scientistnpc_full_any";
            }

            if (entity is BaseAnimalNPC || IsAnimalByPrefab(entity))
            {
                string n = entity.ShortPrefabName;
                if (n.Contains("polarbear")) return "polarbear";
                if (n.Contains("bear"))      return "bear";
                if (n.Contains("wolf"))      return "wolf";
                if (n.Contains("boar"))      return "boar";
                if (n.Contains("chicken"))   return "chicken";
                if (n.Contains("stag"))      return "stag";
                if (n.Contains("shark"))     return "simpleshark";
                if (n.Contains("horse"))     return "horse";
                if (n.Contains("croc"))      return "crocodile";
                if (n.Contains("panther"))   return "panther";
                if (n.Contains("tiger"))     return "tiger";
                if (n.Contains("snake"))     return "snake";
                return "animal";
            }

            if (entity is PatrolHelicopter) return "patrolhelicopter";
            if (entity is BaseHelicopter)
            {
                string n = entity.ShortPrefabName;
                if (n.Contains("ch47"))   return "ch47helicopter";
                if (n.Contains("attack")) return "attackhelicopter";
                return "patrolhelicopter";
            }

            if (entity is BradleyAPC)   return "bradleyapc";
            if (entity is AutoTurret)   return "autoturret";
            if (entity is FlameTurret)  return "flameturret";
            if (entity is SamSite)      return "samsite";
            if (entity is GunTrap)      return "guntrap";
            if (entity is Door)         return "door";

            if (entity is BaseVehicle)
            {
                string n = entity.ShortPrefabName;
                if (n.Contains("minicopter"))     return "minicopter";
                if (n.Contains("scraptransport")) return "scraptransporthelicopter";
                if (n.Contains("rhib"))           return "rhib";
                if (n.Contains("rowboat"))        return "rowboat";
                if (n.Contains("tugboat"))        return "tugboat";
                if (n.Contains("workcart"))       return "workcart";
                if (n.Contains("sedan"))          return "sedan";
                if (n.Contains("hotairballoon"))  return "hotairballoon";
                if (n.Contains("submarinesolo"))  return "submarinesolo";
                if (n.Contains("submarinetwo"))   return "submarinetwo";
                if (n.Contains("snowmobile"))     return "snowmobile";
                if (n.Contains("motorbike"))      return "motorbike";
                return "vehicle";
            }

            if (entity is LootContainer)
            {
                string n = entity.ShortPrefabName;
                if (n.Contains("loot-barrel-1") || n.Contains("loot_barrel_1")) return "loot-barrel-1";
                if (n.Contains("loot-barrel-2") || n.Contains("loot_barrel_2")) return "loot-barrel-2";
                if (n.Contains("oil_barrel"))     return "oil_barrel";
                if (n.Contains("diesel_barrel"))  return "diesel_barrel";
                if (n.Contains("barrel"))         return "barrel";
                if (n.Contains("crate_elite"))               return "crate_elite";
                if (n.Contains("bradley_crate"))             return "bradley_crate";
                if (n.Contains("heli_crate"))                return "heli_crate";
                if (n.Contains("codelockedhackablecrate"))   return "codelockedhackablecrate";
                if (n.Contains("crate_underwater_advanced")) return "crate_underwater_advanced";
                if (n.Contains("crate_underwater"))          return "crate_underwater_basic";
                if (n.Contains("crate_ammunition"))          return "crate_ammunition";
                if (n.Contains("crate_tools"))               return "crate_tools";
                if (n.Contains("crate_medical"))             return "crate_medical";
                if (n.Contains("crate_food"))                return "crate_food_1";
                if (n.Contains("crate_fuel"))                return "crate_fuel";
                if (n.Contains("crate_mine"))                return "crate_mine";
                if (n.Contains("crate_shore"))               return "crate_shore";
                if (n.Contains("crate_normal_2"))            return "crate_normal_2";
                if (n.Contains("crate_normal"))              return "crate_normal";
                if (n.Contains("crate_basic"))               return "crate_basic";
                return "crate_normal";
            }

            return "default";
        }

        #endregion

        #region Display Helpers

        private static readonly Dictionary<string, string> KnownNames = new Dictionary<string, string>
        {
            ["bradleyapc"]                           = "Bradley APC",
            ["patrolhelicopter"]                     = "Patrol Helicopter",
            ["ch47helicopter.entity"]                = "Chinook",
            ["scientistnpc_heavy"]                   = "Heavy Scientist",
            ["scientistnpc_heavy_flamethrower"]      = "Bradley Heavy Flamethrower Scientist",
            ["scientistnpc_heavy_m249"]              = "Bradley Heavy M249 Scientist",
            ["scientistnpc_heavy_minigun"]           = "Bradley Heavy Minigun Scientist",
            ["scientistnpc_heavy_spas12"]            = "Bradley Heavy Spas-12 Scientist",
            ["oilrig_heavy_flamethrower"]            = "Oil Rig Heavy Flamethrower Scientist",
            ["oilrig_heavy_m249"]                    = "Oil Rig Heavy M249 Scientist",
            ["oilrig_heavy_minigun"]                 = "Oil Rig Heavy Minigun Scientist",
            ["oilrig_heavy_spas12"]                  = "Oil Rig Heavy Spas-12 Scientist",
            ["scientist_bradley"]                    = "Bradley Scientist",
            ["scientistnpc_peacekeeper"]             = "Peacekeeper",
            ["scientistnpc_roam"]                    = "Road Scientist",
            ["scientistnpc_roamtethered"]            = "Scientist",
            ["scientistnpc_patrol"]                  = "Patrol Scientist",
            ["scientistnpc_arctic"]                  = "Arctic Research Base Scientist",
            ["scientistnpc_ch47_gunner"]             = "CH47 Gunner",
            ["scientist_oilrig_small"]               = "Small Oil Rig Scientist",
            ["scientist_oilrig_large"]               = "Large Oil Rig Scientist",
            ["scientistnpc_oilrig1"]                 = "Oil Rig Scientist",
            ["scientistnpc_oilrig2"]                 = "Oil Rig Scientist",
            ["scientistnpc_junkpile_pistol"]         = "Junkpile Scientist",
            ["scientistnpc_junkpile_shotgun"]        = "Junkpile Scientist",
            ["scientistnpc_cargo"]                   = "Cargo Ship Scientist",
            ["scientistnpc_excavator"]               = "Excavator Scientist",
            ["scientistnpc_nvg"]                     = "NVG Scientist",
            ["scientistnpc_outbreak"]                = "Outbreak Sprayer",
            ["scientistnpc_outpost"]                 = "Outpost Scientist",
            ["scientist_deepsea_ghostship"]          = "Deep Sea Ghost Ship Scientist",
            ["scientist_deepsea_island"]             = "Deep Sea Island Scientist",
            ["scientistnpc_abandoned_military_base"] = "Abandoned Military Base Scientist",
            ["scientistnpc_airfield"]                = "Airfield Scientist",
            ["scientistnpc_launchsite"]              = "Launch Site Scientist",
            ["scientistnpc_missilesilo_inside"]      = "Missile Silo Scientist",
            ["scientistnpc_missilesilo_outside"]     = "Missile Silo Scientist",
            ["scientistnpc_ptboat"]                  = "PT Boat Scientist",
            ["scientistnpc_rhib"]                    = "RHIB Scientist",
            ["scientistnpc_trainyard"]               = "Trainyard Scientist",
            ["bandit_shopkeeper"]                    = "Bandit Shopkeeper",
            ["boat_shopkeeper"]                      = "Boat Vendor",
            ["stables_shopkeeper"]                   = "Stables Vendor",
            ["airwolf_vendor"]                       = "Airwolf Vendor",
            ["waterwell_shopkeeper"]                 = "Water Well Shopkeeper",
            ["missionprovider_bandit_a"]             = "Bandit Missions",
            ["missionprovider_bandit_b"]             = "Bandit Missions",
            ["npc_underwaterdweller"]                = "Underwater Lab Dweller",
            ["npc_tunneldweller"]                    = "Tunnel Dweller",
            ["militarytunneldweller"]                = "Military Tunnel Dweller",
            ["autoturret_deployed"]                  = "Auto Turret",
            ["flameturret.deployed"]                 = "Flame Turret",
            ["sam_site_turret_deployed"]             = "SAM Site",
            ["guntrap.deployed"]                     = "Shotgun Trap",
            ["loot-barrel-1"]                        = "Barrel",
            ["loot-barrel-2"]                        = "Barrel",
            ["oil_barrel"]                           = "Oil Barrel",
            ["diesel_barrel_world"]                  = "Diesel Barrel",
            ["crate_basic"]                          = "Basic Crate",
            ["crate_basic_jungle"]                   = "Basic Crate",
            ["crate_normal"]                         = "Normal Crate",
            ["crate_normal_2"]                       = "Military Crate",
            ["crate_elite"]                          = "Elite Crate",
            ["crate_tools"]                          = "Tool Crate",
            ["crate_ammunition"]                     = "Ammo Crate",
            ["crate_medical"]                        = "Medical Crate",
            ["crate_food_1"]                         = "Food Crate",
            ["crate_food_2"]                         = "Food Crate",
            ["crate_fuel"]                           = "Fuel Crate",
            ["crate_mine"]                           = "Mine Crate",
            ["crate_shore"]                          = "Shore Crate",
            ["crate_underwater_basic"]               = "Underwater Crate",
            ["crate_underwater_advanced"]            = "Advanced Underwater Crate",
            ["bradley_crate"]                        = "Bradley Crate",
            ["heli_crate"]                           = "Heli Crate",
            ["codelockedhackablecrate"]              = "Hackable Crate",
            ["codelockedhackablecrate_oilrig"]       = "Oil Rig Hackable Crate",
            ["minicopter.entity"]                    = "Minicopter",
            ["scraptransporthelicopter"]             = "Scrap Transport Heli",
            ["rhib"]                                 = "RHIB",
            ["rowboat"]                              = "Rowboat",
            ["tugboat"]                              = "Tugboat",
            ["workcart"]                             = "Work Cart",
            ["workcart_aboveground"]                 = "Work Cart",
            ["submarinesolo.entity"]                 = "Solo Submarine",
            ["submarinetwo.entity"]                  = "Duo Submarine",
            ["hotairballoon"]                        = "Hot Air Balloon",
            ["snowmobile"]                           = "Snowmobile",
            ["motorbike"]                            = "Motorbike",
            ["simpleshark"]                          = "Shark",
            ["crocodile"]                            = "Crocodile",
            ["panther"]                              = "Panther",
            ["tiger"]                                = "Tiger",
            ["snake"]                                = "Snake",
            ["gingerbreadnpc"]                       = "Gingerbread",
            ["travelling_vendor"]                    = "Travelling Vendor",
        };

        private string FormatEntityName(BaseCombatEntity entity)
        {
            if (entity is BasePlayer bp)
            {
                if (!bp.IsNpc) return bp.displayName;
                if (!string.IsNullOrEmpty(bp.displayName) && bp.displayName != bp.ShortPrefabName)
                    return bp.displayName;
            }

            if (KnownNames.TryGetValue(entity.ShortPrefabName, out string known))
                return known;

            string name = entity.ShortPrefabName.Replace("_", " ").Replace("-", " ").Replace(".", " ");
            var words = name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++)
                if (words[i].Length > 0)
                    words[i] = char.ToUpper(words[i][0]) + words[i].Substring(1).ToLower();
            return string.Join(" ", words);
        }

        private string FormatHealth(float current, float max)
        {
            int cur = Mathf.CeilToInt(current);
            int mx  = Mathf.CeilToInt(max);
            if (mx >= 10000) return $"{cur / 1000f:F1}k/{mx / 1000f:F1}k";
            return $"{cur}/{mx}";
        }

        private string FormatTTK(float hp, float dps)
        {
            if (dps <= 0f) return null;
            float ttk = hp / dps;
            return ttk < 60f ? $"TTK = {ttk:F1}s" : $"TTK = {ttk / 60f:F1}m";
        }

        #endregion

        #region UI Rendering

        private void UpdateUI(BasePlayer player, BaseCombatEntity entity)
        {
            if (player == null || player.IsDestroyed) return;

            // Read DPS BEFORE DestroyCUI wipes nothing, but kept here for clarity —
            // tracking data is never removed by DestroyCUI, only by ClearTarget.
            playerTargets.TryGetValue(player.userID, out var tracked);

            DestroyCUI(player);
            if (entity == null || entity.IsDestroyed) return;

            bool   hasExtended = tracked?.HasExtended ?? false;
            float  dist        = Vector3.Distance(player.transform.position, entity.transform.position);
            string distLabel   = (config.ShowDistanceLabel || hasExtended) ? $"{dist:F0}m" : null;

            string ttkLabel = null;
            if (hasExtended && tracked != null && tracked.LastDPS > 0f)
                ttkLabel = FormatTTK(entity.Health(), tracked.LastDPS);

            string iconKey = GetCachedIconKey(entity);
            bool   isElite = config.EliteKeys != null && config.EliteKeys.Contains(iconKey);

            if (entity is PatrolHelicopter heli)
            {
                CreateHeliUI(player, heli, distLabel, ttkLabel, isElite);
                return;
            }

            var container = new CuiElementContainer();
            RenderBar(container, LayerName, entity,
                FormatEntityName(entity), entity.Health(), entity.MaxHealth(),
                config.PositionY, config.Height, distLabel, ttkLabel, isElite);
            CuiHelper.AddUi(player, container);
        }

        private void CreateHeliUI(BasePlayer player, PatrolHelicopter heli,
            string distLabel, string ttkLabel, bool isElite)
        {
            var container = new CuiElementContainer();
            float h   = config.Height;
            float gap = 0.004f;
            float y   = config.PositionY;

            bool ws0 = heli.weakspots != null && heli.weakspots.Length > 0 && heli.weakspots[0] != null;
            bool ws1 = heli.weakspots != null && heli.weakspots.Length > 1 && heli.weakspots[1] != null;

            if (ws1)
                RenderBar(container, LayerName + "_Tail", heli, "Tail Rotor",
                    heli.weakspots[1].health, heli.weakspots[1].maxHealth,
                    y, h, null, null, false);

            if (ws0)
                RenderBar(container, LayerName + "_Main", heli, "Main Rotor",
                    heli.weakspots[0].health, heli.weakspots[0].maxHealth,
                    y + (ws1 ? (h + gap) : 0f), h, null, null, false);

            float bodyY = y + ((ws0 ? 1 : 0) + (ws1 ? 1 : 0)) * (h + gap);
            RenderBar(container, LayerName + "_Body", heli, "Patrol Helicopter",
                heli.Health(), heli.MaxHealth(), bodyY, h, distLabel, ttkLabel, isElite);

            CuiHelper.AddUi(player, container);
        }

        // Layout (back to front): Shadow → Border → Background → [IconBG | Icon | Sep] → BarBG → Fill → Shine → Labels
        private void RenderBar(CuiElementContainer c, string panel,
            BaseCombatEntity entity, string label,
            float current, float max, float yPos, float height,
            string distLabel = null, string ttkLabel = null, bool isElite = false)
        {
            float pct = max > 0f ? Mathf.Clamp01(current / max) : 0f;

            string barColor    = pct < 0.25f ? config.BarColorLow : pct < 0.5f ? config.BarColorMedium : config.BarColorHigh;
            string borderColor = isElite ? config.EliteBorderColor : barColor;

            float x   = config.PositionX;
            float w   = config.Width;
            float bpx = 0.0012f;
            float bpy = 0.0020f;
            float spx = bpx * 3.5f;
            float spy = bpy * 3.5f;

            // Drop shadow
            c.Add(new CuiPanel
            {
                Image         = { Color = "0 0 0 0.72" },
                RectTransform = { AnchorMin = $"{x - spx} {yPos - spy}", AnchorMax = $"{x + w + spx} {yPos + height + spy}" },
                CursorEnabled = false
            }, "Hud", panel + ".Shadow");

            // Accent border — gold for elites, HP colour otherwise
            c.Add(new CuiPanel
            {
                Image         = { Color = borderColor },
                RectTransform = { AnchorMin = $"{x - bpx} {yPos - bpy}", AnchorMax = $"{x + w + bpx} {yPos + height + bpy}" },
                CursorEnabled = false
            }, "Hud", panel + ".Border");

            // Main background
            c.Add(new CuiPanel
            {
                Image         = { Color = config.BackgroundColor },
                RectTransform = { AnchorMin = $"{x} {yPos}", AnchorMax = $"{x + w} {yPos + height}" },
                CursorEnabled = false
            }, "Hud", panel);

            // Icon — skipped in compact mode
            bool   hasIcon = !config.CompactMode;
            string iconId  = hasIcon ? GetEntityIcon(entity) : "";
            hasIcon = hasIcon && !string.IsNullOrEmpty(iconId);
            float iconW = hasIcon ? 0.10f : 0f;
            float barX  = hasIcon ? iconW + 0.012f : 0.01f;

            if (hasIcon)
            {
                c.Add(new CuiPanel
                {
                    Image         = { Color = "0 0 0 0.28" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = $"{iconW} 1" }
                }, panel, panel + ".IconBG");

                c.Add(new CuiElement
                {
                    Name   = panel + ".Icon",
                    Parent = panel,
                    Components =
                    {
                        new CuiRawImageComponent    { Png = iconId },
                        new CuiRectTransformComponent { AnchorMin = $"0.01 0.1", AnchorMax = $"{iconW - 0.01f} 0.9" }
                    }
                });

                c.Add(new CuiPanel
                {
                    Image         = { Color = "1 1 1 0.07" },
                    RectTransform = { AnchorMin = $"{iconW} 0.08", AnchorMax = $"{iconW + 0.004f} 0.92" }
                }, panel, panel + ".Sep");
            }

            // Bar background
            c.Add(new CuiPanel
            {
                Image         = { Color = config.BarBackgroundColor },
                RectTransform = { AnchorMin = $"{barX} 0.12", AnchorMax = "0.99 0.88" }
            }, panel, panel + ".BarBG");

            // Bar fill + top-edge shine
            if (pct > 0f)
            {
                c.Add(new CuiPanel
                {
                    Image         = { Color = barColor },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = $"{pct} 1", OffsetMin = "2 2", OffsetMax = "-2 -2" }
                }, panel + ".BarBG", panel + ".Fill");

                c.Add(new CuiPanel
                {
                    Image         = { Color = "1 1 1 0.07" },
                    RectTransform = { AnchorMin = "0 0.58", AnchorMax = $"{pct} 1", OffsetMin = "2 0", OffsetMax = "-2 -2" }
                }, panel + ".BarBG");
            }

            // Label zone layout — adjusted per combination of extras shown
            // Both dist+TTK : name 0.03-0.36 | hp 0.33-0.54 | dist 0.54-0.68 | TTK 0.68-0.98
            // TTK only      : name 0.03-0.42 | hp 0.38-0.60 |                  TTK 0.60-0.98
            // Dist only     : name 0.03-0.50 | hp 0.42-0.76 | dist 0.76-0.98
            // Neither       : name 0.03-0.60 | hp 0.42-0.98
            int  fs       = height >= 0.035f ? 11 : 10;
            bool hasDist  = distLabel != null;
            bool hasTTK   = ttkLabel  != null;
            bool hasExtra = hasDist || hasTTK;

            string nameMaxX, hpMaxX;
            if (hasDist && hasTTK)      { nameMaxX = "0.36 1"; hpMaxX = "0.54 1"; }
            else if (hasTTK)            { nameMaxX = "0.42 1"; hpMaxX = "0.60 1"; }
            else if (hasDist)           { nameMaxX = "0.50 1"; hpMaxX = "0.76 1"; }
            else                        { nameMaxX = "0.60 1"; hpMaxX = "0.98 1"; }

            c.Add(new CuiLabel
            {
                Text          = { Text = label, FontSize = fs, Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.92", Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = "0.03 0", AnchorMax = nameMaxX }
            }, panel + ".BarBG");

            // HP number — hidden in compact mode
            if (!config.CompactMode)
            {
                c.Add(new CuiLabel
                {
                    Text          = { Text = FormatHealth(current, max), FontSize = fs, Align = TextAnchor.MiddleRight, Color = "1 1 1 0.70", Font = "robotocondensed-regular.ttf" },
                    RectTransform = { AnchorMin = "0.33 0", AnchorMax = hpMaxX }
                }, panel + ".BarBG");
            }

            // Distance label — light blue ("150m" is short, 14% is enough)
            if (hasDist)
            {
                string dMin = hasTTK ? "0.54 0" : "0.76 0";
                string dMax = hasTTK ? "0.68 1" : "0.98 1";
                c.Add(new CuiLabel
                {
                    Text          = { Text = distLabel, FontSize = fs - 1, Align = TextAnchor.MiddleRight, Color = "0.65 0.85 1 0.85", Font = "robotocondensed-regular.ttf" },
                    RectTransform = { AnchorMin = dMin, AnchorMax = dMax }
                }, panel + ".BarBG");
            }

            // TTK label — orange, 30% wide to fit "TTK = 12.5s"
            if (hasTTK)
            {
                c.Add(new CuiLabel
                {
                    Text          = { Text = ttkLabel, FontSize = fs - 1, Align = TextAnchor.MiddleRight, Color = "1 0.62 0.18 0.90", Font = "robotocondensed-regular.ttf" },
                    RectTransform = { AnchorMin = "0.68 0", AnchorMax = "0.98 1" }
                }, panel + ".BarBG");
            }
        }

        // Destroys only the CUI panels — tracking data (DPS, LastHealth) is preserved.
        private void DestroyCUI(BasePlayer player)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, LayerName + ".Shadow");
            CuiHelper.DestroyUi(player, LayerName + ".Border");
            CuiHelper.DestroyUi(player, LayerName);
            CuiHelper.DestroyUi(player, LayerName + "_Tail.Shadow");
            CuiHelper.DestroyUi(player, LayerName + "_Tail.Border");
            CuiHelper.DestroyUi(player, LayerName + "_Tail");
            CuiHelper.DestroyUi(player, LayerName + "_Main.Shadow");
            CuiHelper.DestroyUi(player, LayerName + "_Main.Border");
            CuiHelper.DestroyUi(player, LayerName + "_Main");
            CuiHelper.DestroyUi(player, LayerName + "_Body.Shadow");
            CuiHelper.DestroyUi(player, LayerName + "_Body.Border");
            CuiHelper.DestroyUi(player, LayerName + "_Body");
        }

        // Destroys CUI and removes tracking data — call when player loses target or disconnects.
        private void ClearTarget(BasePlayer player)
        {
            if (player == null) return;
            if (playerTargets.TryGetValue(player.userID, out var tracked) && tracked.EntityNetId != 0)
            {
                if (_entityWatchers.TryGetValue(tracked.EntityNetId, out var watchers))
                    watchers.Remove(player.userID);
            }
            playerTargets.Remove(player.userID);
            DestroyCUI(player);
        }

        #endregion
    }
}
