using RimWorld;
using System;
using System.Linq;
using UnityEngine;
using Verse;

namespace BigAndSmall
{
    public static class Soul
    {
        public static SoulCollector GetOrAddSoulCollector(Pawn attacker)
        {
            if (BSDefs.BS_SoulCollector == null)
            {
                Log.Warning("Soul Collector Hediff is null. This is likely due to a missing mod or a missing def.");
                return null;
            }
            SoulCollector soulCollector = (SoulCollector)attacker.health.hediffSet.GetFirstHediffOfDef(BSDefs.BS_SoulCollector);
            if (soulCollector == null)
            {
                // Add the soul to the attacker.
                attacker.health.AddHediff(BSDefs.BS_SoulCollector);
                soulCollector = (SoulCollector)attacker.health.hediffSet.GetFirstHediffOfDef(BSDefs.BS_SoulCollector);
            }
            return soulCollector;
        }
    }

    public class CompProperties_ConsumeSoul : CompProperties_AbilityEffect
    {
        public float gainMultiplier = 1;
        public float? gainSkillMultiplier = null;
        public float exponentialFalloff = 2.5f;
        public bool doKill = true;
        public bool doEnslave = false;

        public CompProperties_ConsumeSoul()
        {
            compClass = typeof(CompAbilityEffect_ConsumeSoul);
        }
    }

    public class CompAbilityEffect_ConsumeSoul : CompAbilityEffect
    {
        public new CompProperties_ConsumeSoul Props => (CompProperties_ConsumeSoul)props;

        public override void Apply(LocalTargetInfo target, LocalTargetInfo dest)
        {
            base.Apply(target, dest);
            Pawn pawn = target.Pawn;
            if (pawn != null)
            {
                DrainSoul(parent.pawn, pawn);
            }
        }

        public override bool CanApplyOn(LocalTargetInfo target, LocalTargetInfo dest)
        {
            return Valid(target);
        }

        public override bool Valid(LocalTargetInfo target, bool throwMessages = false)
        {
            Pawn enemy = target.Pawn;
            if (enemy == null)
            {
                return false;
            }
            if (!AbilityUtility.ValidateMustBeHumanOrWildMan(enemy, throwMessages, parent))
            {
                return false;
            }
            if (!enemy.Downed && enemy.DevelopmentalStage > DevelopmentalStage.Baby)
            {
                if (throwMessages)
                {
                    Messages.Message("MessageCantUseOnResistingPerson".Translate(parent.def.LabelCap), enemy, MessageTypeDefOf.RejectInput, historical: false);
                }
                return false;
            }
            if (enemy.IsWildMan() && !enemy.IsPrisonerOfColony && !enemy.Downed)
            {
                if (throwMessages)
                {
                    Messages.Message("MessageCantUseOnResistingPerson".Translate(parent.def.LabelCap), enemy, MessageTypeDefOf.RejectInput, historical: false);
                }
                return false;
            }
            if (enemy.health.hediffSet.HasHediff(BSDefs.BS_Soulless))
            {
                if (throwMessages)
                {
                    Messages.Message("BS_CannotUseOnSoulless".Translate(), enemy, MessageTypeDefOf.RejectInput, historical: false);
                }
                return false;
            }
            return true;
        }

        public override string ExtraLabelMouseAttachment(LocalTargetInfo target)
        {
            Pawn pawn = target.Pawn;
            if (pawn != null)
            {
                string text = null;
                if (pawn.HostileTo(parent.pawn) && !pawn.Downed)
                {
                    text += "MessageCantUseOnResistingPerson".Translate(parent.def.Named("ABILITY"));
                }
                return text;
            }
            return base.ExtraLabelMouseAttachment(target);
        }

        public override Window ConfirmationDialog(LocalTargetInfo target, Action confirmAction)
        {
            return null;
        }

        public void DrainSoul(Pawn attacker, Pawn victim)
        {
            // Add Psyfocus to the attacker.
            attacker.psychicEntropy?.OffsetPsyfocusDirectly(1.0f);

            // Check if the attacker has Soul Collector hediff.

            SoulCollector soulCollector = MakeGetSoulCollectorHediff(attacker);
            SiphonSoul parms = new SiphonSoul
            {
                type = 0,
                gainMultiplier = Props.gainMultiplier,
                exponentialFalloff = Props.exponentialFalloff,
                gainSkillMultiplier = Props.gainSkillMultiplier
            };
            soulCollector.AddPawnSoul(victim, parms);

            // Remove 50 goodwill from the victim's faction. faction.
            victim.Faction?.TryAffectGoodwillWith(attacker.Faction, -35); // statOffsetsBySeverity

            if (Props.doKill)
            {
                ApplySoulless(victim);
                victim.Kill(null);
            }
            else if (Props.doEnslave)
            {
                ApplySoulless(victim);
                // If Ideology is active, enslave the pawn.
                if (ModsConfig.IdeologyActive)
                {
                    victim.guest.SetGuestStatus(attacker.Faction, GuestStatus.Slave);
                }
                else
                {
                    victim.guest.resistance = 0;

                    // Set to prisoner.
                    victim.guest.CapturedBy(Faction.OfPlayer, attacker);
                }
            }

            HumanoidPawnScaler.GetCache(attacker, forceRefresh: true);
        }

        public static void ApplySoulless(Pawn victim)
        {
            if (victim?.RaceProps?.Humanlike == true)
            {
                victim?.health.AddHediff(BSDefs.BS_Soulless);
            }
        }

        public static SoulCollector MakeGetSoulCollectorHediff(Pawn attacker) => Soul.GetOrAddSoulCollector(attacker);
    }

    public class SoulCollector : HediffWithComps
    {
        protected readonly SoulEnergyTracker soulTracker = new();
        protected SoulResourceHediff Resource => soulTracker.Resource(pawn);

        public void AddSoulPowerDirect(float amount, float exponentialFalloff = 2.5f)
        {
            if (Severity >= 1) { amount /= (Mathf.Pow(Severity, exponentialFalloff)); }

            Severity += amount;
        }

        public void AddPawnSoul(Pawn target, SiphonSoul parms, bool verbose=false, float limitPerTrigger=2)
            //SiphonType gainMethod, float baseAmount = 0, float multiplier = 1, float exponentialFalloff=2.5f, float? gainSkillMultiplier = null)
        {
            const float tuneSPGain = 0.25f;
            const float tunePSGain = 0.25f;
            const float skillGainCap = 0.5f;

            // Get psychic sensitivity of the pawn.
            float gainPS = target.GetStatValue(StatDefOf.PsychicSensitivity) - 0.85f; // We mostly care about sensitivity ab1ove 1.
            float originalAmount = gainPS;

            gainPS = Mathf.Max(0.1f, gainPS) * parms.gainMultiplier;

            float postCap = gainPS;

            if (target?.RaceProps?.Animal == true) { gainPS /= 5; }

            int? architeGeneCount = target.genes?.GenesListForReading.Sum(x=>x.def.biostatArc);

            switch (architeGeneCount)
            {
                case 0:
                    gainPS /= 4f;
                    break;
                case 1:
                    gainPS /= 3f;
                    break;
                case 2:
                    gainPS /= 2f;
                    break;
                default:
                    //gainOffset /= 1.0f;
                    break;
            }
            gainPS *= tunePSGain;

            float gainFromSoulPower = target.GetStatValue(BSDefs.BS_SoulPower) * tuneSPGain;

            float falloffStartOffset = pawn.GetAllPawnExtensions().Sum(x => x.soulFalloffStart);
            float siphonFactor = 1 + pawn.GetAllPawnExtensions().Sum(x => x.siphonFactorOffset);
            float siphonSkillMult = 1 + pawn.GetAllPawnExtensions().Sum(x => x.siphonSkillFactorOffset);

            float preFalloffTotalGain = (gainPS + gainFromSoulPower + parms.baseAmount)* siphonFactor;


            if (preFalloffTotalGain > limitPerTrigger)
            {
                // Clamp, but avoid being too obvious about it.
                preFalloffTotalGain = Mathf.Clamp(Mathf.Lerp(preFalloffTotalGain, limitPerTrigger, 0.95f), 0, limitPerTrigger + 0.1f);
            }

            if (Resource != null)
            {
                Resource.Value += preFalloffTotalGain * 50;
            }
            


            float actualGain = 0;
            const int itrrCount = 10;
            float softCapMod = pawn.GetStatValue(BSDefs.BS_SoulPower) - Severity; // Soul power 
            softCapMod += falloffStartOffset;
            softCapMod += BigSmallMod.settings.soulPowerFalloffOffset;
            float adjustedV = Severity - softCapMod;
            // This is really just and ugly-looking way to make sure the falloff gets applied reasonably if adding a huge amount at the same time.
            for (int i = 0; i < itrrCount; i++)
            {
                float itrrGain = gainPS / itrrCount;
                if (adjustedV > 1) { itrrGain /= (Mathf.Pow(adjustedV + actualGain, parms.exponentialFalloff)); }
                actualGain += itrrGain;
            }
            float finalResult = actualGain * BigSmallMod.settings.soulPowerGainMultiplier;
            Severity += finalResult;

            if (pawn.needs?.TryGetNeed<Need_KillThirst>() is Need_KillThirst killThirst)
            {
                killThirst.CurLevelPercentage = 1;
            }
            target.psychicEntropy?.OffsetPsyfocusDirectly(0.6f);

            if (verbose || actualGain > 0.2f)
            {
                Messages.Message("BS_GainedSoulPower".Translate(pawn.Name.ToStringShort, ($"{finalResult*100:f0}%"), BSDefs.BS_SoulPower.LabelCap, target.Name.ToStringShort), pawn, MessageTypeDefOf.PositiveEvent); 
            }

            if (parms.gainSkillMultiplier != null && target.skills != null)
            {
                float sGainMult = parms.gainSkillMultiplier.Value * siphonSkillMult * Mathf.Min(skillGainCap, preFalloffTotalGain);
                SkillDef philophagySkillAndXpTransfer = PsychicRitualUtility.GetPhilophagySkillAndXpTransfer(pawn, target, sGainMult, out float xpTransfer);
                if (philophagySkillAndXpTransfer == null)
                {
                    Log.Warning("Could not find a skill to transfer xp to.");
                }
                else
                {
                    SkillRecord skill = pawn.skills.GetSkill(philophagySkillAndXpTransfer);
                    skill?.Learn(xpTransfer);
                    if (xpTransfer > 3000)
                    {
                        if (parms.type == SiphonType.KillingBlow)
                        {
                            Messages.Message("BS_StoleSkillAmount_Attack".Translate(pawn.Name.ToStringShort, xpTransfer, skill.def.label, target.Name.ToStringShort),
                                pawn, MessageTypeDefOf.PositiveEvent);
                        }
                        else if (parms.type == SiphonType.Influence)
                        {
                            Messages.Message("BS_StoleSkillAmount_Influence".Translate(pawn.Name.ToStringShort, xpTransfer, skill.def.label, target.Name.ToStringShort),
                                pawn, MessageTypeDefOf.PositiveEvent);
                        }
                        else
                        {
                            Messages.Message("BS_StoleSkillAmount".Translate(pawn.Name.ToStringShort, xpTransfer, skill.def.label, target.Name.ToStringShort),
                                pawn, MessageTypeDefOf.PositiveEvent);
                        }
                    }
                }
            }

            if (Severity > 10) { Severity = 10; }

            if (target.Spawned && (parms.type == SiphonType.KillingBlow || parms.type == SiphonType.None))
            {
                for (int i = 0; i < 5; i++)
                {
                    IntVec3 c = target.Position + GenAdj.AdjacentCellsAndInside[Rand.Range(0, 8)];
                    if (c.InBounds(target.Map))
                    {
                        FilthMaker.TryMakeFilth(c, target.Map, ThingDefOf.Filth_Ash, 1);
                    }
                }
            }
        }

        public override string LabelInBrackets
        {
            get
            {
                // Return pawn BodySize / BodySize of contents to percent.
                try
                {
                    return Severity.ToStringPercent();
                }
                catch
                {
                    return "SPIRIT POWER CALCULATION FAILED";
                }
            }
        }
    }
}
