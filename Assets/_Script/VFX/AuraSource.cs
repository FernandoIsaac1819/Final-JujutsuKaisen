using System;
using System.Collections.Generic;
using UnityEngine;

namespace JJK.Aura
{
    /// <summary>
    /// Attach to any character GameObject that needs a cursed-energy aura. Self-registers
    /// into a live list while enabled so AuraManager can discover and provision it automatically.
    /// </summary>
    public class AuraSource : MonoBehaviour
    {
        [Tooltip("The normal cursed energy aura material for this character.")]
        [SerializeField] private Material _auraMaterial;
        public Material AuraMaterial => _auraMaterial;

        [Tooltip("The white RCT aura material, swapped in while Reverse Cursed Technique is active.")]
        [SerializeField] private Material _rctAuraMaterial;
        public Material RctAuraMaterial => _rctAuraMaterial;

        [Tooltip("Renderer(s) captured for the body aura silhouette.")]
        [SerializeField] private Renderer[] _bodyRenderers;
        public Renderer[] BodyRenderers => _bodyRenderers;

        public static readonly List<AuraSource> Active = new List<AuraSource>();

        public static event Action<AuraSource> Registered;
        public static event Action<AuraSource> Unregistered;

        private void OnEnable()
        {
            Active.Add(this);
            Registered?.Invoke(this);
        }

        private void OnDisable()
        {
            Active.Remove(this);
            Unregistered?.Invoke(this);
        }
    }
}