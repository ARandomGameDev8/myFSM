// myFSM Unity Runtime — Perception (0x0700-0x070D, 14), Steering
// (0x0800-0x0809, 10), Sensing (0x0900-0x0901, 2) and Control
// (0x0A00-0x0A02, 3) categories.
//
// lookAt posts a gradual-rotation goal (see MovementSystem); wait() suspends
// the AI's Update + Traversals until time elapses; waitUntil() suspends until
// its condition (re-evaluated every tick) turns true; emit() records an event
// in the main DB, retrievable via the query server.

using System.Collections.Generic;
using UnityEngine;
using MyFSM.Core;

namespace MyFSM.Unity
{
    // ------------------------------------------------------------------
    // Perception
    // ------------------------------------------------------------------

    public static class PerceptionFunctions
    {
        public static FsmValue Run(FunctionDispatcher d, ushort id, FsmValue[] args, AiExecution exec)
        {
            switch (id)
            {
                case 0x0700: return LookAt(d, args, exec, false);
                case 0x0701: return LookAt(d, args, exec, true);
                case 0x0702: return InLineOfSight(d, args, exec, false);
                case 0x0703: return InLineOfSight(d, args, exec, true);
                case 0x0704: return InRange(d, args, exec, false);
                case 0x0705: return InRange(d, args, exec, true);
                case 0x0706: return AngleTo(d, args, exec, false);
                case 0x0707: return AngleTo(d, args, exec, true);
                case 0x0708: return DistanceTo(d, args, exec, false);
                case 0x0709: return DistanceTo(d, args, exec, true);
                case 0x070A: return NearestOfTag3(d, args, exec);
                case 0x070B: return NearestOfTag2(d, args, exec);
                case 0x070C: return AllInRadius3(d, args, exec);
                case 0x070D: return AllInRadius2(d, args, exec);
                default:
                    exec.Log.Error("unknown Perception function id " + id.ToString("X4"));
                    return FsmValue.Void;
            }
        }

        private static FsmValue LookAt(FunctionDispatcher d, FsmValue[] args,
                                       AiExecution exec, bool is2D)
        {
            if (d.ResolveTransform(args[0], exec, "lookAt") == null) return FsmValue.Void;
            if (d.ResolveTransform(args[1], exec, "lookAt") == null) return FsmValue.Void;
            MoveGoal g = new MoveGoal();
            g.Mode = MoveMode.LookAt;
            g.Agent = args[0];
            g.Target = args[1];
            g.Is2D = is2D;
            d.Movement.SetGoal(args[0].HandleId, g);
            return FsmValue.Void;
        }

        private static FsmValue InLineOfSight(FunctionDispatcher d, FsmValue[] args,
                                              AiExecution exec, bool is2D)
        {
            Transform a = d.ResolveTransform(args[0], exec, "isInLineOfSight");
            Transform b = d.ResolveTransform(args[1], exec, "isInLineOfSight");
            if (a == null || b == null) return FsmValue.MakeBool(false);
            if (is2D)
            {
                Vector2 from = new Vector2(a.position.x, a.position.y);
                Vector2 to = new Vector2(b.position.x, b.position.y);
                Vector2 dir = to - from;
                float dist = dir.magnitude;
                if (dist < 1e-4f) return FsmValue.MakeBool(true);
                RaycastHit2D hit = Physics2D.Raycast(from, dir / dist, dist);
                if (hit.collider == null) return FsmValue.MakeBool(true);
                return FsmValue.MakeBool(hit.collider.gameObject == b.gameObject);
            }
            Vector3 from3 = a.position;
            Vector3 dir3 = b.position - from3;
            float dist3 = dir3.magnitude;
            if (dist3 < 1e-4f) return FsmValue.MakeBool(true);
            RaycastHit hit3;
            if (!Physics.Raycast(from3, dir3 / dist3, out hit3, dist3))
                return FsmValue.MakeBool(true);
            return FsmValue.MakeBool(hit3.collider != null &&
                                     hit3.collider.gameObject == b.gameObject);
        }

        private static FsmValue InRange(FunctionDispatcher d, FsmValue[] args,
                                        AiExecution exec, bool is2D)
        {
            Transform a = d.ResolveTransform(args[0], exec, "isInRange");
            Transform b = d.ResolveTransform(args[1], exec, "isInRange");
            if (a == null || b == null) return FsmValue.MakeBool(false);
            float radius = args[2].F;
            bool inside;
            if (is2D)
            {
                Vector2 diff = new Vector2(a.position.x - b.position.x,
                                           a.position.y - b.position.y);
                inside = diff.sqrMagnitude <= radius * radius;
            }
            else
            {
                inside = (a.position - b.position).sqrMagnitude <= radius * radius;
            }
            return FsmValue.MakeBool(inside);
        }

        private static FsmValue AngleTo(FunctionDispatcher d, FsmValue[] args,
                                        AiExecution exec, bool is2D)
        {
            Transform a = d.ResolveTransform(args[0], exec, "getAngleTo");
            Transform b = d.ResolveTransform(args[1], exec, "getAngleTo");
            if (a == null || b == null) return FsmValue.MakeFloat(0f);
            if (is2D)
            {
                // 2D facing convention: +X (transform.right).
                Vector2 fwd = new Vector2(a.right.x, a.right.y);
                Vector2 dir = new Vector2(b.position.x - a.position.x,
                                          b.position.y - a.position.y);
                if (dir.sqrMagnitude < 1e-8f) return FsmValue.MakeFloat(0f);
                return FsmValue.MakeFloat(Vector2.Angle(fwd, dir.normalized));
            }
            Vector3 dir3 = b.position - a.position;
            if (dir3.sqrMagnitude < 1e-8f) return FsmValue.MakeFloat(0f);
            return FsmValue.MakeFloat(Vector3.Angle(a.forward, dir3.normalized));
        }

        private static FsmValue DistanceTo(FunctionDispatcher d, FsmValue[] args,
                                           AiExecution exec, bool is2D)
        {
            Transform a = d.ResolveTransform(args[0], exec, "getDistanceTo");
            Transform b = d.ResolveTransform(args[1], exec, "getDistanceTo");
            if (a == null || b == null) return FsmValue.MakeFloat(0f);
            if (is2D)
            {
                Vector2 diff = new Vector2(a.position.x - b.position.x,
                                           a.position.y - b.position.y);
                return FsmValue.MakeFloat(diff.magnitude);
            }
            return FsmValue.MakeFloat(Vector3.Distance(a.position, b.position));
        }

        private static FsmValue NearestOfTag3(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Vector3 pos = FsmConvert.ToV3(args[0]);
            int tag = args[1].I;
            float radius = args[2].F;
            if (radius < 0f) radius = 0f;
            Collider[] hits = Physics.OverlapSphere(pos, radius);
            float best = float.MaxValue;
            GameObject bestGo = null;
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i] == null) continue;
                GameObject go = hits[i].gameObject;
                if (AstCodec.Fnv1a32(go.tag) != tag) continue;
                float dd = (go.transform.position - pos).sqrMagnitude;
                if (dd < best)
                {
                    best = dd;
                    bestGo = go;
                }
            }
            if (bestGo == null) return FsmValue.NullHandle(FsmbType.Object3D);
            return FsmValue.MakeHandle(FsmbType.Object3D,
                d.Handles.Alloc(bestGo, FsmbType.Object3D));
        }

        private static FsmValue NearestOfTag2(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Vector2 pos = FsmConvert.ToV2(args[0]);
            int tag = args[1].I;
            float radius = args[2].F;
            if (radius < 0f) radius = 0f;
            Collider2D[] hits = Physics2D.OverlapCircleAll(pos, radius);
            float best = float.MaxValue;
            GameObject bestGo = null;
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i] == null) continue;
                GameObject go = hits[i].gameObject;
                if (AstCodec.Fnv1a32(go.tag) != tag) continue;
                Vector2 gp = new Vector2(go.transform.position.x, go.transform.position.y);
                float dd = (gp - pos).sqrMagnitude;
                if (dd < best)
                {
                    best = dd;
                    bestGo = go;
                }
            }
            if (bestGo == null) return FsmValue.NullHandle(FsmbType.Object2D);
            return FsmValue.MakeHandle(FsmbType.Object2D,
                d.Handles.Alloc(bestGo, FsmbType.Object2D));
        }

        private static FsmValue AllInRadius3(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            float radius = args[1].F;
            if (radius < 0f) radius = 0f;
            Collider[] hits = Physics.OverlapSphere(FsmConvert.ToV3(args[0]), radius);
            return FsmValue.MakeInt(hits.Length);
        }

        private static FsmValue AllInRadius2(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            float radius = args[1].F;
            if (radius < 0f) radius = 0f;
            Collider2D[] hits = Physics2D.OverlapCircleAll(FsmConvert.ToV2(args[0]), radius);
            return FsmValue.MakeInt(hits.Length);
        }
    }

    // ------------------------------------------------------------------
    // Steering
    // ------------------------------------------------------------------

    public static class SteeringFunctions
    {
        public static FsmValue Run(FunctionDispatcher d, ushort id, FsmValue[] args, AiExecution exec)
        {
            switch (id)
            {
                case 0x0800: return Flee(d, args, exec, false);
                case 0x0801: return Flee(d, args, exec, true);
                case 0x0802: return Pursuit3(d, args, exec);
                case 0x0803: return Pursuit2(d, args, exec);
                case 0x0804: return Separation(d, args, exec, false);
                case 0x0805: return Separation(d, args, exec, true);
                case 0x0806: return Arrival(d, args, exec, false);
                case 0x0807: return Arrival(d, args, exec, true);
                case 0x0808: return Wander(d, args, exec, false);
                case 0x0809: return Wander(d, args, exec, true);
                default:
                    exec.Log.Error("unknown Steering function id " + id.ToString("X4"));
                    return FsmValue.Void;
            }
        }

        private static FsmValue Flee(FunctionDispatcher d, FsmValue[] args,
                                     AiExecution exec, bool is2D)
        {
            if (is2D)
            {
                Vector2 away = FsmConvert.ToV2(args[0]) - FsmConvert.ToV2(args[1]);
                if (away.sqrMagnitude < 1e-8f) return FsmValue.MakeVec2(0f, 0f);
                return FsmConvert.FromV2(away.normalized);
            }
            Vector3 away3 = FsmConvert.ToV3(args[0]) - FsmConvert.ToV3(args[1]);
            if (away3.sqrMagnitude < 1e-8f) return FsmValue.MakeVec3(0f, 0f, 0f);
            return FsmConvert.FromV3(away3.normalized);
        }

        private static FsmValue Pursuit3(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Transform t = d.ResolveTransform(args[0], exec, "getPursuitPosition");
            if (t == null) return FunctionDispatcher.InvalidPosition3(exec, "getPursuitPosition");
            // One-second lead on the target's velocity (zero when still or
            // when no positive closing speed is given).
            Vector3 vel = Vector3.zero;
            Rigidbody rb = t.gameObject.GetComponent<Rigidbody>();
            if (rb != null) vel = rb.velocity;
            if (args[1].F <= 0f || vel.sqrMagnitude < 1e-8f)
                return FsmConvert.FromV3(t.position);
            return FsmConvert.FromV3(t.position + vel);
        }

        private static FsmValue Pursuit2(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Transform t = d.ResolveTransform(args[0], exec, "getPursuitPosition");
            if (t == null) return FunctionDispatcher.InvalidPosition2(exec, "getPursuitPosition");
            Vector2 vel = Vector2.zero;
            Rigidbody2D rb = t.gameObject.GetComponent<Rigidbody2D>();
            if (rb != null) vel = rb.velocity;
            Vector2 p = new Vector2(t.position.x, t.position.y);
            if (args[1].F <= 0f || vel.sqrMagnitude < 1e-8f)
                return FsmConvert.FromV2(p);
            return FsmConvert.FromV2(p + vel);
        }

        private static FsmValue Separation(FunctionDispatcher d, FsmValue[] args,
                                           AiExecution exec, bool is2D)
        {
            Transform at = d.ResolveTransform(args[0], exec, "getSeparationVector");
            if (at == null)
                return is2D ? FsmValue.MakeVec2(0f, 0f) : FsmValue.MakeVec3(0f, 0f, 0f);
            int want = args[1].I;
            if (want <= 0)
                return is2D ? FsmValue.MakeVec2(0f, 0f) : FsmValue.MakeVec3(0f, 0f, 0f);
            if (is2D)
            {
                Vector2 p = new Vector2(at.position.x, at.position.y);
                Collider2D[] hits = Physics2D.OverlapCircleAll(
                    p, FunctionDispatcher.DefaultSeparationRadius);
                Vector2 sum = Vector2.zero;
                int taken = 0;
                for (int i = 0; i < hits.Length && taken < want; i++)
                {
                    if (hits[i] == null || hits[i].gameObject == at.gameObject) continue;
                    Vector2 op = new Vector2(hits[i].transform.position.x,
                                             hits[i].transform.position.y);
                    Vector2 away = p - op;
                    float dist = away.magnitude;
                    if (dist < 1e-4f) continue;
                    sum += away / dist / Mathf.Max(dist, 0.01f);
                    taken++;
                }
                if (sum.sqrMagnitude < 1e-8f) return FsmValue.MakeVec2(0f, 0f);
                return FsmConvert.FromV2(sum.normalized);
            }
            Vector3 p3 = at.position;
            Collider[] hits3 = Physics.OverlapSphere(
                p3, FunctionDispatcher.DefaultSeparationRadius);
            Vector3 sum3 = Vector3.zero;
            int taken3 = 0;
            for (int i = 0; i < hits3.Length && taken3 < want; i++)
            {
                if (hits3[i] == null || hits3[i].gameObject == at.gameObject) continue;
                Vector3 away = p3 - hits3[i].transform.position;
                float dist = away.magnitude;
                if (dist < 1e-4f) continue;
                sum3 += away / dist / Mathf.Max(dist, 0.01f);
                taken3++;
            }
            if (sum3.sqrMagnitude < 1e-8f) return FsmValue.MakeVec3(0f, 0f, 0f);
            return FsmConvert.FromV3(sum3.normalized);
        }

        private static FsmValue Arrival(FunctionDispatcher d, FsmValue[] args,
                                        AiExecution exec, bool is2D)
        {
            if (is2D)
            {
                Vector2 want = FsmConvert.ToV2(args[1]) - FsmConvert.ToV2(args[0]);
                float dist = want.magnitude;
                if (dist < 1e-4f) return FsmValue.MakeVec2(0f, 0f);
                float slow = args[2].F;
                float s = slow > 0f ? Mathf.Min(dist / slow, 1f) : 1f;
                return FsmConvert.FromV2(want / dist * s);
            }
            Vector3 want3 = FsmConvert.ToV3(args[1]) - FsmConvert.ToV3(args[0]);
            float dist3 = want3.magnitude;
            if (dist3 < 1e-4f) return FsmValue.MakeVec3(0f, 0f, 0f);
            float slow3 = args[2].F;
            float s3 = slow3 > 0f ? Mathf.Min(dist3 / slow3, 1f) : 1f;
            return FsmConvert.FromV3(want3 / dist3 * s3);
        }

        private static FsmValue Wander(FunctionDispatcher d, FsmValue[] args,
                                       AiExecution exec, bool is2D)
        {
            if (is2D)
            {
                Vector2 r = Random.insideUnitCircle;
                if (r.sqrMagnitude < 1e-4f) r = Vector2.right;
                return FsmConvert.FromV2(r.normalized * args[1].F);
            }
            Vector3 r3 = Random.insideUnitSphere;
            r3.y = 0f; // planar wander
            if (r3.sqrMagnitude < 1e-4f) r3 = Vector3.right;
            return FsmConvert.FromV3(r3.normalized * args[1].F);
        }
    }

    // ------------------------------------------------------------------
    // Sensing
    // ------------------------------------------------------------------

    public static class SensingFunctions
    {
        public static FsmValue Run(FunctionDispatcher d, ushort id, FsmValue[] args, AiExecution exec)
        {
            switch (id)
            {
                case 0x0900: return Hit3(d, args, exec);
                case 0x0901: return Hit2(d, args, exec);
                default:
                    exec.Log.Error("unknown Sensing function id " + id.ToString("X4"));
                    return FsmValue.Void;
            }
        }

        private static FsmValue Hit3(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Vector3 dir = FsmConvert.ToV3(args[1]);
            if (dir.sqrMagnitude < 1e-8f) return FsmValue.NullHandle(FsmbType.Object3D);
            RaycastHit hit;
            if (Physics.Raycast(FsmConvert.ToV3(args[0]), dir.normalized, out hit, args[2].F) &&
                hit.collider != null)
            {
                return FsmValue.MakeHandle(FsmbType.Object3D,
                    d.Handles.Alloc(hit.collider.gameObject, FsmbType.Object3D));
            }
            return FsmValue.NullHandle(FsmbType.Object3D);
        }

        private static FsmValue Hit2(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Vector2 dir = FsmConvert.ToV2(args[1]);
            if (dir.sqrMagnitude < 1e-8f) return FsmValue.NullHandle(FsmbType.Object2D);
            RaycastHit2D hit = Physics2D.Raycast(
                FsmConvert.ToV2(args[0]), dir.normalized, args[2].F);
            if (hit.collider != null)
            {
                return FsmValue.MakeHandle(FsmbType.Object2D,
                    d.Handles.Alloc(hit.collider.gameObject, FsmbType.Object2D));
            }
            return FsmValue.NullHandle(FsmbType.Object2D);
        }
    }

    // ------------------------------------------------------------------
    // Control
    // ------------------------------------------------------------------

    public static class ControlFunctions
    {
        public static FsmValue Run(FunctionDispatcher d, ushort id, FsmValue[] args, AiExecution exec)
        {
            switch (id)
            {
                case 0x0A00: // wait(seconds): suspend Update+Traversals until time elapses
                    exec.SuspendUntil(exec.Time.Time + Mathf.Max(0f, args[0].F));
                    return FsmValue.Void;
                case 0x0A01: // waitUntil(cond): suspend until the condition turns true
                    if (!args[0].B)
                    {
                        int cond = exec.PendingWaitCondAst;
                        if (cond < 0) exec.Log.Error("waitUntil lost its condition");
                        else exec.SuspendOnCondition(cond);
                    }
                    return FsmValue.Void;
                case 0x0A02: // emit(eventId): record into the main DB (queryable)
                    {
                        MainServer main = MainServer.Instance;
                        if (main == null)
                        {
                            exec.Log.Error("emit: no main server");
                            return FsmValue.Void;
                        }
                        main.Db.RecordEmit(new EmitEntry
                        {
                            Tick = main.Db.TotalTicks,
                            Time = exec.Time.Time,
                            InstanceId = exec.InstanceId,
                            InstanceName = exec.InstanceName,
                            EventId = args[0].I
                        });
                        return FsmValue.Void;
                    }
                default:
                    exec.Log.Error("unknown Control function id " + id.ToString("X4"));
                    return FsmValue.Void;
            }
        }
    }
}
