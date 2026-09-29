// RPG Character & Stats System — Stat bars (section 18).
//
// StatBar is the abstract visual: background, fill, and the "trace" ghost
// that lingers after damage (traceDecayDelay/traceDecaySpeed), with lerp-
// smoothed fill (fillLerpSpeed). Subclasses name WHICH stat they show; the
// section 18 rule is that bars REACT to gameplay-stat changes, never
// calculate them.
//
// The sandbox build has no IMGUI, so this file keeps the visuals as plain
// per-frame state (AnimateFill/AnimateTick + a Render description) — the
// Unity-side OnGUI/UGUI rendering subclasses this. Tests assert on the
// animated numbers, which is the part with logic.

using System;
using UnityEngine;

namespace RPGCharacterStats
{
    /// <summary>Section 18's base abstraction, minus any actual painting.</summary>
    public abstract class StatBar : MonoBehaviour
    {
        [Header("Background")]
        public Color backgroundColor = new Color(0f, 0f, 0f, 0.6f);
        [Range(0f, 1f)] public float backgroundOpacity = 0.6f;

        [Header("Fill")]
        public Color fillColor = Color.red;
        [Range(0f, 1f)] public float fillOpacity = 1f;
        public float fillLerpSpeed = 8f;

        [Header("Trace (the lagging ghost behind the fill)")]
        public bool showTrace = true;
        public Color traceColor = Color.white;
        [Range(0f, 1f)] public float traceOpacity = 0.6f;
        public float traceDecayDelay = 0.5f;
        public float traceDecaySpeed = 1.5f;

        public float MaxValue { get; private set; }
        public float CurrentValue { get; private set; }
        public float DisplayedFill { get; private set; }   // lerped toward CurrentValue
        public float DisplayedTrace { get; private set; }  // lerps down after the delay

        private float _timeSinceChange;

        public void SetMaxValue(float max)
        {
            MaxValue = max <= 0f ? 1f : max;
        }

        public void UpdateValue(float value)
        {
            float clamped = Mathf.Clamp(value, 0f, MaxValue);
            if (clamped != CurrentValue)
            {
                CurrentValue = clamped;
                _timeSinceChange = 0f;
            }
        }

        /// <summary>Advance the fill lerp and the trace decay; called from
        /// Update by the renderer subclass (or a test driving time).</summary>
        public void AnimateTick(float deltaTime)
        {
            DisplayedFill = Mathf.Lerp(DisplayedFill, CurrentValue,
                1f - Mathf.Pow(1f - Mathf.Clamp01(fillLerpSpeed * deltaTime), 2f));

            if (showTrace)
            {
                _timeSinceChange += deltaTime;
                if (_timeSinceChange > traceDecayDelay && DisplayedTrace > DisplayedFill)
                {
                    float speed = Mathf.Max(traceDecaySpeed, 0.01f) * MaxValue;
                    DisplayedTrace = Mathf.MoveTowards(DisplayedTrace, DisplayedFill, speed * deltaTime);
                }
                else if (DisplayedTrace < DisplayedFill)
                {
                    DisplayedTrace = DisplayedFill; // growth: trace snaps up
                }
            }
            else
            {
                DisplayedTrace = DisplayedFill;
            }
        }

        public void AnimateFill() { AnimateTick(Time.deltaTime); }
        public void AnimateTrace() { AnimateTick(Time.deltaTime); }

        /// <summary>0..1 fractions for the renderer.</summary>
        public float FillFraction { get { return MaxValue > 0f ? DisplayedFill / MaxValue : 0f; } }
        public float TraceFraction { get { return MaxValue > 0f ? DisplayedTrace / MaxValue : 0f; } }
    }

    /// <summary>Wiring: one bar bound to one gameplay-stat name of one
    /// character, auto-maxing from the stat's schema max when available.
    /// HealthBar is also the concrete bar the Character carries (4.4).</summary>
    public class HealthBar : StatBar
    {
        public string statName = "Health";

        private GameplayStats _gameplay;
        private CharacterStats _character;
        private bool _watching;

        /// <summary>Subscribe to the character's stat events (section 19's
        /// fan-out into UI). Safe to call repeatedly.</summary>
        public void Watch(Character character)
        {
            if (character == null) return;
            Watch(character.gameplayStats, character.characterStats);
        }

        public void Watch(GameplayStats gameplay, CharacterStats characterStats)
        {
            if (_watching)
            {
                if (_gameplay != null) _gameplay.server.OnStatChanged -= OnStatChanged;
                if (_character != null) _character.OnStatChanged -= OnStatChanged;
            }
            _gameplay = gameplay;
            _character = characterStats;
            _watching = true;

            if (_gameplay != null)
            {
                _gameplay.server.OnStatChanged += OnStatChanged;
                GameplayStatClient c = _gameplay.server.Find(statName);
                if (c != null) UpdateValue(c.output.NumericValue);
            }
            if (_character != null)
            {
                _character.OnStatChanged += OnStatChanged;
                StatField f = _character.Find(statName);
                if (f != null)
                {
                    UpdateValue(f.NumericValue);
                    StatSchemaEntry e = _character.Schema.Find(statName);
                    if (e != null && e.hasMax) SetMaxValue(e.maxValue);
                }
            }
        }

        private void OnStatChanged(StatField f)
        {
            if (f == null || f.name != statName) return;
            UpdateValue(f.NumericValue);
        }

        protected virtual void Update()
        {
            AnimateTick(Time.deltaTime);
        }

        protected virtual void OnDestroy()
        {
            if (_gameplay != null) _gameplay.server.OnStatChanged -= OnStatChanged;
            if (_character != null) _character.OnStatChanged -= OnStatChanged;
        }
    }

    // ---- the five named bars from section 18 ----

    public class ShieldBar : HealthBar
    {
        public ShieldBar() { statName = "Shield"; fillColor = new Color(0.4f, 0.7f, 1f); }
    }

    public class StaminaBar : HealthBar
    {
        public StaminaBar() { statName = "Stamina"; fillColor = new Color(1f, 0.85f, 0.2f); }
    }

    public class MagicBar : HealthBar
    {
        public MagicBar() { statName = "Magic"; fillColor = new Color(0.5f, 0.3f, 1f); }
    }

    public class XPBar : HealthBar
    {
        public XPBar() { statName = "XP"; fillColor = new Color(0.2f, 1f, 0.4f); }
    }
}
