using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using SiroccoMod;
using SiroccoMod.Helpers;

[assembly: MelonInfo(typeof(SiroccoMod.Mods.BalanceTweaks.BalanceTweaksPlugin), "Balance Tweaks", "1.0.0", "Shadow")]
[assembly: MelonGame("LunchboxEntertainment", "Sirocco")]

namespace SiroccoMod.Mods.BalanceTweaks
{
    /// <summary>
    /// Balance tweaks for weapons, abilities, status effects, world effects, and entities.
    ///
    /// All tweaks work via the same mechanism: track ScalingFloat IL2CPP pointers and
    /// apply a multiplier in the native ScalingFloatBase.Evaluate hook.
    ///
    /// Multiplier values:
    ///   0    = disable (zero the stat)
    ///   0.5  = halve
    ///   1    = unchanged
    ///   2    = double
    ///
    /// TypeIDs are logged at startup — check the MelonLoader log for the full list.
    /// </summary>
    public class BalanceTweaksPlugin : MelonMod
    {
        // ================================================================
        // BALANCE CONFIGURATION
        // ================================================================

        // --- Weapon TypeIDs ---
        //  1 = Weapon_CloseRange_DeathBomb         18 = Weapon_Empire_Structure_SaltCore
        //  2 = Weapon_CloseRange_Decimator         19 = Weapon_Empire_Structure_Shipyard
        //  3 = Weapon_CloseRange_DumurzhetCoils    20 = Weapon_Empire_Tower_Tier1
        //  4 = Weapon_CloseRange_Howlers           21 = Weapon_Empire_Tower_Tier2
        //  5 = Weapon_CloseRange_Juggernaut        22 = Weapon_Empire_Tower_Tier3
        //  6 = Weapon_CloseRange_KineticDampener   23 = Weapon_LongRange_ChainShot
        //  7 = Weapon_CloseRange_Overclocker       24 = Weapon_LongRange_FragMortor
        //  8 = Weapon_CloseRange_Reinforcer        25 = Weapon_LongRange_HeatSeekingMissle
        //  9 = Weapon_CloseRange_ShoreBasher       26 = Weapon_LongRange_HullSplitter
        // 10 = Weapon_Empire_Harvester_RapidFire   27 = Weapon_LongRange_OrbanKings
        // 11 = Weapon_Empire_Minion_Large          28 = Weapon_LongRange_RailGun
        // 12 = Weapon_Empire_Minion_Large_E_Dead   29 = Weapon_LongRange_RoyalPhoenixCorps
        // 13 = Weapon_Empire_Minion_Medium         30 = Weapon_LongRange_TrenchMaker
        // 14 = Weapon_Empire_Minion_Medium_E_Dead  31 = Weapon_MediumRange_AbyssalThorn
        // 15 = Weapon_Empire_Minion_Siege          32 = Weapon_MediumRange_BarrierDynamo
        // 16 = Weapon_Empire_Minion_Small          33 = Weapon_MediumRange_BreachHornets
        // 17 = Weapon_Empire_Minion_Small_E_Dead   34 = Weapon_MediumRange_NightBringer
        //                                          35 = Weapon_MediumRange_RepairInhibtor
        //                                          36 = Weapon_MediumRange_Scorpions
        //                                          37 = Weapon_MediumRange_SparkGenerator
        //
        // Weapon stats: Damage, RangeMin, RangeMax, ReloadTime, MaxAmmo, ReloadAmount

        private static readonly List<BalanceEntry> WeaponTweaks = new()
        {
        };

        // --- Ability Projectile TypeIDs ---
        //  17 = Horizon_Q1_GravityJump_DefendAugment2
        //  18 = Horizon_W2_Collapse_AttackAugment1
        //  65 = Sampan_Harpoon
        // (Check log for full list)

        private static readonly List<BalanceEntry> AbilityProjectileTweaks = new()
        {
            // Horizon q damage augment: 110% → 90% (multiply by 90/110 ≈ 0.818)
            new(17, new() { { Stat.Damage, 0.818f } }),
            // Horizon e damage augment: 90% → 80% (multiply by 80/90 ≈ 0.889)
            new(18, new() { { Stat.Damage, 0.889f } }),
        };

        // --- Status Effect TypeIDs ---
        // 270 = Zephyr_3_StellarNova_Enemy (Gale Blast silence on enemies)
        // (Check log for full list)

        private static readonly List<BalanceEntry> StatusEffectTweaks = new()
        {
            // Zephyr Gale Blast (e) silence duration -30%
            new(270, new() { { Stat.Duration, 0.7f } }),
        };

        // --- World Effect TypeIDs ---
        //  24 = Dragonfly_3_DeathGaze_DefenceAugment (repulsion on e)
        //  38 = Horizon_Q1_GravityJump (shroud on q)
        // (Check log for full list)

        private static readonly List<BalanceEntry> WorldEffectTweaks = new()
        {
            // Dragonfly repulsion augment on e: -25% force + disable large projectile deflection
            new(24, new() { { Stat.ForceMagnitude, 0.75f }, { Stat.DisableLargeProjectileDeflect, 0f } }),
            // Horizon q shroud augment: -30% duration
            new(38, new() { { Stat.Lifespan, 0.7f } }),
        };

        // --- Entity TypeIDs ---
        // Player ships: 13=Sampan, 14=Crane, 15=Lurker, 16=Nautilus, 17=Oasis,
        //   18=Stalker, 19=Zephyr, 20=Aurora, 21=Dragonfly, 22=Lockjaw,
        //   23=Seabloom, 24=Wither, 25=Crucible, 26=Horizon, 27=Mosaic,
        //   28=Piranha, 29=Stingray, 30=Marquise, 31=Nori, 32=Paradox,
        //   33=Prism, 34=Volt
        // Structures: 36=Shipyard, 47=Tower_T1, 48=Tower_T2, 49=Tower_T3

        private static readonly List<BalanceEntry> EntityTweaks = new()
        {
            // All player ships: +6% movement speed, 2x HP regen (5→10 HP/s)
            new(13, new() { { Stat.MaxSpeed, 1.06f }, { Stat.HealthRegenRate, 2.0f } }), // Sampan
            new(14, new() { { Stat.MaxSpeed, 1.06f }, { Stat.HealthRegenRate, 2.0f } }), // Crane
            new(15, new() { { Stat.MaxSpeed, 1.06f }, { Stat.HealthRegenRate, 2.0f } }), // Lurker
            new(16, new() { { Stat.MaxSpeed, 1.06f }, { Stat.HealthRegenRate, 2.0f } }), // Nautilus
            new(17, new() { { Stat.MaxSpeed, 1.06f }, { Stat.HealthRegenRate, 2.0f } }), // Oasis
            new(18, new() { { Stat.MaxSpeed, 1.06f }, { Stat.HealthRegenRate, 2.0f } }), // Stalker
            new(19, new() { { Stat.MaxSpeed, 1.06f }, { Stat.HealthRegenRate, 2.0f } }), // Zephyr
            new(20, new() { { Stat.MaxSpeed, 1.06f }, { Stat.HealthRegenRate, 2.0f } }), // Aurora
            new(21, new() { { Stat.MaxSpeed, 1.06f }, { Stat.HealthRegenRate, 2.0f } }), // Dragonfly
            new(22, new() { { Stat.MaxSpeed, 1.06f }, { Stat.HealthRegenRate, 2.0f } }), // Lockjaw
            new(23, new() { { Stat.MaxSpeed, 1.06f }, { Stat.HealthRegenRate, 2.0f } }), // Seabloom
            new(24, new() { { Stat.MaxSpeed, 1.06f }, { Stat.HealthRegenRate, 2.0f } }), // Wither
            new(25, new() { { Stat.MaxSpeed, 1.06f }, { Stat.HealthRegenRate, 2.0f } }), // Crucible
            new(26, new() { { Stat.MaxSpeed, 1.06f }, { Stat.HealthRegenRate, 2.0f } }), // Horizon
            new(27, new() { { Stat.MaxSpeed, 1.06f }, { Stat.HealthRegenRate, 2.0f } }), // Mosaic
            new(28, new() { { Stat.MaxSpeed, 1.06f }, { Stat.HealthRegenRate, 2.0f } }), // Piranha
            new(29, new() { { Stat.MaxSpeed, 1.06f }, { Stat.HealthRegenRate, 2.0f } }), // Stingray
            new(30, new() { { Stat.MaxSpeed, 1.06f }, { Stat.HealthRegenRate, 2.0f } }), // Marquise
            new(31, new() { { Stat.MaxSpeed, 1.06f }, { Stat.HealthRegenRate, 2.0f } }), // Nori
            new(32, new() { { Stat.MaxSpeed, 1.06f }, { Stat.HealthRegenRate, 2.0f } }), // Paradox
            new(33, new() { { Stat.MaxSpeed, 1.06f }, { Stat.HealthRegenRate, 2.0f } }), // Prism
            new(34, new() { { Stat.MaxSpeed, 1.06f }, { Stat.HealthRegenRate, 2.0f } }), // Volt
            // Structures: reduce incoming weapon damage
            // Towers: 100% → 90%
            new(47, new() { { Stat.IncomingWeaponDamageScale, 0.9f } }), // Tower_T1
            new(48, new() { { Stat.IncomingWeaponDamageScale, 0.9f } }), // Tower_T2
            new(49, new() { { Stat.IncomingWeaponDamageScale, 0.9f } }), // Tower_T3
            // Shipyard/SaltCore: 100% → 80%
            new(36, new() { { Stat.IncomingWeaponDamageScale, 0.8f } }), // Shipyard
        };

        // --- Card/Loom Coil TypeIDs ---
        //  25 = Entity Damage Resistance Weapon (blue loom coil)
        //  40 = StartCard_Rusana (Overloaded Weapons)
        // (Check log for full list)

        private static readonly List<BalanceEntry> CardTweaks = new()
        {
            // Weapon Resistance Loom Coil (blue): +4% → +8% (double the increment)
            new(25, new() { { Stat.Stat1Increment, 2.0f } }),
            // Overloaded Weapons (Rusana): Option B rework handled by special logic below
        };

        // Overloaded Weapons TypeID for special handling
        private const int OverloadedWeaponsTypeId = 40;

        // ================================================================
        // Stat enum
        // ================================================================

        public enum Stat
        {
            Damage, RangeMin, RangeMax, ReloadTime, MaxAmmo, ReloadAmount,
            Duration,
            Lifespan, Radius, ForceMagnitude, EntryDamage, AmbientDamage,
            DisableLargeProjectileDeflect,
            MaxSpeed, IncomingDamageScale, IncomingWeaponDamageScale,
            HealthRegenRate,
            Stat1Increment, Stat2Increment, Stat3Increment,
        }

        private record BalanceEntry(int TypeId, Dictionary<Stat, float> Modifiers);

        // ================================================================
        // Internal state
        // ================================================================

        private static bool _balanceApplied;
        private static int _horizonTypeIdValue = -1;

        private static PropertyInfo? _gaInstanceProp;
        private static readonly HashSet<IntPtr> _alreadyModified = new();

        // ================================================================
        // Initialization
        // ================================================================

        public override void OnInitializeMelon()
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");

            if (asm == null)
            {
                MelonLogger.Error("[BalanceTweaks] Assembly-CSharp not found");
                return;
            }

            var gaType = asm.GetType("Il2CppWartide.GameAuthority");
            if (gaType != null)
                _gaInstanceProp = gaType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);

            // Horizon ship block
            // InstallHorizonBlock(asm);

            MelonLogger.Msg("[BalanceTweaks] Installed");
        }

        public override void OnFixedUpdate()
        {
            if (_balanceApplied) return;

            try
            {
                var gaInstance = _gaInstanceProp?.GetValue(null);
                if (gaInstance == null) return;

                var gaType = gaInstance.GetType();
                var registry = gaType.GetProperty("_dataRegistry", HarmonyPatcher.FLAGS)?.GetValue(gaInstance);
                if (registry == null) return;

                // Resolve Horizon TypeID
                if (_horizonTypeIdValue < 0)
                {
                    ResolveHorizonTypeId(registry);
                    if (_horizonTypeIdValue < 0) return;
                }

                // Apply all balance tweaks
                ApplyTweaks(registry, "_simWeaponData", WeaponTweaks, ApplyWeaponModifiers);
                ApplyTweaks(registry, "_simAbilityProjectiles", AbilityProjectileTweaks, ApplyAbilityProjectileModifiers);
                ApplyTweaks(registry, "_simStatusEffectData", StatusEffectTweaks, ApplyStatusEffectModifiers);
                ApplyTweaks(registry, "_simWorldEffectData", WorldEffectTweaks, ApplyWorldEffectModifiers);
                ApplyTweaks(registry, "_simEntityData", EntityTweaks, ApplyEntityModifiers);
                ApplyTweaks(registry, "_simCardComponentData", CardTweaks, ApplyCardModifiers);
                ApplyOverloadedWeaponsRework(registry);


                _balanceApplied = true;
                MelonLogger.Msg("[BalanceTweaks] Balance tweaks applied");
            }
            catch { }
        }

        // ================================================================
        // Horizon ship block (Harmony patches)
        // ================================================================

        private void InstallHorizonBlock(Assembly asm)
        {
            try
            {
                var simMgrType = asm.GetType("Il2CppWartide.SimulationManager");
                if (simMgrType == null) return;

                var prefix = typeof(BalanceTweaksPlugin).GetMethod(nameof(Prefix_BlockHorizonSwitch), HarmonyPatcher.FLAGS);

                var switchMethod = simMgrType.GetMethod("AddSwitchShipTransaction", HarmonyPatcher.FLAGS);
                if (switchMethod != null && prefix != null)
                {
                    HarmonyInstance.Patch(switchMethod, prefix: new HarmonyLib.HarmonyMethod(prefix));
                    MelonLogger.Msg("[BalanceTweaks] Patched AddSwitchShipTransaction");
                }

                var stealMethod = simMgrType.GetMethod("AddStealingSwitchShipTransaction", HarmonyPatcher.FLAGS);
                if (stealMethod != null && prefix != null)
                {
                    HarmonyInstance.Patch(stealMethod, prefix: new HarmonyLib.HarmonyMethod(prefix));
                    MelonLogger.Msg("[BalanceTweaks] Patched AddStealingSwitchShipTransaction");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BalanceTweaks] Horizon block patch error: {ex.Message}");
            }
        }

        public static bool Prefix_BlockHorizonSwitch(int playerIndex, object shipTypeID)
        {
            try
            {
                int val = GetTypeIdValue(shipTypeID);
                if (_horizonTypeIdValue >= 0 && val == _horizonTypeIdValue)
                {
                    MelonLogger.Msg($"[BalanceTweaks] Blocked player {playerIndex} from switching to Horizon");
                    return false;
                }
            }
            catch { }
            return true;
        }

        // ================================================================
        // Generic tweak application
        // ================================================================

        private delegate void ModifierApplier(object item, string name, Stat stat, float multiplier);

        private static void ApplyTweaks(object registry, string arrayProp,
            List<BalanceEntry> tweaks, ModifierApplier applyModifiers)
        {
            if (tweaks.Count == 0) return;

            var array = registry.GetType().GetProperty(arrayProp, HarmonyPatcher.FLAGS)?.GetValue(registry);
            if (array == null) return;

            var tweakMap = new Dictionary<int, BalanceEntry>();
            foreach (var t in tweaks)
                tweakMap[t.TypeId] = t;

            foreach (var item in IL2CppArrayHelper.Iterate(array))
            {
                int typeId = GetItemTypeId(item);
                if (!tweakMap.TryGetValue(typeId, out var tweak)) continue;

                string name = GetName(item);
                MelonLogger.Msg($"[BalanceTweaks] Matched TypeID={typeId}: '{name}'");

                foreach (var (stat, multiplier) in tweak.Modifiers)
                    applyModifiers(item, name, stat, multiplier);
            }
        }

        // ================================================================
        // Modifier appliers per category
        // ================================================================

        private static void ApplyWeaponModifiers(object weapon, string name, Stat stat, float m)
        {
            switch (stat)
            {
                case Stat.Damage: NerfWeaponProjectileDamage(weapon, name, m); break;
                case Stat.RangeMin: TrackProp(weapon, "RangeMin", m, name); break;
                case Stat.RangeMax: TrackProp(weapon, "RangeMax", m, name); break;
                case Stat.ReloadTime: TrackProp(weapon, "ReloadTime", m, name); break;
                case Stat.MaxAmmo: TrackProp(weapon, "MaxAmmo", m, name); break;
                case Stat.ReloadAmount: TrackProp(weapon, "ReloadAmount", m, name); break;
            }
        }

        private static void ApplyAbilityProjectileModifiers(object proj, string name, Stat stat, float m)
        {
            if (stat == Stat.Damage)
                NerfAbilityProjectileDamage(proj, name, m);
        }

        private static void ApplyStatusEffectModifiers(object effect, string name, Stat stat, float m)
        {
            switch (stat)
            {
                case Stat.Duration: TrackProp(effect, "Duration", m, name); break;
                case Stat.Damage: ApplyStatusEffectDamage(effect, m, name); break;
            }
        }

        private static void ApplyWorldEffectModifiers(object worldEffect, string name, Stat stat, float m)
        {
            switch (stat)
            {
                case Stat.Lifespan: TrackProp(worldEffect, "Lifespan", m, name); break;
                case Stat.Radius: TrackProp(worldEffect, "Radius", m, name); break;
                case Stat.ForceMagnitude: TrackProp(worldEffect, "ForceMagnitude", m, name); break;
                case Stat.EntryDamage:
                    var ed = worldEffect.GetType().GetProperty("EntryDamage", HarmonyPatcher.FLAGS)?.GetValue(worldEffect);
                    if (ed != null) ApplyScaledDamageMultiplier(ed, m, $"{name}.EntryDamage");
                    break;
                case Stat.AmbientDamage:
                    var ad = worldEffect.GetType().GetProperty("AmbientDamage", HarmonyPatcher.FLAGS)?.GetValue(worldEffect);
                    if (ad != null) ApplyScaledDamageMultiplier(ad, m, $"{name}.AmbientDamage");
                    break;
                case Stat.DisableLargeProjectileDeflect:
                    DisableLargeProjectileDeflection(worldEffect, name);
                    break;
            }
        }

        private static void DisableLargeProjectileDeflection(object worldEffect, string name)
        {
            try
            {
                // Set Shield_AbilityProjectileInteraction_Large to 0 (no interaction)
                var prop = worldEffect.GetType().GetProperty("Shield_AbilityProjectileInteraction_Large", HarmonyPatcher.FLAGS);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(worldEffect, 0);
                    MelonLogger.Msg($"[BalanceTweaks]   {name}: Disabled large projectile deflection");
                }
            }
            catch { }
        }

        private static void ApplyEntityModifiers(object entity, string name, Stat stat, float m)
        {
            var config = entity.GetType().GetProperty("Config", HarmonyPatcher.FLAGS)?.GetValue(entity);
            if (config == null) return;

            switch (stat)
            {
                case Stat.MaxSpeed:
                    var sharedSpeed = config.GetType().GetProperty("SharedSpeed", HarmonyPatcher.FLAGS)?.GetValue(config);
                    if (sharedSpeed != null)
                        TrackProp(sharedSpeed, "Speed", m, name);
                    break;
                case Stat.IncomingDamageScale: TrackProp(config, "IncomingDamageScale", m, name); break;
                case Stat.IncomingWeaponDamageScale: TrackProp(config, "IncomingWeaponDamageScale", m, name); break;
                case Stat.HealthRegenRate:
                    var sharedHealth = config.GetType().GetProperty("SharedHealth", HarmonyPatcher.FLAGS)?.GetValue(config);
                    if (sharedHealth != null)
                        TrackProp(sharedHealth, "HealthRegenerationRate", m, name);
                    break;
            }
        }

        private static void ApplyCardModifiers(object card, string name, Stat stat, float m)
        {
            // Card stat increments are int fields, not ScalingFloat — modify directly
            switch (stat)
            {
                case Stat.Stat1Increment: ScaleIntProp(card, "Stat1Increment", m, name); break;
                case Stat.Stat2Increment: ScaleIntProp(card, "Stat2Increment", m, name); break;
                case Stat.Stat3Increment: ScaleIntProp(card, "Stat3Increment", m, name); break;
            }
        }

        // ================================================================
        // Overloaded Weapons (Rusana) rework — Option B
        // Double max ammo, zero reload amount bonus, set reload time to +10%
        // ================================================================

        private static void ApplyOverloadedWeaponsRework(object registry)
        {
            try
            {
                var array = registry.GetType().GetProperty("_simCardComponentData", HarmonyPatcher.FLAGS)?.GetValue(registry);
                if (array == null) return;

                // Dump all legendary weapon cards to help identify the correct one
                foreach (var card in IL2CppArrayHelper.Iterate(array))
                {
                    int typeId = GetItemTypeId(card);
                    string name = GetName(card);
                    var rarityProp = card.GetType().GetProperty("Rarity", HarmonyPatcher.FLAGS);
                    var typeProp = card.GetType().GetProperty("Type", HarmonyPatcher.FLAGS);
                    int rarity = rarityProp != null ? Convert.ToInt32(rarityProp.GetValue(card)) : -1;
                    int cardType = typeProp != null ? Convert.ToInt32(typeProp.GetValue(card)) : -1;
                    // CardRarity: 0=Common, 1=Uncommon, 2=Rare, 3=Legendary
                    // CardType: 0=Ship, 10=Ability, 20=Repair, 30=Weapon, 40=Common
                    if (rarity == 3) // Legendary only
                    {
                        var s1 = card.GetType().GetProperty("Stat1", HarmonyPatcher.FLAGS)?.GetValue(card);
                        var s2 = card.GetType().GetProperty("Stat2", HarmonyPatcher.FLAGS)?.GetValue(card);
                        var s3 = card.GetType().GetProperty("Stat3", HarmonyPatcher.FLAGS)?.GetValue(card);
                        var i1 = card.GetType().GetProperty("Stat1Increment", HarmonyPatcher.FLAGS)?.GetValue(card);
                        var i2 = card.GetType().GetProperty("Stat2Increment", HarmonyPatcher.FLAGS)?.GetValue(card);
                        var i3 = card.GetType().GetProperty("Stat3Increment", HarmonyPatcher.FLAGS)?.GetValue(card);
                        MelonLogger.Msg($"[BalanceTweaks] [CardDump] TypeID={typeId} name='{name}' rarity={rarity} type={cardType} | {s1}={i1}, {s2}={i2}, {s3}={i3}");
                    }
                }

                foreach (var card in IL2CppArrayHelper.Iterate(array))
                {
                    int typeId = GetItemTypeId(card);
                    if (typeId != OverloadedWeaponsTypeId) continue;

                    string name = GetName(card);
                    MelonLogger.Msg($"[BalanceTweaks] Reworking Overloaded Weapons (TypeID={typeId}): '{name}'");

                    // Read each stat slot and modify based on what it controls
                    // CardStats enum: WeaponAmmo=7, WeaponReloadAmount=8, WeaponFireRate=2
                    for (int slot = 1; slot <= 3; slot++)
                    {
                        var statProp = card.GetType().GetProperty($"Stat{slot}", HarmonyPatcher.FLAGS);
                        var incProp = card.GetType().GetProperty($"Stat{slot}Increment", HarmonyPatcher.FLAGS);
                        if (statProp == null || incProp == null)
                        {
                            MelonLogger.Msg($"[BalanceTweaks]   Stat{slot}: property not found (statProp={statProp != null}, incProp={incProp != null})");
                            continue;
                        }

                        var statRaw = statProp.GetValue(card);
                        int statType = Convert.ToInt32(statRaw);
                        int currentInc = (int)(incProp.GetValue(card) ?? 0);
                        MelonLogger.Msg($"[BalanceTweaks]   Stat{slot}: type={statType} (raw={statRaw}), increment={currentInc}");

                        switch (statType)
                        {
                            case 7: // WeaponAmmo — double it
                                int newAmmo = currentInc * 2;
                                incProp.SetValue(card, newAmmo);
                                MelonLogger.Msg($"[BalanceTweaks]   Stat{slot} (WeaponAmmo): {currentInc} -> {newAmmo}");
                                break;
                            case 8: // WeaponReloadAmount — zero it
                                incProp.SetValue(card, 0);
                                MelonLogger.Msg($"[BalanceTweaks]   Stat{slot} (WeaponReloadAmount): {currentInc} -> 0");
                                break;
                            case 2: // WeaponFireRate (reload time) — reduce to ~10% penalty
                                // Current is +90% reload time. Option B wants +10%.
                                // Scale: 10/90 ≈ 0.111
                                int newRate = (int)Math.Round(currentInc * (10.0 / 90.0));
                                if (newRate < 1) newRate = 1;
                                incProp.SetValue(card, newRate);
                                MelonLogger.Msg($"[BalanceTweaks]   Stat{slot} (WeaponFireRate): {currentInc} -> {newRate}");
                                break;
                        }
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BalanceTweaks] Overloaded Weapons rework error: {ex.Message}");
            }
        }

        private static void ScaleIntProp(object obj, string propName, float multiplier, string label)
        {
            try
            {
                var prop = obj.GetType().GetProperty(propName, HarmonyPatcher.FLAGS);
                if (prop == null || !prop.CanRead || !prop.CanWrite) return;

                int original = (int)(prop.GetValue(obj) ?? 0);
                int scaled = (int)Math.Round(original * multiplier);
                prop.SetValue(obj, scaled);
                MelonLogger.Msg($"[BalanceTweaks]   {label}.{propName}: {original} -> {scaled} (x{multiplier})");
            }
            catch { }
        }

        // ================================================================
        // Weapon damage (follows projectile chain)
        // ================================================================

        private static void NerfWeaponProjectileDamage(object weapon, string weaponName, float m)
        {
            var projData = weapon.GetType().GetProperty("_projectileData", HarmonyPatcher.FLAGS)?.GetValue(weapon);
            if (projData != null)
            {
                var damage = projData.GetType().GetProperty("Damage", HarmonyPatcher.FLAGS)?.GetValue(projData);
                if (damage != null)
                    ApplyScaledDamageMultiplier(damage, m, $"{weaponName}.Damage");

                var statusEffects = projData.GetType().GetProperty("StatusEffects", HarmonyPatcher.FLAGS)?.GetValue(projData);
                if (statusEffects != null)
                    ApplyStatusEffectArrayMultiplier(statusEffects, m, $"{weaponName}.StatusEffects");
            }

            foreach (var prop in weapon.GetType().GetProperties(HarmonyPatcher.FLAGS))
            {
                if (prop.Name.Contains("Damage") && prop.GetIndexParameters().Length == 0)
                {
                    try
                    {
                        var val = prop.GetValue(weapon);
                        if (val != null && val.GetType().Name.Contains("ScaledDamage"))
                            ApplyScaledDamageMultiplier(val, m, $"{weaponName}.{prop.Name}");
                    }
                    catch { }
                }
            }
        }

        // ================================================================
        // Ability projectile damage
        // ================================================================

        private static void NerfAbilityProjectileDamage(object proj, string projName, float m)
        {
            var type = proj.GetType();

            var collDmg = type.GetProperty("CollisionDamage", HarmonyPatcher.FLAGS)?.GetValue(proj);
            if (collDmg != null)
                ApplyScaledDamageMultiplier(collDmg, m, $"{projName}.CollisionDamage");

            var attachedWE = type.GetProperty("ActiveAttachedWorldEffect", HarmonyPatcher.FLAGS)?.GetValue(proj);
            if (attachedWE != null)
            {
                var entryDmg = attachedWE.GetType().GetProperty("EntryDamage", HarmonyPatcher.FLAGS)?.GetValue(attachedWE);
                if (entryDmg != null) ApplyScaledDamageMultiplier(entryDmg, m, $"{projName}.WE.EntryDamage");

                var ambientDmg = attachedWE.GetType().GetProperty("AmbientDamage", HarmonyPatcher.FLAGS)?.GetValue(attachedWE);
                if (ambientDmg != null) ApplyScaledDamageMultiplier(ambientDmg, m, $"{projName}.WE.AmbientDamage");
            }

            var enemyStatus = type.GetProperty("CollisionStatusEffectsEnemy", HarmonyPatcher.FLAGS)?.GetValue(proj);
            if (enemyStatus != null)
                ApplyStatusEffectArrayMultiplier(enemyStatus, m, $"{projName}.StatusEnemy");

            var friendStatus = type.GetProperty("CollisionStatusEffectsFriend", HarmonyPatcher.FLAGS)?.GetValue(proj);
            if (friendStatus != null)
                ApplyStatusEffectArrayMultiplier(friendStatus, m, $"{projName}.StatusFriend");
        }

        // ================================================================
        // ScaledDamage + StatusEffect helpers
        // ================================================================

        private static void ApplyScaledDamageMultiplier(object scaledDamage, float m, string label)
        {
            try
            {
                TrackProp(scaledDamage, "Amount", m, label);
                TrackProp(scaledDamage, "AmountEnemy", m, label);
                TrackProp(scaledDamage, "AmountFriend", m, label);
            }
            catch { }
        }

        private static void ApplyStatusEffectDamage(object effect, float m, string label)
        {
            var dmgOrHeal = effect.GetType().GetProperty("DamageOrHeal", HarmonyPatcher.FLAGS)?.GetValue(effect);
            if (dmgOrHeal != null) ApplyScaledDamageMultiplier(dmgOrHeal, m, $"{label}.DamageOrHeal");

            // Note: OnApplicationDamageOrHeal is a ConditionalBool flag, not a ScaledDamage.
            // The actual damage is in DamageOrHeal (already tracked above).

            var dot = effect.GetType().GetProperty("DamageOverTime", HarmonyPatcher.FLAGS)?.GetValue(effect);
            if (dot != null) ApplyScaledDamageMultiplier(dot, m, $"{label}.DoT");

            var dom = effect.GetType().GetProperty("DamageOverMovement", HarmonyPatcher.FLAGS)?.GetValue(effect);
            if (dom != null) ApplyScaledDamageMultiplier(dom, m, $"{label}.DoM");
        }

        private static void ApplyStatusEffectArrayMultiplier(object statusArray, float m, string label)
        {
            try
            {
                int i = 0;
                foreach (var effect in IL2CppArrayHelper.Iterate(statusArray))
                {
                    ApplyStatusEffectDamage(effect, m, $"{label}[{i}]");
                    i++;
                }
            }
            catch { }
        }

        // ================================================================
        // ScalingFloat tracking
        // ================================================================

        private static void TrackProp(object obj, string propName, float multiplier, string label)
        {
            var val = obj.GetType().GetProperty(propName, HarmonyPatcher.FLAGS)?.GetValue(obj);
            if (val is Il2CppObjectBase il2cppObj)
            {
                IntPtr ptr = Il2CppInterop.Runtime.IL2CPP.Il2CppObjectBaseToPtr(il2cppObj);
                if (ptr != IntPtr.Zero && !_alreadyModified.Add(ptr))
                {
                    MelonLogger.Msg($"[BalanceTweaks]     {label}.{propName}: shared ScalingFloat, already modified — skipping");
                    return;
                }
                ModifyBaseValue(val, multiplier, $"{label}.{propName}");
            }
        }

        private const float PackingMaxValue = 2000f;

        private static void ModifyBaseValue(object scalingFloat, float multiplier, string label)
        {
            try
            {
                var baseValueProp = scalingFloat.GetType().GetProperty("BaseValue", HarmonyPatcher.FLAGS);
                if (baseValueProp == null || !baseValueProp.CanRead || !baseValueProp.CanWrite) return;

                float original = (float)(baseValueProp.GetValue(scalingFloat) ?? 0f);
                float modified = original * multiplier;
                if (modified > PackingMaxValue)
                {
                    MelonLogger.Warning($"[BalanceTweaks]     {label}: clamped {modified} -> {PackingMaxValue} (packing limit)");
                    modified = PackingMaxValue;
                }
                else if (modified < -PackingMaxValue)
                {
                    MelonLogger.Warning($"[BalanceTweaks]     {label}: clamped {modified} -> {-PackingMaxValue} (packing limit)");
                    modified = -PackingMaxValue;
                }
                baseValueProp.SetValue(scalingFloat, modified);
                MelonLogger.Msg($"[BalanceTweaks]     BaseValue: {original} -> {modified}");
            }
            catch { }
        }


        // ================================================================
        // Horizon TypeID resolution
        // ================================================================

        private static void ResolveHorizonTypeId(object registry)
        {
            var entityArray = registry.GetType().GetProperty("_simEntityData", HarmonyPatcher.FLAGS)?.GetValue(registry);
            if (entityArray == null) return;

            foreach (var entity in IL2CppArrayHelper.Iterate(entityArray))
            {
                string name = GetName(entity);
                if (name.IndexOf("Horizon", StringComparison.OrdinalIgnoreCase) < 0) continue;

                _horizonTypeIdValue = GetItemTypeId(entity);
                MelonLogger.Msg($"[BalanceTweaks] Horizon TypeID resolved: {_horizonTypeIdValue}");
                return;
            }
        }

        // ================================================================
        // Reflection helpers
        // ================================================================

        private static int GetTypeIdValue(object typeId)
        {
            if (typeId == null) return -1;
            var field = typeId.GetType().GetField("Value", HarmonyPatcher.FLAGS);
            if (field != null) return (int)(field.GetValue(typeId) ?? -1);
            return -1;
        }

        private static string GetName(object obj)
        {
            return obj.GetType().GetProperty("name", HarmonyPatcher.FLAGS)?.GetValue(obj)?.ToString() ?? "";
        }

        private static int GetItemTypeId(object item)
        {
            var typeIdVal = item.GetType().GetProperty("TypeID", HarmonyPatcher.FLAGS)?.GetValue(item);
            return typeIdVal != null ? GetTypeIdValue(typeIdVal) : -1;
        }
    }
}
