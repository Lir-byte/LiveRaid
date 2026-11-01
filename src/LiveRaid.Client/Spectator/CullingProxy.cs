using System;
using System.Collections.Generic;
using System.Reflection;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;

namespace LiveRaid.Client.Spectator
{
    /// <summary>
    /// EFT switches parts of a level on and off with trigger zones (DisablerCullingObject: interiors, terrain, occlusion culling,
    /// lights), and a zone reacts only to the collider of "your" player. After your death that collider lies still, so everything away
    /// from the body stayed off: black rooms, missing objects (video 2026-10-19). While the camera is loose LiveRaid tells every zone
    /// itself whether the camera is inside it - the same entered / not entered state the game keeps for you.
    /// </summary>
    internal static class CullingProxy
    {
        private class Zone
        {
            public DisablerCullingObjectBase Owner;
            public Bounds Area;
            public bool HasArea;
            public readonly List<(ColliderReporter reporter, Collider[] colliders)> Normal = new List<(ColliderReporter, Collider[])>();
            public readonly List<(ColliderReporter reporter, Collider[] colliders)> Inverse = new List<(ColliderReporter, Collider[])>();
            public List<ColliderReporter> Entered;
            public List<ColliderReporter> EnteredInverse;
        }

        private static readonly FieldInfo ReportersField = AccessTools.Field(typeof(DisablerCullingObjectBase), "_colliderReporters");
        private static readonly FieldInfo InverseReportersField = AccessTools.Field(typeof(DisablerCullingObjectBase), "_inverseColliderReporters");
        private static readonly FieldInfo EnteredField = AccessTools.Field(typeof(DisablerCullingObjectBase), "_enteredColliders");
        private static readonly FieldInfo EnteredInverseField = AccessTools.Field(typeof(DisablerCullingObjectBase), "_enteredInverseColliders");

        private static List<Zone> _zones;
        private static float _next;
        private static readonly List<ColliderReporter> _buffer = new List<ColliderReporter>(4);

        public static int Switched { get; private set; }

        // Indoor / outdoor for the camera: exposure, shadows and sound environment follow "your" player's indoor triggers only.
        // After your death they stayed where the body lies - an outdoor exposure made every room the camera entered pitch black
        private static EFT.EnvironmentEffect.IndoorTrigger _lastTrigger;
        private static bool _environmentSet;

        public static void Clear()
        {
            _lastTrigger = null;
            _environmentSet = false;
            _zones = null;
            _next = 0f;
            Switched = 0;
        }

        public static void Tick(Vector3 viewer)
        {
            if (Time.time < _next)
            {
                return;
            }

            _next = Time.time + 0.25f;
            UpdateEnvironment(viewer);
            if (_zones == null)
            {
                Collect();
            }

            foreach (Zone zone in _zones)
            {
                if (zone.Owner == null)
                {
                    continue;
                }

                bool changed = Apply(zone.Normal, zone.Entered, zone.Area, viewer);
                changed |= Apply(zone.Inverse, zone.EnteredInverse, zone.Area, viewer);
                if (changed)
                {
                    Switched++;
                    zone.Owner.UpdateComponentsStatusOnUpdate();
                }
            }
        }

        private static void UpdateEnvironment(Vector3 viewer)
        {
            var environment = EFT.EnvironmentEffect.EnvironmentManager.Instance;
            EFT.Player who = TakeOver.Current ?? SpectatorSession.Original;
            if (environment == null || who == null)
            {
                return;
            }

            EFT.EnvironmentEffect.IndoorTrigger trigger = environment.TryFindTriggerByPos(viewer);
            if (_environmentSet && trigger == _lastTrigger)
            {
                return;
            }

            _environmentSet = true;
            _lastTrigger = trigger;
            environment.SetTriggerForPlayer(who, trigger);
            Log.Detail("debug", $"LR Spectator: camera is {(trigger == null ? "outdoors" : "indoors")} - light and sound set for it");
        }

        /// <summary>Makes the zone's entered list exactly the triggers the viewer is in. True when it changed.</summary>
        private static bool Apply(List<(ColliderReporter reporter, Collider[] colliders)> triggers, List<ColliderReporter> entered, Bounds area, Vector3 viewer)
        {
            if (entered == null || triggers.Count == 0)
            {
                return false;
            }

            _buffer.Clear();
            if (area.Contains(viewer))
            {
                foreach ((ColliderReporter reporter, Collider[] colliders) in triggers)
                {
                    if (reporter != null && Inside(colliders, viewer))
                    {
                        _buffer.Add(reporter);
                    }
                }
            }

            bool same = _buffer.Count == entered.Count;
            for (int i = 0; same && i < _buffer.Count; i++)
            {
                same = entered.Contains(_buffer[i]);
            }

            if (same)
            {
                return false;
            }

            entered.Clear();
            entered.AddRange(_buffer);
            return true;
        }

        private static bool Inside(Collider[] colliders, Vector3 p)
        {
            foreach (Collider c in colliders)
            {
                if (c == null || !c.bounds.Contains(p))
                {
                    continue;
                }

                // ClosestPoint works for primitives and convex meshes; a concave mesh trigger counts by its bounds
                if (c is MeshCollider mesh && !mesh.convex)
                {
                    return true;
                }

                if ((c.ClosestPoint(p) - p).sqrMagnitude < 0.0001f)
                {
                    return true;
                }
            }

            return false;
        }

        private static void Collect()
        {
            _zones = new List<Zone>();
            foreach (DisablerCullingObjectBase owner in UnityEngine.Object.FindObjectsOfType<DisablerCullingObjectBase>())
            {
                try
                {
                    Zone zone = new Zone
                    {
                        Owner = owner,
                        Entered = EnteredField?.GetValue(owner) as List<ColliderReporter>,
                        EnteredInverse = EnteredInverseField?.GetValue(owner) as List<ColliderReporter>,
                    };
                    bool any = Add(zone, zone.Normal, ReportersField?.GetValue(owner) as List<ColliderReporter>);
                    any |= Add(zone, zone.Inverse, InverseReportersField?.GetValue(owner) as List<ColliderReporter>);
                    if (any)
                    {
                        zone.Area.Expand(0.5f);
                        _zones.Add(zone);
                    }
                }
                catch (Exception e)
                {
                    Log.Detail("debug", "LR Spectator: culling zone skipped: " + e.Message);
                }
            }

            Log.Info($"LR Spectator: {_zones.Count} level culling zones now follow the camera");
        }

        private static bool Add(Zone zone, List<(ColliderReporter, Collider[])> into, List<ColliderReporter> reporters)
        {
            if (reporters == null)
            {
                return false;
            }

            bool any = false;
            foreach (ColliderReporter reporter in reporters)
            {
                if (reporter == null)
                {
                    continue;
                }

                Collider[] colliders = reporter.GetComponents<Collider>();
                foreach (Collider c in colliders)
                {
                    if (!zone.HasArea)
                    {
                        zone.Area = c.bounds;
                        zone.HasArea = true;
                    }
                    else
                    {
                        zone.Area.Encapsulate(c.bounds);
                    }

                    any = true;
                }

                into.Add((reporter, colliders));
            }

            return any;
        }
    }
}
