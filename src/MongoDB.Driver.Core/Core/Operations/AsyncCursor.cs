﻿/* Copyright 2013-2014 MongoDB Inc.
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
* http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver.Core.Bindings;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.Core.WireProtocol;
using MongoDB.Driver.Core.WireProtocol.Messages.Encoders;

namespace MongoDB.Driver.Core.Operations
{
    public class AsyncCursor<TDocument> : IAsyncCursor<TDocument>
    {
        // fields
        private readonly int _batchSize;
        private readonly CollectionNamespace _collectionNamespace;
        private readonly IChannelSource _channelSource;
        private int _count;
        private IReadOnlyList<TDocument> _currentBatch;
        private long _cursorId;
        private bool _disposed;
        private IReadOnlyList<TDocument> _firstBatch;
        private readonly int _limit;
        private readonly MessageEncoderSettings _messageEncoderSettings;
        private readonly BsonDocument _query;
        private readonly IBsonSerializer<TDocument> _serializer;

        // constructors
        public AsyncCursor(
            IChannelSource channelSource,
            CollectionNamespace collectionNamespace,
            BsonDocument query,
            IReadOnlyList<TDocument> firstBatch,
            long cursorId,
            int batchSize,
            int limit,
            IBsonSerializer<TDocument> serializer,
            MessageEncoderSettings messageEncoderSettings)
        {
            _channelSource = channelSource;
            _collectionNamespace = Ensure.IsNotNull(collectionNamespace, "collectionNamespace");
            _query = Ensure.IsNotNull(query, "query");
            _firstBatch = Ensure.IsNotNull(firstBatch, "firstBatch");
            _cursorId = cursorId;
            _batchSize = Ensure.IsGreaterThanOrEqualToZero(batchSize, "batchSize");
            _limit = Ensure.IsGreaterThanOrEqualToZero(limit, "limit");
            _serializer = Ensure.IsNotNull(serializer, "serializer");
            _messageEncoderSettings = messageEncoderSettings;

            if (_limit == 0)
            {
                _limit = int.MaxValue;
            }
            if (_firstBatch.Count > _limit)
            {
                _firstBatch = _firstBatch.Take(_limit).ToList();
            }
            _count = _firstBatch.Count;

            // if we aren't going to need the channel source we can go ahead and Dispose it now
            if (_cursorId == 0 && _channelSource != null)
            {
                _channelSource.Dispose();
                _channelSource = null;
            }
        }

        // properties
        public IEnumerable<TDocument> Current
        {
            get
            {
                ThrowIfDisposed();
                return _currentBatch;
            }
        }

        // methods
        private GetMoreWireProtocolArgs<TDocument> CreateGetMoreProtocolArgs()
        {
            return new GetMoreWireProtocolArgs<TDocument>(
                _collectionNamespace,
                _query,
                _cursorId,
                _batchSize,
                _serializer,
                _messageEncoderSettings);
        }

        private KillCursorsWireProtocolArgs CreateKillCursorsProtocolArgs()
        {
            return new KillCursorsWireProtocolArgs(new[] { _cursorId }, _messageEncoderSettings);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!_disposed)
                {
                    try
                    {
                        if (_cursorId != 0)
                        {
                            KillCursorAsync(_cursorId).GetAwaiter().GetResult();
                        }
                    }
                    catch
                    {
                        // ignore exceptions
                    }
                    if (_channelSource != null)
                    {
                        _channelSource.Dispose();
                    }
                }
            }
            _disposed = true;
        }

        private async Task<CursorBatch<TDocument>> GetNextBatchAsync(CancellationToken cancellationToken)
        {
            using (var channel = await _channelSource.GetChannelAsync(cancellationToken).ConfigureAwait(false))
            {
                var args = CreateGetMoreProtocolArgs();
                return await channel.GetMoreAsync(args, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task KillCursorAsync(long cursorId)
        {
            try
            {
                using (var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                using (var channel = await _channelSource.GetChannelAsync(cancellationTokenSource.Token).ConfigureAwait(false))
                {
                    var args = CreateKillCursorsProtocolArgs();
                    await channel.KillCursorAsync(args, cancellationTokenSource.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                // ignore exceptions
            }
        }

        public async Task<bool> MoveNextAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            if (_firstBatch != null)
            {
                _currentBatch = _firstBatch;
                _firstBatch = null;
                return true;
            }

            if (_currentBatch == null)
            {
                return false;
            }

            if (_cursorId == 0 || _count == _limit)
            {
                _currentBatch = null;
                return false;
            }

            var batch = await GetNextBatchAsync(cancellationToken).ConfigureAwait(false);
            var cursorId = batch.CursorId;
            var documents = batch.Documents;

            _count += documents.Count;
            if (_count > _limit)
            {
                var remove = _count - _limit;
                var take = documents.Count - remove;
                documents = documents.Take(take).ToList();
                _count = _limit;
            }

            _currentBatch = documents;
            _cursorId = cursorId;
            return true;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().Name);
            }
        }
    }
}
