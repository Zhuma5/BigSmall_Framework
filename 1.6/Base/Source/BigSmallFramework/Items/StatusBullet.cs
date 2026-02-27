using RimWorld;
using System.Linq;
using Verse;
using Verse.Noise;
using static RimWorld.PsychicRitualRoleDef;

namespace BigAndSmall
{
    public class BS_StatusBullet : Bullet
    {
        public ModExtension_StatusAfflicter Props => def.GetModExtension<ModExtension_StatusAfflicter>();

        protected override void Impact(Thing hitThing, bool blockedByShield = false)
        {
            Map map = Map;
            var position = Position;
            
            if (Props != null && def.projectile.explosionRadius > 0)
            {
                Explode(map, position);
            }
            else if (Props != null && hitThing != null && hitThing is Pawn pawn)
            {
                if (!blockedByShield)
                {
                    ApplyStatusTo(pawn, 1);
                }
            }
            Destroy();
        }

        protected virtual void Explode(Map map, IntVec3 position)
        {
            var cells = GenRadial.RadialCellsAround(position, def.projectile.explosionRadius, useCenter: true);
            var cellsWithLoS = cells.Where(c => GenSight.LineOfSight(position, c, map)).ToList();
            var pawnsInRange = cellsWithLoS.SelectMany(c => c.GetThingList(map).Where(t => t is Pawn).Select(t => (Pawn)t)).ToList();
            var proj = def.projectile;
            if (def.projectile.explosionEffect != null)
            {
                Effecter effecter = def.projectile.explosionEffect.Spawn();
                if (def.projectile.explosionEffectLifetimeTicks != 0)
                {
                    map.effecterMaintainer.AddEffecterToMaintain(effecter, base.Position.ToVector3().ToIntVec3(), def.projectile.explosionEffectLifetimeTicks);
                }
                else
                {
                    effecter.Trigger(new TargetInfo(base.Position, map), new TargetInfo(base.Position, map));
                    effecter.Cleanup();
                }
            }
            GenExplosion.DoExplosion(
                position,
                map, proj.explosionRadius, proj.damageDef, launcher, DamageAmount, ArmorPenetration, proj.soundExplode,
                damageFalloff: proj.explosionDamageFalloff,
                weapon: equipmentDef,
                projectile: def,
                postExplosionSpawnThingDef: proj.postExplosionSpawnThingDef,
                postExplosionSpawnChance: proj.postExplosionSpawnChance,
                postExplosionSpawnThingCount: proj.postExplosionSpawnThingCount,
                postExplosionGasType: proj.postExplosionGasType,
                applyDamageToExplosionCellsNeighbors: proj.applyDamageToExplosionCellsNeighbors,
                preExplosionSpawnThingDef: proj.preExplosionSpawnThingDef,
                preExplosionSpawnChance: proj.preExplosionSpawnChance,
                preExplosionSpawnThingCount: proj.preExplosionSpawnThingCount,
                direction: origin.AngleToFlat(destination)
                );
            foreach (var otherPawn in pawnsInRange)
            {
                float distance = position.DistanceTo(otherPawn.Position);
                float falloff = 1;
                if (def.projectile.explosionDamageFalloff)
                {
                    falloff = 1 - (distance / def.projectile.explosionRadius);
                }
                ApplyStatusTo(otherPawn, falloff);
            }
        }

        private void ApplyStatusTo(Pawn pawn, float effect)
        {
            float severity = Props.severity;
            if (Props.scaleSeverityByDamage && def.projectile.damageDef != null)
            {
                severity *= DamageAmount;
            }
            float severityPerBodySize = Props.severityPart;
            if (Props.softScaleSeverityByBodySize && pawn.BodySize > 1)
            {
                severity /= UnityEngine.Mathf.Sqrt(pawn.BodySize);
                severityPerBodySize /= UnityEngine.Mathf.Sqrt(pawn.BodySize);
            }
            if (effect != 1)
            {
                severity *= effect;
            }
            if (Props.hediffToAdd != null)
            {
                Hediff oldHediff = pawn.health?.hediffSet?.GetFirstHediffOfDef(Props.hediffToAdd);
                if (oldHediff != null)
                {
                    oldHediff.Severity += severity;
                }
                else
                {
                    Hediff hediff = HediffMaker.MakeHediff(Props.hediffToAdd, pawn);
                    hediff.Severity = severity;
                    pawn.health.AddHediff(hediff);
                }
            }
            if (Props.hediffToAddToPart != null)
            {
                BodyPartRecord bodyPartRecord = pawn.health.hediffSet.GetNotMissingParts().RandomElement();
                Hediff hediff2 = HediffMaker.MakeHediff(Props.hediffToAddToPart, pawn, bodyPartRecord);
                hediff2.Severity = severityPerBodySize;
                pawn.health.AddHediff(hediff2, bodyPartRecord);
            }
        }
    }

    public class ModExtension_StatusAfflicter : DefModExtension
    {
        public HediffDef hediffToAdd = null;
        public float severity = 0.01f;
        public HediffDef hediffToAddToPart = null;
        public float severityPart = 0.01f;
        public bool softScaleSeverityByBodySize = false;
        public bool scaleSeverityByDamage = false;
    }

}
