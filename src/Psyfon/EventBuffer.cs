﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Azure.EventHubs;
using System.Linq;
using System.Threading.Tasks;

namespace Psyfon
{
    public class EventBuffer : IDisposable
    {
        private ConcurrentQueue<Tuple<EventData, string>> _queue = new ConcurrentQueue<Tuple<EventData, string>>();
        private const int DefaultBatchSize = 128 * 1024; // 128KB
        private readonly IHasher _hasher;
        private readonly IEventHubClientWrapper _client;
        private readonly int _batchBufferSize;
        private readonly int _partitionCount;
        private bool _isAccepting = true;
        private Thread _worker;
        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private ConcurrentDictionary<string, Lazy<PartitionCommitter>> _committers = new ConcurrentDictionary<string, Lazy<PartitionCommitter>>();


        public EventBuffer(string connectionString,
            int batchBufferSize = DefaultBatchSize,
            IHasher hasher = null):
            this(new DefaultWrapper(EventHubClient.CreateFromConnectionString(connectionString)), batchBufferSize, hasher)
        {
        }

        public EventBuffer(IEventHubClientWrapper client,
            int batchBufferSize = DefaultBatchSize,
            IHasher hasher = null
            )
        {
            _hasher = hasher ?? new Md5Hasher();
            _client = client;
            _batchBufferSize = batchBufferSize;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="event"></param>
        /// <param name="partitionKey"></param>
        /// <returns>Whether message was accepted</returns>
        public bool Add(EventData @event, string partitionKey = null)
        {
            if(_isAccepting)
                _queue.Enqueue(new Tuple<EventData, string>(@event, partitionKey));

            return _isAccepting;
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="events"></param>
        /// <param name="partitionKey"></param>
        /// <returns>Whether message was accepted. If one is not accepted it will be false.</returns>
        public bool Add(IEnumerable<EventData> events, string partitionKey = null)
        {
            return events.Select(x => Add(x, partitionKey)).Aggregate((a,b) => a && b);
        }

        private void Work()
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                Tuple<EventData, string> data;
                if(_queue.TryDequeue(out data))
                {
                    var pk = _hasher.Hash(data.Item2 ?? Guid.NewGuid().ToString(), _partitionCount);
                    var committer = _committers.GetOrAdd(pk,
                        new Lazy<PartitionCommitter>(() => new PartitionCommitter(_client, _batchBufferSize, pk)));
                    committer.Value.Add(data.Item1);
                }
                else
                {
                    Task.Delay(100, _cancellationTokenSource.Token).GetAwaiter().GetResult();
                }
            }
        }

        public void Dispose()
        {
            _isAccepting = false;
            _cancellationTokenSource.Cancel();
            foreach (var kv in _committers)
            {
                kv.Value.Value.Dispose();
            }

            _client.Dispose();
        }

        public void Start()
        {
            _worker = new Thread(Work);
            _worker.Start();
        }
    }
}
