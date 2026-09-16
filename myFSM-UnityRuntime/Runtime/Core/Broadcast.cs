// myFSM Unity Runtime — live broadcast servers for state changes.
//
// Two independent servers, both fed by the main server on every tick that
// actually changes a head state:
// - OrderedBroadcastServer: subscribers are informed in subscription order.
// - PriorityBroadcastServer: higher priority first. After being served, each
//   priority subscriber decides (via NextVerdict) how lower-priority
//   subscribers are treated: served, skipped for this tick, or de-registered.
//
// Subscriptions are per target AI (a GameObject with an AIInstance), stored as
// target -> subscriber-list dictionaries. Target AnyTarget (-1) listens to
// every AI. Subscriber callbacks return void and take either no arguments or
// the new state's name.

using System;
using System.Collections.Generic;

namespace MyFSM.Core
{
    public sealed class StateChangeEvent
    {
        public int InstanceId;
        public string InstanceName;
        public string FromState;
        public string ToState;
        public string Reason;
        public long Tick;
        public float Time;
    }

    public abstract class StateSubscriber
    {
        public const int AnyTarget = -1;

        private static int _nextId = 1;
        private static long _seq;

        public readonly int SubscriberId;
        public readonly int TargetInstanceId;
        internal readonly long SubscribeSeq;

        protected StateSubscriber(int targetInstanceId)
        {
            SubscriberId = _nextId++;
            TargetInstanceId = targetInstanceId;
            SubscribeSeq = ++_seq;
        }

        public abstract void Notify(StateChangeEvent e);
    }

    public sealed class ActionSubscriber : StateSubscriber
    {
        private readonly Action _callback;

        public ActionSubscriber(int targetInstanceId, Action callback)
            : base(targetInstanceId)
        {
            _callback = callback;
        }

        public override void Notify(StateChangeEvent e)
        {
            if (_callback != null) _callback();
        }
    }

    public sealed class ActionWithStateSubscriber : StateSubscriber
    {
        private readonly Action<string> _callback;

        public ActionWithStateSubscriber(int targetInstanceId, Action<string> callback)
            : base(targetInstanceId)
        {
            _callback = callback;
        }

        public override void Notify(StateChangeEvent e)
        {
            if (_callback != null) _callback(e.ToState);
        }
    }

    public enum PriorityVerdict
    {
        /// <summary>Lower-priority subscribers are served normally.</summary>
        ServeRest,
        /// <summary>Lower-priority subscribers are skipped for this tick.</summary>
        SkipRestThisTick,
        /// <summary>Lower-priority subscribers are de-registered.</summary>
        DeregisterLower
    }

    public abstract class PriorityStateSubscriber : StateSubscriber
    {
        public readonly int Priority;
        /// <summary>
        /// Set during (or right after) your callback; the server reads it once
        /// per publish and resets it to ServeRest.
        /// </summary>
        public PriorityVerdict NextVerdict = PriorityVerdict.ServeRest;

        protected PriorityStateSubscriber(int targetInstanceId, int priority)
            : base(targetInstanceId)
        {
            Priority = priority;
        }
    }

    public sealed class PriorityActionSubscriber : PriorityStateSubscriber
    {
        private readonly Action _voidCallback;
        private readonly Action<string> _stateCallback;

        public PriorityActionSubscriber(int targetInstanceId, int priority, Action callback)
            : base(targetInstanceId, priority)
        {
            _voidCallback = callback;
        }

        public PriorityActionSubscriber(int targetInstanceId, int priority, Action<string> callback)
            : base(targetInstanceId, priority)
        {
            _stateCallback = callback;
        }

        public override void Notify(StateChangeEvent e)
        {
            if (_voidCallback != null) _voidCallback();
            else if (_stateCallback != null) _stateCallback(e.ToState);
        }
    }

    /// <summary>No-priority server: subscription order, every tick a change lands.</summary>
    public sealed class OrderedBroadcastServer
    {
        private readonly Dictionary<int, List<StateSubscriber>> _byTarget =
            new Dictionary<int, List<StateSubscriber>>();

        public int SubscriberCount
        {
            get
            {
                int n = 0;
                foreach (KeyValuePair<int, List<StateSubscriber>> kv in _byTarget)
                    n += kv.Value.Count;
                return n;
            }
        }

        public void Subscribe(StateSubscriber s)
        {
            List<StateSubscriber> list;
            if (!_byTarget.TryGetValue(s.TargetInstanceId, out list))
            {
                list = new List<StateSubscriber>();
                _byTarget[s.TargetInstanceId] = list;
            }
            list.Add(s);
        }

        public bool Unsubscribe(int subscriberId)
        {
            foreach (KeyValuePair<int, List<StateSubscriber>> kv in _byTarget)
            {
                for (int i = 0; i < kv.Value.Count; i++)
                {
                    if (kv.Value[i].SubscriberId == subscriberId)
                    {
                        kv.Value.RemoveAt(i);
                        return true;
                    }
                }
            }
            return false;
        }

        public void Publish(StateChangeEvent e)
        {
            PublishList(e, e.InstanceId);
            if (e.InstanceId != StateSubscriber.AnyTarget)
                PublishList(e, StateSubscriber.AnyTarget);
        }

        private void PublishList(StateChangeEvent e, int target)
        {
            List<StateSubscriber> list;
            if (!_byTarget.TryGetValue(target, out list)) return;
            // Snapshot: callbacks may (un)subscribe re-entrantly.
            StateSubscriber[] snapshot = list.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
                snapshot[i].Notify(e);
        }
    }

    /// <summary>
    /// Priority server: higher priority first, ties in subscription order.
    /// After each subscriber, its NextVerdict governs the lower priorities.
    /// </summary>
    public sealed class PriorityBroadcastServer
    {
        private readonly Dictionary<int, List<PriorityStateSubscriber>> _byTarget =
            new Dictionary<int, List<PriorityStateSubscriber>>();

        public int SubscriberCount
        {
            get
            {
                int n = 0;
                foreach (KeyValuePair<int, List<PriorityStateSubscriber>> kv in _byTarget)
                    n += kv.Value.Count;
                return n;
            }
        }

        public void Subscribe(PriorityStateSubscriber s)
        {
            List<PriorityStateSubscriber> list;
            if (!_byTarget.TryGetValue(s.TargetInstanceId, out list))
            {
                list = new List<PriorityStateSubscriber>();
                _byTarget[s.TargetInstanceId] = list;
            }
            list.Add(s);
        }

        public bool Unsubscribe(int subscriberId)
        {
            foreach (KeyValuePair<int, List<PriorityStateSubscriber>> kv in _byTarget)
            {
                for (int i = 0; i < kv.Value.Count; i++)
                {
                    if (kv.Value[i].SubscriberId == subscriberId)
                    {
                        kv.Value.RemoveAt(i);
                        return true;
                    }
                }
            }
            return false;
        }

        public void Publish(StateChangeEvent e)
        {
            List<PriorityStateSubscriber> merged = new List<PriorityStateSubscriber>();
            Collect(e.InstanceId, merged);
            if (e.InstanceId != StateSubscriber.AnyTarget)
                Collect(StateSubscriber.AnyTarget, merged);
            merged.Sort(ComparePriority);
            for (int i = 0; i < merged.Count; i++)
            {
                PriorityStateSubscriber s = merged[i];
                if (!IsStillSubscribed(s)) continue;
                s.Notify(e);
                PriorityVerdict verdict = s.NextVerdict;
                s.NextVerdict = PriorityVerdict.ServeRest;
                if (verdict == PriorityVerdict.SkipRestThisTick) break;
                if (verdict == PriorityVerdict.DeregisterLower)
                    DeregisterLowerThan(s.Priority);
            }
        }

        private static int ComparePriority(PriorityStateSubscriber a,
                                           PriorityStateSubscriber b)
        {
            int c = b.Priority.CompareTo(a.Priority); // higher first
            if (c != 0) return c;
            return a.SubscribeSeq.CompareTo(b.SubscribeSeq); // ties: older first
        }

        private void Collect(int target, List<PriorityStateSubscriber> output)
        {
            List<PriorityStateSubscriber> list;
            if (_byTarget.TryGetValue(target, out list))
                output.AddRange(list);
        }

        private bool IsStillSubscribed(PriorityStateSubscriber s)
        {
            foreach (KeyValuePair<int, List<PriorityStateSubscriber>> kv in _byTarget)
            {
                if (kv.Value.Contains(s)) return true;
            }
            return false;
        }

        private void DeregisterLowerThan(int priority)
        {
            foreach (KeyValuePair<int, List<PriorityStateSubscriber>> kv in _byTarget)
            {
                for (int i = kv.Value.Count - 1; i >= 0; i--)
                {
                    if (kv.Value[i].Priority < priority)
                        kv.Value.RemoveAt(i);
                }
            }
        }
    }
}
