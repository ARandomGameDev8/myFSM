// myFSM Unity Runtime — per-AI handle table.
//
// DSL handle values (Object3D, NavMeshAgent, ...) are (type tag, handle id)
// pairs. Each AI instance owns one table mapping its ids to Unity objects.
// Id 0 is the reserved null handle. Bindings (inspector or code) allocate ids;
// functions like getRaycastHit allocate new ones for discovered objects.

using System;
using UnityEngine;
using MyFSM.Core;

namespace MyFSM.Unity
{
    public sealed class HandleTable
    {
        private readonly System.Collections.Generic.List<UnityEngine.Object> _objects =
            new System.Collections.Generic.List<UnityEngine.Object>();
        private readonly System.Collections.Generic.List<byte> _tags =
            new System.Collections.Generic.List<byte>();

        public HandleTable()
        {
            _objects.Add(null); // id 0: null handle
            _tags.Add(0);
        }

        public int Count { get { return _objects.Count - 1; } }

        public int Alloc(UnityEngine.Object obj, byte tag)
        {
            _objects.Add(obj);
            _tags.Add(tag);
            return _objects.Count - 1;
        }

        /// <summary>Resolves an id, or null for null/destroyed/out-of-range.</summary>
        public UnityEngine.Object Resolve(int handleId)
        {
            if (handleId <= 0 || handleId >= _objects.Count) return null;
            UnityEngine.Object o = _objects[handleId];
            // In Unity, a destroyed object compares == null; in plain C# (the
            // sandbox harness) this is a normal null check. Both are correct.
            if (o == null) return null;
            return o;
        }

        public byte TagOf(int handleId)
        {
            if (handleId <= 0 || handleId >= _tags.Count) return 0;
            return _tags[handleId];
        }

        public void Clear()
        {
            _objects.Clear();
            _tags.Clear();
            _objects.Add(null);
            _tags.Add(0);
        }
    }
}
