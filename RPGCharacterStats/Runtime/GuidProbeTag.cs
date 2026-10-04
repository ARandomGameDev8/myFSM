// RPG Character & Stats System — the probe's subject component (DIAGNOSTIC
// ONLY). A plain custom MonoBehaviour attached as the SOLE extra custom script
// on a spawned character, so a Play → Stop cycle can prove whether custom
// script components survive on their own. Remove together with
// Editor/SpawnProbe.cs once the question is answered.

using UnityEngine;

namespace RPGCharacterStats
{
    [DisallowMultipleComponent]
    public class GuidProbeTag : MonoBehaviour
    {
        [Tooltip("When this component was attached (set by the probe spawner).")]
        public string attachedAt = "";

        [Tooltip("How many Play → Stop cycles this instance has already been re-inspected through.")]
        public int survivedStops;
    }
}
