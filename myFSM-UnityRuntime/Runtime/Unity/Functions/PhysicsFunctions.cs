// myFSM Unity Runtime — Physics category (0x0400-0x0411, 18 overloads).
// Rigidbody(2D) velocity/mass/forces, grounded + colliding probes (query
// based: short downward raycast / overlap sampling, no event tracking), the
// nearest-contact normal approximation, and raycasts.

using UnityEngine;
using MyFSM.Core;

namespace MyFSM.Unity
{
    public static class PhysicsFunctions
    {
        public static FsmValue Run(FunctionDispatcher d, ushort id, FsmValue[] args, AiExecution exec)
        {
            switch (id)
            {
                case 0x0400: return GetVelocity3(d, args, exec);
                case 0x0401: return GetVelocity2(d, args, exec);
                case 0x0402: return SetVelocity3(d, args, exec);
                case 0x0403: return SetVelocity2(d, args, exec);
                case 0x0404: return GetMass3(d, args, exec);
                case 0x0405: return GetMass2(d, args, exec);
                case 0x0406: return ApplyForce3(d, args, exec);
                case 0x0407: return ApplyForce2(d, args, exec);
                case 0x0408: return ApplyImpulse3(d, args, exec);
                case 0x0409: return ApplyImpulse2(d, args, exec);
                case 0x040A: return IsGrounded3(d, args, exec);
                case 0x040B: return IsGrounded2(d, args, exec);
                case 0x040C: return IsColliding3(d, args, exec);
                case 0x040D: return IsColliding2(d, args, exec);
                case 0x040E: return CollisionNormal3(d, args, exec);
                case 0x040F: return CollisionNormal2(d, args, exec);
                case 0x0410: return Raycast3(d, args, exec);
                case 0x0411: return Raycast2(d, args, exec);
                default:
                    exec.Log.Error("unknown Physics function id " + id.ToString("X4"));
                    return FsmValue.Void;
            }
        }

        private static FsmValue GetVelocity3(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Rigidbody rb = d.ResolveComponent<Rigidbody>(args[0], exec, "getVelocity");
            if (rb == null) return FsmValue.MakeVec3(0f, 0f, 0f);
            return FsmConvert.FromV3(rb.velocity);
        }

        private static FsmValue GetVelocity2(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Rigidbody2D rb = d.ResolveComponent<Rigidbody2D>(args[0], exec, "getVelocity");
            if (rb == null) return FsmValue.MakeVec2(0f, 0f);
            return FsmConvert.FromV2(rb.velocity);
        }

        private static FsmValue SetVelocity3(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Rigidbody rb = d.ResolveComponent<Rigidbody>(args[0], exec, "setVelocity");
            if (rb != null) rb.velocity = FsmConvert.ToV3(args[1]);
            return FsmValue.Void;
        }

        private static FsmValue SetVelocity2(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Rigidbody2D rb = d.ResolveComponent<Rigidbody2D>(args[0], exec, "setVelocity");
            if (rb != null) rb.velocity = FsmConvert.ToV2(args[1]);
            return FsmValue.Void;
        }

        private static FsmValue GetMass3(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Rigidbody rb = d.ResolveComponent<Rigidbody>(args[0], exec, "getMass");
            if (rb == null) return FsmValue.MakeFloat(0f);
            return FsmValue.MakeFloat(rb.mass);
        }

        private static FsmValue GetMass2(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Rigidbody2D rb = d.ResolveComponent<Rigidbody2D>(args[0], exec, "getMass");
            if (rb == null) return FsmValue.MakeFloat(0f);
            return FsmValue.MakeFloat(rb.mass);
        }

        private static FsmValue ApplyForce3(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Rigidbody rb = d.ResolveComponent<Rigidbody>(args[0], exec, "applyForce");
            if (rb != null) rb.AddForce(FsmConvert.ToV3(args[1]));
            return FsmValue.Void;
        }

        private static FsmValue ApplyForce2(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Rigidbody2D rb = d.ResolveComponent<Rigidbody2D>(args[0], exec, "applyForce");
            if (rb != null) rb.AddForce(FsmConvert.ToV2(args[1]));
            return FsmValue.Void;
        }

        private static FsmValue ApplyImpulse3(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Rigidbody rb = d.ResolveComponent<Rigidbody>(args[0], exec, "applyImpulse");
            if (rb != null) rb.AddForce(FsmConvert.ToV3(args[1]), ForceMode.Impulse);
            return FsmValue.Void;
        }

        private static FsmValue ApplyImpulse2(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Rigidbody2D rb = d.ResolveComponent<Rigidbody2D>(args[0], exec, "applyImpulse");
            if (rb != null) rb.AddForce(FsmConvert.ToV2(args[1]), ForceMode2D.Impulse);
            return FsmValue.Void;
        }

        private static FsmValue IsGrounded3(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            GameObject go = d.ResolveGameObject(args[0], exec, "isGrounded");
            if (go == null) return FsmValue.MakeBool(false);
            Collider col = go.GetComponent<Collider>();
            Vector3 origin = go.transform.position + Vector3.up * 0.1f;
            float dist = 0.35f;
            if (col != null)
            {
                origin = col.bounds.center;
                dist = col.bounds.extents.y + 0.2f;
            }
            return FsmValue.MakeBool(Physics.Raycast(origin, Vector3.down, dist));
        }

        private static FsmValue IsGrounded2(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            GameObject go = d.ResolveGameObject(args[0], exec, "isGrounded");
            if (go == null) return FsmValue.MakeBool(false);
            Collider2D col = go.GetComponent<Collider2D>();
            Vector2 origin = new Vector2(go.transform.position.x, go.transform.position.y + 0.1f);
            float dist = 0.35f;
            if (col != null)
            {
                origin = col.bounds.center;
                dist = col.bounds.extents.y + 0.2f;
            }
            RaycastHit2D hit = Physics2D.Raycast(origin, Vector2.down, dist);
            return FsmValue.MakeBool(hit.collider != null);
        }

        private static FsmValue IsColliding3(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            GameObject go = d.ResolveGameObject(args[0], exec, "isColliding");
            if (go == null) return FsmValue.MakeBool(false);
            Vector3 center;
            float radius;
            OverlapProbe3(go, out center, out radius);
            Collider[] hits = Physics.OverlapSphere(center, radius);
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i] != null && hits[i].gameObject != go)
                    return FsmValue.MakeBool(true);
            }
            return FsmValue.MakeBool(false);
        }

        private static FsmValue IsColliding2(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            GameObject go = d.ResolveGameObject(args[0], exec, "isColliding");
            if (go == null) return FsmValue.MakeBool(false);
            Vector2 center;
            float radius;
            OverlapProbe2(go, out center, out radius);
            Collider2D[] hits = Physics2D.OverlapCircleAll(center, radius);
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i] != null && hits[i].gameObject != go)
                    return FsmValue.MakeBool(true);
            }
            return FsmValue.MakeBool(false);
        }

        private static FsmValue CollisionNormal3(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            GameObject go = d.ResolveGameObject(args[0], exec, "getCollisionNormal");
            if (go == null) return FsmValue.MakeVec3(0f, 1f, 0f);
            Vector3 center;
            float radius;
            OverlapProbe3(go, out center, out radius);
            Collider[] hits = Physics.OverlapSphere(center, radius);
            float best = float.MaxValue;
            Vector3 normal = Vector3.up;
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i] == null || hits[i].gameObject == go) continue;
                Vector3 closest = hits[i].ClosestPoint(center);
                float dd = (center - closest).sqrMagnitude;
                if (dd < best)
                {
                    best = dd;
                    Vector3 away = center - closest;
                    normal = away.sqrMagnitude > 1e-8f ? away.normalized : Vector3.up;
                }
            }
            return FsmConvert.FromV3(normal);
        }

        private static FsmValue CollisionNormal2(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            GameObject go = d.ResolveGameObject(args[0], exec, "getCollisionNormal");
            if (go == null) return FsmValue.MakeVec2(0f, 1f);
            Vector2 center;
            float radius;
            OverlapProbe2(go, out center, out radius);
            Collider2D[] hits = Physics2D.OverlapCircleAll(center, radius);
            float best = float.MaxValue;
            Vector2 normal = Vector2.up;
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i] == null || hits[i].gameObject == go) continue;
                Vector2 closest = hits[i].ClosestPoint(center);
                float dd = (center - closest).sqrMagnitude;
                if (dd < best)
                {
                    best = dd;
                    Vector2 away = center - closest;
                    normal = away.sqrMagnitude > 1e-8f ? away.normalized : Vector2.up;
                }
            }
            return FsmConvert.FromV2(normal);
        }

        private static void OverlapProbe3(GameObject go, out Vector3 center, out float radius)
        {
            Collider col = go.GetComponent<Collider>();
            if (col != null)
            {
                center = col.bounds.center;
                radius = col.bounds.extents.magnitude;
                if (radius < 0.1f) radius = 0.1f;
            }
            else
            {
                center = go.transform.position;
                radius = 0.3f;
            }
        }

        private static void OverlapProbe2(GameObject go, out Vector2 center, out float radius)
        {
            Collider2D col = go.GetComponent<Collider2D>();
            if (col != null)
            {
                center = col.bounds.center;
                radius = col.bounds.extents.magnitude;
                if (radius < 0.1f) radius = 0.1f;
            }
            else
            {
                center = new Vector2(go.transform.position.x, go.transform.position.y);
                radius = 0.3f;
            }
        }

        private static FsmValue Raycast3(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Vector3 dir = FsmConvert.ToV3(args[1]);
            if (dir.sqrMagnitude < 1e-8f) return FsmValue.MakeBool(false);
            return FsmValue.MakeBool(Physics.Raycast(
                FsmConvert.ToV3(args[0]), dir.normalized, args[2].F));
        }

        private static FsmValue Raycast2(FunctionDispatcher d, FsmValue[] args, AiExecution exec)
        {
            Vector2 dir = FsmConvert.ToV2(args[1]);
            if (dir.sqrMagnitude < 1e-8f) return FsmValue.MakeBool(false);
            RaycastHit2D hit = Physics2D.Raycast(
                FsmConvert.ToV2(args[0]), dir.normalized, args[2].F);
            return FsmValue.MakeBool(hit.collider != null);
        }
    }
}
