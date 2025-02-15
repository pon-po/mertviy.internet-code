using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;
using Rust;
using System.Collections.Generic;
using Oxide.Core.Configuration;
using Facepunch;
using System.Collections;
using System.Text.RegularExpressions;
using System.Globalization;
using Newtonsoft.Json;
using System;

namespace Oxide.Plugins
{
    [Info("FPV Drone", "Nakson_Play", "1.0.0")]
    [Description("ФПВ дрон в раст")]
    
    class FPVDrone : RustPlugin
    {
        private const string PermUse = "fpvdrone.use";
        private readonly HashSet<NetworkableId> activeGrenades = new HashSet<NetworkableId>();
        private Dictionary<string, DateTime> cooldownPlayers = new Dictionary<string, DateTime>();
        private Configuration config;

        #region Configuration
        private class Configuration
        {
            [JsonProperty("Урон игрокам (граната)")]
            public float PlayerDamage { get; set; } = 300f;
            
            [JsonProperty("Урон сооружениям (граната)")]
            public float StructureDamage { get; set; } = 1500f;
            
            [JsonProperty("Радиус взрыва (граната)")]
            public float ExplosionRadius { get; set; } = 15f;
            
            [JsonProperty("Задержка детонации (сек)")]
            public float FuseTime { get; set; } = 3f;
            
            [JsonProperty("Высота сброса (метры)")]
            public float DropHeight { get; set; } = 0.5f;
            
            [JsonProperty("Задержка между сбросами (сек)")]
            public float Cooldown { get; set; } = 1f;
            
            [JsonProperty("Урон игрокам (дрон)")]
            public float DronePlayerDamage { get; set; } = 500f;
            
            [JsonProperty("Урон сооружениям (дрон)")]
            public float DroneStructureDamage { get; set; } = 2500f;
			
             [JsonProperty("Максимальная дистанция дрона (метры)")]
            public float MaxDroneDistance { get; set; } = 1000f;
			
            [JsonProperty("Радиус взрыва (дрон)")]
            public float DroneExplosionRadius { get; set; } = 10f;
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            config = Config.ReadObject<Configuration>();
            SaveConfig();
        }
        #endregion

        void Init()
        {
            permission.RegisterPermission(PermUse, this);
            LoadConfig();
        }
 
        void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null || !input.WasJustPressed(BUTTON.FIRE_PRIMARY)) return;
            if (!permission.UserHasPermission(player.UserIDString, PermUse)) return;
            
            if (cooldownPlayers.TryGetValue(player.UserIDString, out DateTime lastUse))
            {
                if ((DateTime.Now - lastUse).TotalSeconds < config.Cooldown) return;
            }

            var mounted = player.GetMounted() as ComputerStation;
            var drone = mounted?.currentlyControllingEnt.Get(true) as Drone;
            if (drone == null) return;

            cooldownPlayers[player.UserIDString] = DateTime.Now;
            SpawnGrenade(drone);
        }

        private void SpawnGrenade(Drone drone)
        {
            var spawnPos = drone.transform.position - new Vector3(0, config.DropHeight, 0);

            var grenade = GameManager.server.CreateEntity(
                "assets/prefabs/ammo/40mmgrenade/40mm_grenade_he.prefab",
                spawnPos,
                Quaternion.identity) as TimedExplosive;

            if (grenade == null) return;

            grenade.timerAmountMin = config.FuseTime;
            grenade.timerAmountMax = config.FuseTime;
            grenade.explosionRadius = config.ExplosionRadius;
            Effect.server.Run(
                "assets/prefabs/misc/xmas/presents/effects/unwrap.prefab", 
                spawnPos
            );

            UnityEngine.Object.Destroy(grenade.GetComponent<DestroyOnGroundMissing>());
            UnityEngine.Object.Destroy(grenade.GetComponent<GroundWatch>());
            grenade.Spawn();

            activeGrenades.Add(grenade.net.ID);
            timer.Once(config.FuseTime + 5f, () => activeGrenades.Remove(grenade.net.ID));
        }

        void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (info?.Initiator is TimedExplosive explosive && 
                activeGrenades.Contains(explosive.net.ID))
            {
                info.damageTypes.Set(DamageType.Explosion, 
                    entity is BasePlayer ? config.PlayerDamage : config.StructureDamage);
            }
        }

        void OnEntityDeath(Drone drone, HitInfo info)
        {
            if (drone == null) return;

            foreach (var entity in BaseNetworkable.serverEntities)
            {
                if (entity is ComputerStation station && 
                    station.currentlyControllingEnt.Get(true) == drone)
                {
                    ExplodeDrone(drone);
                    return;
                }
            }
        }

        private void ExplodeDrone(Drone drone)
        {
            try 
            {
                var explosion = new Explosion()
                {
                    Position = drone.transform.position,
                    Radius = config.DroneExplosionRadius,
                    PlayerDamage = config.DronePlayerDamage,
                    StructureDamage = config.DroneStructureDamage
                };
                
                explosion.Detonate();

                Effect.server.Run(
                    "assets/prefabs/tools/c4/effects/c4_explosion.prefab", 
                    drone.transform.position
                );
            }
            catch (Exception e)
            {
                Puts($"Ошибка при взрыве дрона: {e}");
            }
        }

        void Unload()
        {
            foreach (var grenadeId in activeGrenades)
            {
                var entity = BaseNetworkable.serverEntities.Find(grenadeId);
                entity?.Kill();
            }
            activeGrenades.Clear();
        }

        class Explosion
        {
            public Vector3 Position { get; set; }
            public float Radius { get; set; }
            public float PlayerDamage { get; set; }
            public float StructureDamage { get; set; }

            public void Detonate()
            {
                List<BaseCombatEntity> hits = new List<BaseCombatEntity>();
                Vis.Entities(Position, Radius, hits);
                
                foreach (var entity in hits)
                {
                    if (entity == null) continue;
                    
                    var hitInfo = new HitInfo
                    {
                        Initiator = null,
                        Weapon = null,
                        HitPositionWorld = Position,
                        HitNormalWorld = Vector3.up,
                        damageTypes = new DamageTypeList()
                    };
                    
                    float damage = entity is BasePlayer 
                        ? PlayerDamage 
                        : StructureDamage;
                        
                    hitInfo.damageTypes.Set(DamageType.Explosion, damage);
                    entity.OnAttacked(hitInfo);
                }
            }
        }
    }
}