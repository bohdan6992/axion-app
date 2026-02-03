using System;
using System.Collections.Concurrent;
using System.Linq;
using TradingBridgeApi.Options;
using Microsoft.Extensions.Options;

namespace TradingBridgeApi.Services.Tickerdays
{
    public sealed class TickerdaysJobStore
    {
        private readonly TickerdaysOptions _opt;

        private readonly ConcurrentDictionary<string, TickerdaysJobState> _jobsById = new();
        private readonly ConcurrentDictionary<string, string> _jobIdByHash = new();

        public TickerdaysJobStore(IOptions<TickerdaysOptions> opt)
        {
            _opt = opt.Value;
        }

        public TickerdaysJobState? Get(string requestId)
            => _jobsById.TryGetValue(requestId, out var j) ? j : null;

        public TickerdaysJobState CreateNew(string requestHash)
        {
            EvictIfNeeded();

            // If there is a previous job for the same hash, cancel it (replace policy)
            if (!string.IsNullOrWhiteSpace(requestHash) && _jobIdByHash.TryGetValue(requestHash, out var prevId))
            {
                var prev = Get(prevId);
                if (prev != null && prev.Status == TickerdaysJobStatus.Running)
                    Cancel(prevId, "Replaced by new request");
            }

            var id = Guid.NewGuid().ToString("N");
            var job = new TickerdaysJobState
            {
                RequestId = id,
                RequestHash = requestHash,
                Status = TickerdaysJobStatus.Running,
                Progress = 0,
                Message = "Queued…",
            };

            _jobsById[id] = job;

            if (!string.IsNullOrWhiteSpace(requestHash))
                _jobIdByHash[requestHash] = id;

            return job;
        }

        public TickerdaysJobState? TryGetByHash(string requestHash)
        {
            if (string.IsNullOrWhiteSpace(requestHash))
                return null;

            if (_jobIdByHash.TryGetValue(requestHash, out var id))
            {
                var j = Get(id);
                if (j != null) return j;

                // stale mapping
                _jobIdByHash.TryRemove(requestHash, out _);
            }

            return null;
        }

        public void UpdateProgress(string requestId, double progress, string message)
        {
            var j = Get(requestId);
            if (j == null) return;
            j.Progress = Clamp01(progress);
            j.Message = message;
            j.UpdatedUtc = DateTime.UtcNow;
        }

        public void Complete(string requestId, object result)
        {
            var j = Get(requestId);
            if (j == null) return;

            j.Status = TickerdaysJobStatus.Done;
            j.Progress = 1;
            j.Message = "Done";
            j.Result = result;
            j.UpdatedUtc = DateTime.UtcNow;
            // Keep hash mapping for caching Done results (intended behavior).
        }

        public void Fail(string requestId, string error)
        {
            var j = Get(requestId);
            if (j == null) return;

            j.Status = TickerdaysJobStatus.Error;
            j.Error = error;
            j.Message = "Error";
            j.UpdatedUtc = DateTime.UtcNow;

            // Do not keep hash mapping pointing to failed jobs.
            TryRemoveHashMappingIfPointsToThisJob(j);
        }

        public void Cancel(string requestId, string reason = "Cancelled")
        {
            var j = Get(requestId);
            if (j == null) return;

            // Don't cancel already completed jobs - preserve cache correctness.
            if (j.Status == TickerdaysJobStatus.Done)
                return;

            try { j.Cts.Cancel(); } catch { }

            j.Status = TickerdaysJobStatus.Cancelled;
            j.Message = reason;
            j.UpdatedUtc = DateTime.UtcNow;

            // Do not keep hash mapping pointing to cancelled jobs.
            TryRemoveHashMappingIfPointsToThisJob(j);
        }

        public object? TryGetResult(string requestId)
        {
            var j = Get(requestId);
            if (j == null) return null;
            return j.Status == TickerdaysJobStatus.Done ? j.Result : null;
        }

        private void TryRemoveHashMappingIfPointsToThisJob(TickerdaysJobState j)
        {
            if (string.IsNullOrWhiteSpace(j.RequestHash))
                return;

            if (_jobIdByHash.TryGetValue(j.RequestHash, out var mappedId) &&
                string.Equals(mappedId, j.RequestId, StringComparison.OrdinalIgnoreCase))
            {
                _jobIdByHash.TryRemove(j.RequestHash, out _);
            }
        }

        private void EvictIfNeeded()
        {
            // TTL eviction
            var ttl = TimeSpan.FromMinutes(Math.Max(5, _opt.Jobs.TtlMinutes));
            var now = DateTime.UtcNow;

            foreach (var kv in _jobsById)
            {
                var j = kv.Value;
                if (now - j.UpdatedUtc > ttl)
                {
                    _jobsById.TryRemove(kv.Key, out _);
                    if (!string.IsNullOrWhiteSpace(j.RequestHash))
                    {
                        // remove hash mapping only if it points to this job
                        if (_jobIdByHash.TryGetValue(j.RequestHash, out var mappedId) &&
                            string.Equals(mappedId, j.RequestId, StringComparison.OrdinalIgnoreCase))
                        {
                            _jobIdByHash.TryRemove(j.RequestHash, out _);
                        }
                    }
                }
            }

            // max results cap
            var max = Math.Max(5, _opt.Jobs.MaxResultsInMemory);
            var done = _jobsById.Values
                .Where(x => x.Status != TickerdaysJobStatus.Running)
                .OrderBy(x => x.UpdatedUtc)
                .ToList();

            while (done.Count > max)
            {
                var oldest = done[0];
                done.RemoveAt(0);
                _jobsById.TryRemove(oldest.RequestId, out _);

                if (!string.IsNullOrWhiteSpace(oldest.RequestHash))
                {
                    if (_jobIdByHash.TryGetValue(oldest.RequestHash, out var mappedId) &&
                        string.Equals(mappedId, oldest.RequestId, StringComparison.OrdinalIgnoreCase))
                    {
                        _jobIdByHash.TryRemove(oldest.RequestHash, out _);
                    }
                }
            }
        }

        private static double Clamp01(double x) => x < 0 ? 0 : (x > 1 ? 1 : x);
    }
}
