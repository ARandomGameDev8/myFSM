// myFSM Unity Runtime — query server: an OS-scheduler-like service desk.
//
// External systems use the AI system as a service through here: they submit
// DB queries or main-server commands and collect responses. Scheduling:
//
// - every client owns an internal FIFO request queue capped at 15; a capped
//   client cannot enqueue until it drops under the cap;
// - the long-term queue holds unique waiting clients (no duplicates), capped;
// - the ready scheduler holds N of them and solves at most M requests per
//   client per tick, round-robin. A client with 0 requests is popped and the
//   next long-term client fills its spot.
// - near/far rule: requests a client adds *while its own slice is running*
//   ("close") land in a pending buffer flushed next tick; requests added at
//   any other time ("far") join the internal queue immediately and may run
//   later in the same tick.
// - 1 <= M <= 3 (default 2). min 8 <= soft 15 <= N <= hard 20: congestion
//   pulls N toward the upper bound, idleness toward the lower bound.
//   Shrinking drains the tail: the last (N - NewN) ready clients are served
//   at most M requests, then popped next round.
// - starvation guard: under congestion, a back-half long-term client that has
//   waited too long is evicted and all its requests fail fast instead of
//   wedging the queue.

using System;
using System.Collections.Generic;

namespace MyFSM.Core
{
    // ------------------------------------------------------------------
    // Request / response design
    // ------------------------------------------------------------------

    /// <summary>Read-only DB queries (never mutate AI state).</summary>
    public enum QueryCode
    {
        GetInstanceState = 1, // target instance -> Text = head state name
        ListInstances = 2,    // -> Lines, one per instance
        GetAssetInfo = 3,     // TargetAsset -> Lines (states, slots, counts)
        ListAssets = 4,       // -> Lines, one per asset
        GetStateHistory = 5,  // TargetInstanceId (-1 = all), IntArg = max rows
        GetVariable = 6,      // TargetInstanceId + StringArg = var name -> Value
        GetStats = 7,         // -> Lines (totals + per-instance counters)
        GetEmits = 8          // IntArg = max rows -> Lines, newest first
    }

    /// <summary>Validated commands applied to the main server / AIs.</summary>
    public enum CommandCode
    {
        TransitionTo = 101, // TargetInstanceId + StringArg = state name
        SetVariable = 102,  // TargetInstanceId + StringArg = var name + ValueArg
        Emit = 103,         // TargetInstanceId + IntArg = event id
        PauseAI = 104,      // TargetInstanceId
        ResumeAI = 105,     // TargetInstanceId
        ReloadModule = 106  // TargetAsset + BytesArg = new .fsmb (schema-checked)
    }

    public sealed class ServerRequest
    {
        public long Sequence;
        public int ClientId;
        public bool IsCommand;
        public int Code;
        public int TargetInstanceId = -1;
        public string TargetAsset;
        public string StringArg;
        public FsmValue ValueArg;
        public int IntArg;
        public byte[] BytesArg;
        public long EnqueuedTick;
    }

    public sealed class ServerResponse
    {
        public long Sequence;
        public int ClientId;
        public bool Ok;
        public string Error;
        public string Text;
        public FsmValue Value;
        public List<string> Lines;

        public static ServerResponse Fail(ServerRequest req, string error)
        {
            return new ServerResponse
            {
                Sequence = req.Sequence,
                ClientId = req.ClientId,
                Ok = false,
                Error = error
            };
        }

        public static ServerResponse Succeed(ServerRequest req, string text)
        {
            return new ServerResponse
            {
                Sequence = req.Sequence,
                ClientId = req.ClientId,
                Ok = true,
                Text = text
            };
        }
    }

    /// <summary>Implemented by the main server: runs one request.</summary>
    public interface IQueryBackend
    {
        ServerResponse Execute(ServerRequest request);
    }

    // ------------------------------------------------------------------
    // Client
    // ------------------------------------------------------------------

    public sealed class QueryClient
    {
        public const int MaxInternalRequests = 15;

        public readonly int ClientId;
        public readonly string Name;

        private readonly Queue<ServerRequest> _internal = new Queue<ServerRequest>();
        private readonly Queue<ServerRequest> _pending = new Queue<ServerRequest>();
        private readonly Queue<ServerResponse> _responses = new Queue<ServerResponse>();

        public QueryClient(int clientId, string name)
        {
            ClientId = clientId;
            Name = name ?? ("client-" + clientId);
        }

        public int InternalCount { get { return _internal.Count; } }
        public int PendingCount { get { return _pending.Count; } }
        public int ResponseCount { get { return _responses.Count; } }

        /// <returns>False when the client is at cap (caller must retry later).</returns>
        public bool TryEnqueue(ServerRequest req, bool defer)
        {
            if (_internal.Count + _pending.Count >= MaxInternalRequests) return false;
            if (defer) _pending.Enqueue(req);
            else _internal.Enqueue(req);
            return true;
        }

        public void FlushPending()
        {
            while (_pending.Count > 0) _internal.Enqueue(_pending.Dequeue());
        }

        public bool TryDequeue(out ServerRequest req)
        {
            if (_internal.Count == 0)
            {
                req = null;
                return false;
            }
            req = _internal.Dequeue();
            return true;
        }

        public void PushResponse(ServerResponse r)
        {
            _responses.Enqueue(r);
        }

        public bool TryTakeResponse(out ServerResponse r)
        {
            if (_responses.Count == 0)
            {
                r = null;
                return false;
            }
            r = _responses.Dequeue();
            return true;
        }

        /// <summary>Fails every queued request (starvation eviction).</summary>
        public void FailAll(string error)
        {
            ServerRequest req;
            while (TryDequeue(out req)) PushResponse(ServerResponse.Fail(req, error));
            while (_pending.Count > 0)
                PushResponse(ServerResponse.Fail(_pending.Dequeue(), error));
        }
    }

    // ------------------------------------------------------------------
    // Server
    // ------------------------------------------------------------------

    public sealed class QueryServer
    {
        public const int MinClients = 8;
        public const int SoftMaxClients = 15;
        public const int HardMaxClients = 20;
        public const int MinRequestsPerClient = 1;
        public const int MaxRequestsPerClient = 3;
        public const int LongTermCap = 64;
        public const long StarvationTicks = 300;

        private readonly IQueryBackend _backend;
        private readonly Dictionary<int, QueryClient> _clients =
            new Dictionary<int, QueryClient>();
        private readonly Queue<int> _longTerm = new Queue<int>();
        private readonly HashSet<int> _longTermSet = new HashSet<int>();
        private readonly Dictionary<int, long> _longTermSince =
            new Dictionary<int, long>();
        private readonly List<int> _ready = new List<int>();
        private readonly HashSet<int> _draining = new HashSet<int>();
        private int _rrCursor;
        private int _servingClient = -1;
        private int _requestsPerClient = 2;
        private int _readyTarget = SoftMaxClients;
        private int _nextClientId = 1;
        private long _sequence;

        public long TickCount { get; private set; }
        public int ReadyTarget { get { return _readyTarget; } }
        public int ReadyCount { get { return _ready.Count; } }
        public int LongTermCount { get { return _longTerm.Count; } }

        public int RequestsPerClient
        {
            get { return _requestsPerClient; }
            set
            {
                if (value < MinRequestsPerClient) value = MinRequestsPerClient;
                if (value > MaxRequestsPerClient) value = MaxRequestsPerClient;
                _requestsPerClient = value;
            }
        }

        public bool Congested { get { return _longTerm.Count > _readyTarget; } }

        public QueryServer(IQueryBackend backend)
        {
            _backend = backend;
        }

        public QueryClient RegisterClient(string name)
        {
            QueryClient c = new QueryClient(_nextClientId++, name);
            _clients[c.ClientId] = c;
            return c;
        }

        public bool TryGetClient(int clientId, out QueryClient client)
        {
            return _clients.TryGetValue(clientId, out client);
        }

        /// <summary>
        /// Submits a request. Returns false (with an immediate failure
        /// response pushed) when the client is at cap or the server refuses
        /// new long-term clients under heavy congestion.
        /// </summary>
        public bool Enqueue(int clientId, ServerRequest req)
        {
            QueryClient client;
            if (!_clients.TryGetValue(clientId, out client)) return false;
            bool tracked = _ready.Contains(clientId) || _longTermSet.Contains(clientId);
            // Refuse untracked clients up front under heavy congestion so a
            // rejected request is never half-queued.
            if (!tracked && _longTerm.Count >= LongTermCap)
            {
                req.Sequence = ++_sequence;
                req.ClientId = clientId;
                req.EnqueuedTick = TickCount;
                client.PushResponse(ServerResponse.Fail(req,
                    "server congested: long-term queue is full"));
                return false;
            }
            req.Sequence = ++_sequence;
            req.ClientId = clientId;
            req.EnqueuedTick = TickCount;
            // Near/far rule: enqueues from inside your own slice wait a tick.
            bool defer = clientId == _servingClient;
            if (!client.TryEnqueue(req, defer))
            {
                client.PushResponse(ServerResponse.Fail(req, "client queue is capped at " +
                    QueryClient.MaxInternalRequests + "; retry when drained"));
                return false;
            }
            if (!tracked)
            {
                _longTerm.Enqueue(clientId);
                _longTermSet.Add(clientId);
                _longTermSince[clientId] = TickCount;
            }
            return true;
        }

        public void Tick()
        {
            TickCount++;

            // 1. Pending ("close") requests join their internal queues.
            foreach (KeyValuePair<int, QueryClient> kv in _clients)
                kv.Value.FlushPending();

            // 2. Adapt N: congestion pulls toward the hard max, idleness
            // toward the minimum. Two steps per tick keeps it stable.
            if (Congested && _readyTarget < HardMaxClients)
                _readyTarget = Math.Min(HardMaxClients, _readyTarget + 2);
            else if (_longTerm.Count == 0 && _ready.Count < SoftMaxClients &&
                     _readyTarget > MinClients)
                _readyTarget = Math.Max(MinClients, _readyTarget - 2);

            // 3. Mark the shrink tail draining: last (ready - N) entries get
            // at most M requests this round, then pop next round.
            _draining.Clear();
            if (_ready.Count > _readyTarget)
            {
                for (int i = _readyTarget; i < _ready.Count; i++)
                    _draining.Add(_ready[i]);
            }

            // 4. Serve every ready client once (round-robin start), at most M.
            List<int> finished = new List<int>();
            int count = _ready.Count;
            for (int s = 0; s < count; s++)
            {
                int entry = _ready[(_rrCursor + s) % count];
                QueryClient client;
                if (!_clients.TryGetValue(entry, out client))
                {
                    finished.Add(entry);
                    continue;
                }
                _servingClient = entry;
                try
                {
                    for (int k = 0; k < _requestsPerClient; k++)
                    {
                        ServerRequest req;
                        if (!client.TryDequeue(out req)) break;
                        ServerResponse resp;
                        try
                        {
                            resp = _backend.Execute(req) ??
                                   ServerResponse.Fail(req, "backend returned nothing");
                        }
                        catch (Exception ex)
                        {
                            resp = ServerResponse.Fail(req, "backend error: " + ex.Message);
                        }
                        resp.Sequence = req.Sequence;
                        resp.ClientId = req.ClientId;
                        client.PushResponse(resp);
                    }
                }
                finally
                {
                    _servingClient = -1;
                }
                if (_draining.Contains(entry)) finished.Add(entry);
                else if (client.InternalCount == 0 && client.PendingCount == 0)
                    finished.Add(entry); // 0 requests: popped, spot refilled below
            }
            for (int i = 0; i < finished.Count; i++)
            {
                _ready.Remove(finished[i]);
                // A shrink-tail client popped with work left rejoins the
                // long-term queue — otherwise its requests would sit unserved
                // until the client happens to enqueue again.
                if (_draining.Contains(finished[i]))
                {
                    QueryClient leftover;
                    if (_clients.TryGetValue(finished[i], out leftover) &&
                        leftover.InternalCount + leftover.PendingCount > 0)
                    {
                        _longTerm.Enqueue(finished[i]);
                        _longTermSet.Add(finished[i]);
                        _longTermSince[finished[i]] = TickCount;
                    }
                }
            }
            if (_ready.Count > 0)
                _rrCursor = (_rrCursor + 1) % _ready.Count;
            else
                _rrCursor = 0;

            // 5. Refill from the long-term queue up to N.
            while (_ready.Count < _readyTarget && _longTerm.Count > 0)
            {
                int next = _longTerm.Dequeue();
                _longTermSet.Remove(next);
                _longTermSince.Remove(next);
                QueryClient client;
                if (!_clients.TryGetValue(next, out client)) continue;
                if (client.InternalCount + client.PendingCount == 0) continue;
                _ready.Add(next);
            }

            // 6. Starvation guard under congestion: back-half clients that
            // waited too long opt off with failure instead of wedging.
            if (Congested && _longTerm.Count > 0)
            {
                List<int> snapshot = new List<int>(_longTerm);
                int middle = snapshot.Count / 2;
                List<int> evicted = new List<int>();
                for (int i = middle; i < snapshot.Count; i++)
                {
                    long since;
                    if (_longTermSince.TryGetValue(snapshot[i], out since) &&
                        TickCount - since > StarvationTicks)
                    {
                        evicted.Add(snapshot[i]);
                    }
                }
                if (evicted.Count > 0)
                {
                    _longTerm.Clear();
                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        if (evicted.Contains(snapshot[i]))
                        {
                            QueryClient client;
                            if (_clients.TryGetValue(snapshot[i], out client))
                                client.FailAll("evicted: server congested, request starved");
                            _longTermSet.Remove(snapshot[i]);
                            _longTermSince.Remove(snapshot[i]);
                        }
                        else _longTerm.Enqueue(snapshot[i]);
                    }
                }
            }
        }
    }
}
