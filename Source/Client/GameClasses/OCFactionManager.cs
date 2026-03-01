using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;
using Verse;
using Model;
using OCUnion;
using System.Reflection;

namespace GameClasses
{
	public class OCFactionManager
	{
		[CompilerGenerated]
		[Serializable]
		private sealed class c
		{
			public static readonly OCFactionManager.c c9 = new OCFactionManager.c();
			public static Func<Faction, string> c9__5_0;

			internal string b__5_0(Faction fac)
			{
				return fac.Name;
			}

		}

		public static void AddNewFaction(FactionOnline factionOnline)
        {
			if (factionOnline == null) return;

			var factionList = Find.FactionManager.AllFactionsListForReading;
			var existing = factionList.FirstOrDefault(f =>
				!f.IsPlayer
				&& ((factionOnline.loadID > 0 && f.loadID == factionOnline.loadID)
					|| (string.Equals(f.def?.defName, factionOnline.DefName?.Trim(), StringComparison.OrdinalIgnoreCase)
						&& string.Equals(f.def?.LabelCap, factionOnline.LabelCap?.Trim(), StringComparison.OrdinalIgnoreCase))));
			if (existing != null)
			{
				if (existing.loadID <= 0 && factionOnline.loadID > 0)
				{
					existing.loadID = factionOnline.loadID;
				}
				return;
			}

			var defName = factionOnline.DefName?.Trim();
			FactionDef facDef = string.IsNullOrWhiteSpace(defName)
				? null
				: DefDatabase<FactionDef>.GetNamedSilentFail(defName);
			if (facDef == null)
			{
				Loger.Log("Skip add faction. Missing FactionDef: " + factionOnline.DefName, Loger.LogLevel.WARNING);
				return;
			}

			Faction faction = new Faction();
			faction.def = facDef;
			faction.loadID = Math.Max(0, factionOnline.loadID);
			faction.colorFromSpectrum = FactionGenerator.NewRandomColorFromSpectrum(faction);

			var fallbackName = facDef.LabelCap.ToString();
			faction.Name = string.IsNullOrWhiteSpace(factionOnline.Name)
				? (string.IsNullOrWhiteSpace(factionOnline.LabelCap) ? fallbackName : factionOnline.LabelCap.Trim())
				: factionOnline.Name.Trim();
			//faction.centralMelanin = Rand.Value;
			foreach (Faction current in Find.FactionManager.AllFactionsListForReading)
			{
				faction.TryMakeInitialRelationsWith(current);
			}
			faction.TryGenerateNewLeader();

			Find.FactionManager.Add(faction);
			typeof(FactionManager).GetMethod("RecacheFactions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(Find.FactionManager, null);
		}

		public static void DeleteFaction(Faction faction)
        {
            try
            {
				if (faction == null)
				{
					return;
				}
				List<Faction> list = Find.FactionManager.AllFactionsListForReading;
				if (!list.Contains(faction))
				{
					return;
				}

				foreach (Settlement current in (from sett in Find.WorldObjects.Settlements
												where sett.Faction == faction
												select sett).ToList<Settlement>())
				{
					Find.WorldObjects.Remove(current);
				}

				List<Pawn> allMapsWorldAndTemporary_AliveOrDead = PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead;
				for (int i = 0; i < allMapsWorldAndTemporary_AliveOrDead.Count; i++)
				{
					Pawn pawn = allMapsWorldAndTemporary_AliveOrDead[i];
					if (pawn.Faction == faction && faction.leader != pawn)
					{
						pawn.SetFaction(null, null);
					}
				}

				for (int j = 0; j < Find.Maps.Count; j++)
				{
					Find.Maps[j].pawnDestinationReservationManager.Notify_FactionRemoved(faction);
				}

				Find.LetterStack.Notify_FactionRemoved(faction);
				faction.RemoveAllRelations();
				if (faction.leader != null)
				{
					faction.leader.SetFaction(null, null);
				}
				list.Remove(faction);
			}
			catch (Exception e)
			{
				Log.Error("OnlineCity: Error DeleteFaction >> " + e);
			}

		}

		public static void UpdateFactionIDS(List<FactionOnline> factionOnlineList)
        {
			var factionList = Find.FactionManager.AllFactionsListForReading;

			for (var i = 0; i < factionOnlineList.Count; i++)
			{
				var faction = factionList.FirstOrDefault(f => ValidateFaction(factionOnlineList[i], f));
				if (faction != null)
                {
					faction.loadID = factionOnlineList[i].loadID;
					Loger.Log("Successfully updated faction ID: " + faction.def.LabelCap);
				}
			}
		}

		private static bool ValidateFaction(FactionOnline fOnline1, Faction fOnline2)
		{
			if (fOnline1.DefName == fOnline2.def.defName &&
				fOnline1.LabelCap == fOnline2.def.LabelCap &&
				fOnline1.loadID != fOnline2.loadID)
			{
				return true;
			}
			return false;
		}
	}
}
