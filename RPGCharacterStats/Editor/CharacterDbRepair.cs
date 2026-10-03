// RPG Character & Stats System — CharacterDB status menu.
//
// The database HEALS ITSELF: the first DB touch of an editor session
// migrates legacy .asset rows to JSON records and re-links any row whose
// script GUID went stale (folder delete + re-copy). This menu is just the
// manual "do it right now and tell me what's in there" button — the same
// code path the server and the windows trigger automatically.

using UnityEditor;
using UnityEngine;

namespace RPGCharacterStats.EditorTools
{
    public static class CharacterDbRepair
    {
        [MenuItem("RPG/CharacterDB — Check & Heal", priority = 20)]
        public static void CheckAndHeal()
        {
            int rows = CharacterDB.HealNow();

            Debug.Log("[RPGStats] CharacterDB: " + rows + " character record(s) on disk. " +
                      "Legacy .asset rows (including any with broken script links) are migrated " +
                      "to GUID-proof JSON records automatically; there is nothing to repair by hand.");

            EditorUtility.DisplayDialog("CharacterDB — Check & Heal",
                rows + " character record(s) in " + CharacterDB.EditorFolder + ".\n\n" +
                "Records are JSON now: they survive folder re-copies, script re-imports and " +
                "refactors with no repair steps. Old .asset rows (even broken ones) migrate " +
                "automatically the moment the database is touched.\n\n" +
                "Note: scene objects spawned BEFORE a package re-copy still carry Unity's " +
                "script GUID references — delete and re-spawn those; the data itself is safe.",
                "OK");
        }
    }
}
